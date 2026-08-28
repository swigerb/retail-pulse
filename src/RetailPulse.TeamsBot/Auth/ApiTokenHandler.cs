using Microsoft.Identity.Client;

namespace RetailPulse.TeamsBot.Auth;

/// <summary>
/// Attaches an app-only access token to every outbound call to the Retail Pulse API.
/// </summary>
/// <remarks>
/// The bot called the API with a bare <see cref="HttpClient"/> and no credential at all,
/// so every turn ended in <c>401 Unauthorized</c> and the user saw a generic error card.
/// The API's authorization policy is deny-by-default and requires either a delegated
/// scope or an app-only token bearing the <c>RetailPulse.User</c> app role.
///
/// The bot uses the client-credentials flow with its own registration rather than
/// on-behalf-of. There is no user token to exchange on a proactive or channel-initiated
/// turn, and the API already supports app-only callers behind an explicit opt-in plus a
/// client-id allow-list — so the bot is authorised as a named service principal rather
/// than impersonating whoever happens to be typing. User attribution still travels in the
/// request body, where the API treats it as data rather than as an authorization signal.
///
/// MSAL caches tokens in memory and refreshes them before expiry, so this acquires once
/// per token lifetime rather than per request.
/// </remarks>
public sealed class ApiTokenHandler : DelegatingHandler
{
    private readonly IConfidentialClientApplication _app;
    private readonly string[] _scopes;
    private readonly ILogger<ApiTokenHandler> _logger;

    public ApiTokenHandler(IConfiguration configuration, ILogger<ApiTokenHandler> logger)
    {
        _logger = logger;

        string tenantId = Require(configuration, "MicrosoftEntra:TenantId");
        string clientId = Require(configuration, "MicrosoftEntra:ClientId");
        string clientSecret = Require(configuration, "Connections:BotServiceConnection:Settings:ClientSecret");

        // ".default" requests every application permission already consented for this
        // client, which is exactly the app role granted on the API registration.
        string apiScope = configuration["TeamsBot:ApiScope"]
            ?? throw new InvalidOperationException(
                "TeamsBot:ApiScope is required so the bot can request a token for the Retail Pulse API.");

        _scopes = [apiScope];

        _app = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .Build();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            AuthenticationResult result = await _app
                .AcquireTokenForClient(_scopes)
                .ExecuteAsync(cancellationToken);

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", result.AccessToken);
        }
        catch (MsalException ex)
        {
            // Let the request proceed unauthenticated so the API's own 401 surfaces in the
            // logs alongside this, rather than masking a config problem as a transport fault.
            _logger.LogError(ex, "Could not acquire an API access token for scope {Scope}", _scopes[0]);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] ?? throw new InvalidOperationException(
            $"'{key}' is required for the Teams bot to authenticate to the Retail Pulse API.");
}
