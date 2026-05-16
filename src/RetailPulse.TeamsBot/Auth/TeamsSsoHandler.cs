using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace RetailPulse.TeamsBot.Auth;

public class TeamsSsoHandler
{
    private readonly ILogger<TeamsSsoHandler> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _oidcConfigManager;
    private readonly string? _configuredTenantId;
    private readonly bool _strictTenantValidation;

    public TeamsSsoHandler(ILogger<TeamsSsoHandler> logger, IConfiguration configuration, IHostEnvironment environment)
    {
        _logger = logger;
        _configuration = configuration;
        _environment = environment;

        _configuredTenantId = _configuration["MicrosoftEntra:TenantId"];
        bool isDevelopment = _environment.IsDevelopment();

        // Require explicit tenant config outside Development
        if (string.IsNullOrEmpty(_configuredTenantId) && !isDevelopment)
        {
            throw new InvalidOperationException(
                "MicrosoftEntra:TenantId is required in non-development environments");
        }

        // Strict validation defaults to true in Production, false in Development
        string? strictConfigValue = _configuration["MicrosoftEntra:StrictTenantValidation"];
        _strictTenantValidation = strictConfigValue != null
            ? bool.Parse(strictConfigValue)
            : !isDevelopment;

        string metadataTenantId = _configuredTenantId ?? "common";
        string metadataAddress = $"https://login.microsoftonline.com/{metadataTenantId}/v2.0/.well-known/openid-configuration";
        _oidcConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever());
    }

    /// <summary>
    /// Extracts a Teams SSO identity from the inbound activity, validating the
    /// token signature, issuer, audience, and tenant against Microsoft Entra ID OIDC metadata.
    /// </summary>
    public async Task<UserIdentity?> ExtractUserIdentityAsync(IActivity activity)
    {
        try
        {
            // Extract SSO token from Teams activity
            string? token = GetSsoTokenFromActivity(activity);

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("No SSO token found in activity");
                return null;
            }

            string botClientId = _configuration["MicrosoftEntra:ClientId"]
                ?? throw new InvalidOperationException("MicrosoftEntra:ClientId must be configured for token validation.");

            OpenIdConnectConfiguration oidcConfig = await _oidcConfigManager.GetConfigurationAsync(CancellationToken.None);

            string[] validIssuers = BuildValidIssuers();

            var handler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateLifetime = true,
                ValidateIssuer = true,
                ValidIssuers = validIssuers,
                ValidateAudience = true,
                ValidAudiences = [botClientId],
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfig.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            SecurityToken validatedToken;
            try
            {
                handler.ValidateToken(token, validationParams, out validatedToken);
            }
            catch (SecurityTokenException stex)
            {
                _logger.LogWarning(stex, "SSO token failed validation");
                return null;
            }

            var jwtToken = (JwtSecurityToken)validatedToken;

            // Validate tid claim against configured tenant
            string? userTenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
            if (!ValidateTenantClaim(userTenantId))
            {
                return null;
            }

            // Extract claims
            string? oid = jwtToken.Claims.FirstOrDefault(c => c.Type == "oid")?.Value;
            string? name = jwtToken.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            string? email = jwtToken.Claims.FirstOrDefault(c => c.Type is "preferred_username" or "upn")?.Value;

            if (string.IsNullOrEmpty(oid))
            {
                _logger.LogWarning("No object ID found in token");
                return null;
            }

            _logger.LogInformation("Successfully extracted user identity: {Name} ({Email})",
                PrivacyRedactor.RedactName(name), PrivacyRedactor.RedactEmail(email));

            return new UserIdentity
            {
                ObjectId = oid,
                DisplayName = name ?? "Unknown User",
                Email = email ?? string.Empty,
                TenantId = userTenantId ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract user identity from activity");
            return null;
        }
    }

    /// <summary>
    /// Builds the set of valid issuers based on environment and tenant configuration.
    /// Only includes the common issuer in Development when no tenant is configured.
    /// </summary>
    internal string[] BuildValidIssuers()
    {
        if (!string.IsNullOrEmpty(_configuredTenantId))
        {
            // Tenant-specific issuers only — no common endpoint
            return
            [
                $"https://login.microsoftonline.com/{_configuredTenantId}/v2.0",
                $"https://sts.windows.net/{_configuredTenantId}/"
            ];
        }

        // Development fallback with common issuer (no tenant configured)
        return
        [
            "https://login.microsoftonline.com/common/v2.0",
            "https://sts.windows.net/common/"
        ];
    }

    /// <summary>
    /// Validates the tid claim from the token against the configured tenant.
    /// Returns false (reject) when strict validation is enabled and tenants don't match.
    /// </summary>
    internal bool ValidateTenantClaim(string? tokenTenantId)
    {
        if (string.IsNullOrEmpty(_configuredTenantId))
        {
            // No configured tenant — skip tid check (Development-only path)
            _logger.LogDebug("No tenant configured; skipping tid claim validation");
            return true;
        }

        if (string.IsNullOrEmpty(tokenTenantId))
        {
            _logger.LogWarning("Token missing tid claim; rejecting");
            return false;
        }

        if (!string.Equals(tokenTenantId, _configuredTenantId, StringComparison.OrdinalIgnoreCase))
        {
            if (_strictTenantValidation)
            {
                _logger.LogWarning(
                    "Token tid {TokenTenant} does not match configured tenant {ConfiguredTenant}; rejecting",
                    tokenTenantId, _configuredTenantId);
                return false;
            }

            _logger.LogWarning(
                "Token tid {TokenTenant} does not match configured tenant {ConfiguredTenant}; " +
                "allowing because StrictTenantValidation is disabled",
                tokenTenantId, _configuredTenantId);
        }

        return true;
    }

    private string? GetSsoTokenFromActivity(IActivity activity)
    {
        // Teams SSO token is typically in activity.Value (for invoke activities).
        // We deliberately do NOT log activity.ChannelData — it contains tenant
        // metadata, conversation routing data, and occasionally bearer tokens.
        if (activity.Value != null)
        {
            string? valueJson = activity.Value.ToString();
            if (!string.IsNullOrEmpty(valueJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(valueJson);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("authentication", out JsonElement authElement) &&
                        authElement.TryGetProperty("token", out JsonElement tokenElement))
                    {
                        return tokenElement.GetString();
                    }
                }
                catch (JsonException)
                {
                    _logger.LogDebug("Activity.Value is not valid JSON");
                }
            }
        }

        // Note: For message activities we used to log From.Properties
        // ("aadObjectId"). That property can include user identifiers we don't
        // want in plaintext logs, so we no longer emit it.
        return null;
    }
}

public class UserIdentity
{
    public required string ObjectId { get; init; }
    public required string DisplayName { get; init; }
    public required string Email { get; init; }
    public required string TenantId { get; init; }
}
