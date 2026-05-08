using Microsoft.Agents.Core.Models;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

namespace RetailPulse.TeamsBot.Auth;

public class TeamsSsoHandler
{
    private readonly ILogger<TeamsSsoHandler> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _oidcConfigManager;

    public TeamsSsoHandler(ILogger<TeamsSsoHandler> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        var tenantId = _configuration["MicrosoftEntra:TenantId"] ?? "common";
        var metadataAddress = $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration";
        _oidcConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever());
    }

    /// <summary>
    /// Extracts a Teams SSO identity from the inbound activity, validating the
    /// token signature, issuer, and audience against Microsoft Entra ID OIDC metadata.
    /// </summary>
    public async Task<UserIdentity?> ExtractUserIdentityAsync(IActivity activity)
    {
        try
        {
            // Extract SSO token from Teams activity
            var token = GetSsoTokenFromActivity(activity);

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("No SSO token found in activity");
                return null;
            }

            var tenantId = _configuration["MicrosoftEntra:TenantId"] ?? "common";
            var botClientId = _configuration["MicrosoftEntra:ClientId"]
                ?? throw new InvalidOperationException("MicrosoftEntra:ClientId must be configured for token validation.");

            var oidcConfig = await _oidcConfigManager.GetConfigurationAsync(CancellationToken.None);

            var handler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateLifetime = true,
                ValidateIssuer = true,
                ValidIssuers = new[]
                {
                    $"https://login.microsoftonline.com/{tenantId}/v2.0",
                    "https://login.microsoftonline.com/common/v2.0",
                    $"https://sts.windows.net/{tenantId}/"
                },
                ValidateAudience = true,
                ValidAudiences = new[] { botClientId },
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

            // Extract claims
            var oid = jwtToken.Claims.FirstOrDefault(c => c.Type == "oid")?.Value;
            var name = jwtToken.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            var email = jwtToken.Claims.FirstOrDefault(c => c.Type == "preferred_username" || c.Type == "upn")?.Value;
            var userTenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;

            if (string.IsNullOrEmpty(oid))
            {
                _logger.LogWarning("No object ID found in token");
                return null;
            }

            _logger.LogInformation("Successfully extracted user identity: {Name} ({Email})", name, email);

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

    private string? GetSsoTokenFromActivity(IActivity activity)
    {
        // Teams SSO token is typically in activity.Value (for invoke activities).
        // We deliberately do NOT log activity.ChannelData — it contains tenant
        // metadata, conversation routing data, and occasionally bearer tokens.
        if (activity.Value != null)
        {
            var valueJson = activity.Value.ToString();
            if (!string.IsNullOrEmpty(valueJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(valueJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("authentication", out var authElement) &&
                        authElement.TryGetProperty("token", out var tokenElement))
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
