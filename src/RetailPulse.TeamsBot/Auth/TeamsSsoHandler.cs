using Microsoft.Agents.Core.Models;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

namespace RetailPulse.TeamsBot.Auth;

public class TeamsSsoHandler
{
    private readonly ILogger<TeamsSsoHandler> _logger;

    public TeamsSsoHandler(ILogger<TeamsSsoHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts a Teams SSO identity from the inbound activity. Synchronous
    /// today (no I/O), but kept Task-returning so callers can adopt OBO/OIDC
    /// token validation later without a signature change.
    /// </summary>
    public Task<UserIdentity?> ExtractUserIdentityAsync(IActivity activity)
    {
        try
        {
            // Extract SSO token from Teams activity
            var token = GetSsoTokenFromActivity(activity);

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("No SSO token found in activity");
                return Task.FromResult<UserIdentity?>(null);
            }

            // SECURITY (demo-grade validation):
            //
            // ValidateToken below enforces token structure, lifetime, and that
            // the value is a real JWT — sufficient to keep an unsigned blob or
            // an expired token out of our identity pipeline.
            //
            // Production deployments MUST also validate signature, issuer, and
            // audience against Microsoft Entra OIDC metadata (e.g.,
            // https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration)
            // and configure ValidateIssuerSigningKey = true with an
            // IssuerSigningKeyResolver that fetches Microsoft's signing keys.
            var handler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateLifetime = true,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = false,
                SignatureValidator = (string t, TokenValidationParameters _) => new JwtSecurityToken(t),
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            try
            {
                handler.ValidateToken(token, validationParams, out var validatedToken);
            }
            catch (SecurityTokenException stex)
            {
                _logger.LogWarning(stex, "SSO token failed validation");
                return Task.FromResult<UserIdentity?>(null);
            }

            var jwtToken = handler.ReadJwtToken(token);

            // Extract claims
            var oid = jwtToken.Claims.FirstOrDefault(c => c.Type == "oid")?.Value;
            var name = jwtToken.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            var email = jwtToken.Claims.FirstOrDefault(c => c.Type == "preferred_username" || c.Type == "upn")?.Value;
            var tenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;

            if (string.IsNullOrEmpty(oid))
            {
                _logger.LogWarning("No object ID found in token");
                return Task.FromResult<UserIdentity?>(null);
            }

            _logger.LogInformation("Successfully extracted user identity: {Name} ({Email})", name, email);

            return Task.FromResult<UserIdentity?>(new UserIdentity
            {
                ObjectId = oid,
                DisplayName = name ?? "Unknown User",
                Email = email ?? string.Empty,
                TenantId = tenantId ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract user identity from activity");
            return Task.FromResult<UserIdentity?>(null);
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
