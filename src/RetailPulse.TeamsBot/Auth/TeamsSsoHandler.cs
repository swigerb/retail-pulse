using Microsoft.Agents.Core.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

namespace RetailPulse.TeamsBot.Auth;

public class TeamsSsoHandler
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeamsSsoHandler> _logger;

    public TeamsSsoHandler(
        IConfiguration configuration,
        ILogger<TeamsSsoHandler> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

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

            // Validate and parse the token
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);

            // Extract claims
            var oid = jwtToken.Claims.FirstOrDefault(c => c.Type == "oid")?.Value;
            var name = jwtToken.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            var email = jwtToken.Claims.FirstOrDefault(c => c.Type == "preferred_username" || c.Type == "upn")?.Value;
            var tenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;

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
                TenantId = tenantId ?? string.Empty,
                Token = token
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
        // Teams SSO token is typically in activity.Value or activity.ChannelData
        
        // Try to get from ChannelData
        if (activity.ChannelData != null)
        {
            var channelData = activity.ChannelData.ToString();
            if (!string.IsNullOrEmpty(channelData))
            {
                _logger.LogDebug("ChannelData: {ChannelData}", channelData);
            }
        }

        // Try to get from Activity.Value (for invoke activities)
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

        // For message activities, check From.Properties for aadObjectId
        if (activity.From?.Properties != null)
        {
            foreach (var prop in activity.From.Properties)
            {
                if (prop.Key == "aadObjectId")
                {
                    _logger.LogDebug("Found aadObjectId in From.Properties: {AadObjectId}", prop.Value);
                }
            }
        }

        return null;
    }
}

public class UserIdentity
{
    public required string ObjectId { get; init; }
    public required string DisplayName { get; init; }
    public required string Email { get; init; }
    public required string TenantId { get; init; }
    public required string Token { get; init; }
}
