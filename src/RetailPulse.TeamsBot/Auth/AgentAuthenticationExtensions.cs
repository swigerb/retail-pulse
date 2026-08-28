using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.Agents.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;

namespace RetailPulse.TeamsBot.Auth;

/// <summary>
/// Registers JWT bearer validation for inbound Activities from Azure Bot Service.
/// </summary>
/// <remarks>
/// The Microsoft 365 Agents SDK does not ship this in any NuGet package. It lives as a
/// shared helper at <c>microsoft/Agents-for-net: src/samples/Shared/AspNetExtensions.cs</c>
/// which the sample projects link rather than copy, so a standalone project has to carry
/// its own. Without it, <c>MapAgentApplicationEndpoints(requireAuth: true)</c> maps an
/// endpoint that requires authorization while no authentication scheme exists, and every
/// inbound Activity fails with "No authenticationScheme was specified" — a 500 where the
/// channel expects a 401.
///
/// The logic mirrors the upstream helper. The two-issuer split is the crux: Azure Bot
/// Service mints channel tokens signed with keys published at login.botframework.com,
/// while Entra mints agent-to-agent tokens signed with keys from login.microsoftonline.com.
/// A single ConfigurationManager cannot serve both, so the issuer is peeked before
/// validation and the matching metadata endpoint is selected per request.
/// </remarks>
public static class AgentAuthenticationExtensions
{
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _openIdMetadataCache = new();

    private static bool IsBotFrameworkIssuer(string issuer) =>
        AuthenticationConstants.BotFrameworkTokenIssuer.Equals(issuer, StringComparison.OrdinalIgnoreCase)
        || AuthenticationConstants.GovBotFrameworkTokenIssuer.Equals(issuer, StringComparison.OrdinalIgnoreCase);

    public static void AddAgentAspNetAuthentication(
        this IHostApplicationBuilder builder,
        string tokenValidationSectionName = "TokenValidation")
    {
        IConfigurationSection section = builder.Configuration.GetSection(tokenValidationSectionName);

        if (!section.Exists())
        {
            throw new InvalidOperationException(
                $"Configuration section '{tokenValidationSectionName}' is missing. The bot cannot validate " +
                "inbound Activities without it. Supply TokenValidation:Audiences (the bot's app id) and " +
                "TokenValidation:TenantId.");
        }

        builder.Services.AddAgentAspNetAuthentication(section.Get<TokenValidationOptions>()!);
    }

    public static void AddAgentAspNetAuthentication(
        this IServiceCollection services,
        TokenValidationOptions validationOptions)
    {
        ArgumentNullException.ThrowIfNull(validationOptions);

        if (validationOptions.Audiences is null || validationOptions.Audiences.Count == 0)
            throw new ArgumentException("TokenValidation:Audiences requires at least one ClientId.", nameof(validationOptions));

        foreach (string audience in validationOptions.Audiences)
        {
            if (!Guid.TryParse(audience, out _))
                throw new ArgumentException("TokenValidation:Audiences values must be GUIDs.", nameof(validationOptions));
        }

        if (validationOptions.ValidIssuers is null || validationOptions.ValidIssuers.Count == 0)
        {
            validationOptions.ValidIssuers = BuildDefaultIssuers(validationOptions);
        }

        if (string.IsNullOrEmpty(validationOptions.AzureBotServiceOpenIdMetadataUrl))
        {
            validationOptions.AzureBotServiceOpenIdMetadataUrl = validationOptions.IsGov
                ? AuthenticationConstants.GovAzureBotServiceOpenIdMetadataUrl
                : AuthenticationConstants.PublicAzureBotServiceOpenIdMetadataUrl;
        }

        if (string.IsNullOrEmpty(validationOptions.OpenIdMetadataUrl))
        {
            validationOptions.OpenIdMetadataUrl = validationOptions.IsGov
                ? AuthenticationConstants.GovOpenIdMetadataUrl
                : AuthenticationConstants.PublicOpenIdMetadataUrl;
        }

        TimeSpan refresh = validationOptions.OpenIdMetadataRefresh
            ?? BaseConfigurationManager.DefaultAutomaticRefreshInterval;

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
                ValidIssuers = validationOptions.ValidIssuers,
                ValidAudiences = validationOptions.Audiences,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
            };

            options.TokenValidationParameters.EnableAadSigningKeyIssuerValidation();

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    string authorizationHeader = context.Request.Headers.Authorization.ToString();
                    string[] parts = authorizationHeader.Split(' ');

                    if (parts.Length != 2 || !string.Equals(parts[0], "Bearer", StringComparison.Ordinal))
                    {
                        // Nothing to route on; leave whatever manager is already configured.
                        context.Options.TokenValidationParameters.ConfigurationManager
                            ??= options.ConfigurationManager as BaseConfigurationManager;
                        return Task.CompletedTask;
                    }

                    // Read the issuer WITHOUT validating — this only selects which key set
                    // to validate against. The signature check still happens afterwards.
                    string issuer = new JsonWebToken(parts[1]).Issuer;

                    string metadataUrl = validationOptions.AzureBotServiceTokenHandling && IsBotFrameworkIssuer(issuer)
                        ? validationOptions.AzureBotServiceOpenIdMetadataUrl
                        : validationOptions.OpenIdMetadataUrl;

                    context.Options.TokenValidationParameters.ConfigurationManager =
                        _openIdMetadataCache.GetOrAdd(metadataUrl, url =>
                            new ConfigurationManager<OpenIdConnectConfiguration>(
                                url,
                                new OpenIdConnectConfigurationRetriever(),
                                new HttpClient())
                            {
                                AutomaticRefreshInterval = refresh,
                            });

                    return Task.CompletedTask;
                },

                OnTokenValidated = context =>
                {
                    string? issuer = context.Principal?.FindFirst("iss")?.Value;
                    bool isBotFrameworkToken = validationOptions.AzureBotServiceTokenHandling
                        && issuer is not null
                        && IsBotFrameworkIssuer(issuer);

                    // Bot Framework tokens carry no tenant claim, so the cross-check below
                    // only applies to Entra-issued agent-to-agent tokens.
                    if (!isBotFrameworkToken
                        && context.Principal?.Identity is ClaimsIdentity identity
                        && !IsTenantIdIssuerValid(identity, issuer))
                    {
                        context.Fail("Token tenant ID does not match its issuer.");
                        return Task.CompletedTask;
                    }

                    if (!isBotFrameworkToken
                        && validationOptions.AllowedCallers is { Count: > 0 }
                        && !validationOptions.AllowedCallers.Any(c => c.Equals("*", StringComparison.Ordinal)))
                    {
                        string? callerAppId = context.Principal?.FindFirst("azp")?.Value
                            ?? context.Principal?.FindFirst("appid")?.Value;

                        if (string.IsNullOrEmpty(callerAppId)
                            || !validationOptions.AllowedCallers.Any(c => c.Equals(callerAppId, StringComparison.OrdinalIgnoreCase)))
                        {
                            context.Fail($"Caller App ID '{callerAppId}' is not in the AllowedCallers list.");
                        }
                    }

                    return Task.CompletedTask;
                },
            };
        });
    }

    /// <summary>
    /// Public-cloud defaults are the Bot Framework issuer plus the three Entra tenants
    /// Azure Bot Service itself issues from, and — when the bot is single-tenant — its own
    /// tenant in both v1 and v2 issuer forms.
    /// </summary>
    private static List<string> BuildDefaultIssuers(TokenValidationOptions o)
    {
        if (o.AzureBotServiceOnly)
        {
            return [o.IsGov ? AuthenticationConstants.GovBotFrameworkTokenIssuer : AuthenticationConstants.BotFrameworkTokenIssuer];
        }

        List<string> issuers = o.IsGov
            ?
            [
                AuthenticationConstants.GovBotFrameworkTokenIssuer,
                "https://sts.windows.net/cab8a31a-1906-4287-a0d8-4eef66b95f6e/",
                "https://login.microsoftonline.us/cab8a31a-1906-4287-a0d8-4eef66b95f6e/v2.0",
            ]
            :
            [
                AuthenticationConstants.BotFrameworkTokenIssuer,
                "https://sts.windows.net/d6d49420-f39b-4df7-a1dc-d59a935871db/",
                "https://login.microsoftonline.com/d6d49420-f39b-4df7-a1dc-d59a935871db/v2.0",
                "https://sts.windows.net/f8cdef31-a31e-4b4a-93e4-5f571e91255a/",
                "https://login.microsoftonline.com/f8cdef31-a31e-4b4a-93e4-5f571e91255a/v2.0",
                "https://sts.windows.net/69e9b82d-4842-4902-8d1e-abc5b98a55e8/",
                "https://login.microsoftonline.com/69e9b82d-4842-4902-8d1e-abc5b98a55e8/v2.0",
            ];

        if (!string.IsNullOrEmpty(o.TenantId) && Guid.TryParse(o.TenantId, out _))
        {
            issuers.Add(FormatIssuer(AuthenticationConstants.ValidTokenIssuerUrlTemplateV1, o.TenantId));
            issuers.Add(FormatIssuer(
                o.IsGov ? AuthenticationConstants.ValidGovernmentTokenIssuerUrlTemplateV2 : AuthenticationConstants.ValidTokenIssuerUrlTemplateV2,
                o.TenantId));
        }

        return issuers;
    }

    /// <summary>
    /// The issuer templates are SDK constants selected at runtime, so a cached
    /// <see cref="CompositeFormat"/> per template keeps the analyzer satisfied without
    /// hardcoding URLs the SDK owns. This runs once at startup, not per request.
    /// </summary>
    private static readonly ConcurrentDictionary<string, CompositeFormat> _issuerFormats = new();

    private static string FormatIssuer(string template, string tenantId) =>
        string.Format(
            CultureInfo.InvariantCulture,
            _issuerFormats.GetOrAdd(template, CompositeFormat.Parse),
            tenantId);

    /// <summary>
    /// Confirms the token's <c>tid</c> claim matches the tenant embedded in its issuer.
    /// </summary>
    /// <remarks>
    /// Upstream calls <c>ClaimsIdentity.IsTenantIdIssuerValid()</c> from a newer
    /// Microsoft.IdentityModel.Validators than this solution pins, so the check is written
    /// out here rather than taking a version bump on a security-critical package.
    ///
    /// Without it, a validly signed token from one tenant could satisfy an issuer entry for
    /// another, because the issuer list and the tenant claim would be checked independently.
    /// Both Entra issuer forms embed the tenant GUID as a path segment:
    /// <c>https://sts.windows.net/{tid}/</c> and
    /// <c>https://login.microsoftonline.com/{tid}/v2.0</c>.
    /// </remarks>
    private static bool IsTenantIdIssuerValid(ClaimsIdentity identity, string? issuer)
    {
        string? tenantId = identity.FindFirst("tid")?.Value
            ?? identity.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

        // No tenant claim means there is nothing to cross-check; the issuer allow-list and
        // signature validation already applied.
        return string.IsNullOrEmpty(tenantId)
            || (!string.IsNullOrEmpty(issuer)
                && Uri.TryCreate(issuer, UriKind.Absolute, out Uri? issuerUri)
                && issuerUri.Segments.Any(segment =>
                    segment.Trim('/').Equals(tenantId, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Settings read from the <c>TokenValidation</c> configuration section.</summary>
    public class TokenValidationOptions
    {
        /// <summary>Accepted audiences. Must contain the bot's Entra app (client) id.</summary>
        public IList<string>? Audiences { get; set; }

        /// <summary>Tenant that owns the bot registration. Adds that tenant's issuers.</summary>
        public string? TenantId { get; set; }

        public IList<string>? ValidIssuers { get; set; }

        public bool IsGov { get; set; }

        /// <summary>Restricts validation to Bot Framework channel tokens only.</summary>
        public bool AzureBotServiceOnly { get; set; }

        public string? AzureBotServiceOpenIdMetadataUrl { get; set; }

        public string? OpenIdMetadataUrl { get; set; }

        /// <summary>
        /// Route Bot Framework issuers to the Bot Framework metadata endpoint. Must stay on
        /// while Azure Bot Service still issues its own channel tokens.
        /// </summary>
        public bool AzureBotServiceTokenHandling { get; set; } = true;

        public TimeSpan? OpenIdMetadataRefresh { get; set; }

        /// <summary>App ids permitted to call this agent. Null or "*" accepts any.</summary>
        public IList<string>? AllowedCallers { get; set; }
    }
}
