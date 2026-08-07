using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetailPulse.Api.Security.GitHub;

/// <summary>Outcome of the confidential authorization-code → access-token exchange.</summary>
/// <param name="Success">True when GitHub returned a usable access token.</param>
/// <param name="AccessToken">The provider token — used only transiently for verification, never stored/returned/logged.</param>
/// <param name="Error">A short, token-free error code/description safe to log.</param>
public readonly record struct GitHubTokenResult(bool Success, string? AccessToken, string? Error);

/// <summary>Outcome of the <c>/user</c> validation call.</summary>
/// <param name="Success">True when GitHub returned a valid authenticated user.</param>
/// <param name="UserId">The immutable numeric GitHub user id.</param>
/// <param name="Login">The mutable login handle (informational only).</param>
/// <param name="Error">A short, token-free error code/description safe to log.</param>
public readonly record struct GitHubUserResult(bool Success, long UserId, string Login, string? Error);

/// <summary>Outcome of an org active-membership check.</summary>
/// <param name="IsActiveMember">True only when the user is an ACTIVE member of the org.</param>
/// <param name="Error">Non-null when the check could not be completed (fail closed on any error).</param>
public readonly record struct GitHubOrgMembershipResult(bool IsActiveMember, string? Error);

/// <summary>
/// The confidential-client transport to GitHub. The interface is the mock boundary for tests; the
/// implementation talks ONLY to the fixed, hard-coded GitHub endpoints in
/// <see cref="GitHubAuthConstants"/> (SSRF defense — no host, path, or redirect is ever derived from
/// user input), with auto-redirect disabled so a crafted 3xx cannot bounce a request or a bearer
/// token to another host.
/// </summary>
public interface IGitHubOAuthClient
{
    /// <summary>Exchanges an authorization code for a provider access token (server-side, confidential).</summary>
    Task<GitHubTokenResult> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Validates the provider token by reading the authenticated user's immutable id + login.</summary>
    Task<GitHubUserResult> GetUserAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>Checks whether the authenticated user is an ACTIVE member of <paramref name="org"/>.</summary>
    Task<GitHubOrgMembershipResult> GetActiveOrgMembershipAsync(string accessToken, string org, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class GitHubOAuthClient : IGitHubOAuthClient
{
    private static readonly CompositeFormat _orgMembershipUrlFormat =
        CompositeFormat.Parse(GitHubAuthConstants.OrgMembershipUrlFormat);

    private readonly HttpClient _http;
    private readonly GitHubAuthOptions _options;
    private readonly ILogger<GitHubOAuthClient> _logger;

    public GitHubOAuthClient(HttpClient http, GitHubAuthOptions options, ILogger<GitHubOAuthClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GitHubTokenResult> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new GitHubTokenResult(false, null, "missing_code");
        }

        // Confidential exchange: the client secret is sent ONLY here, server→GitHub, over the fixed
        // token endpoint. The redirect_uri is the exact registered callback (defense in depth).
        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubAuthConstants.AccessTokenUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret!,
            ["code"] = code,
            ["redirect_uri"] = _options.CallbackUrl,
        });

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub token exchange failed with status {Status}", (int)response.StatusCode);
                return new GitHubTokenResult(false, null, $"exchange_http_{(int)response.StatusCode}");
            }

            TokenResponse? body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (body is null || !string.IsNullOrEmpty(body.Error))
            {
                // GitHub reports a denied/expired/reused code as a 200 with an "error" field.
                return new GitHubTokenResult(false, null, SafeError(body?.Error) ?? "exchange_no_token");
            }

            return string.IsNullOrWhiteSpace(body.AccessToken)
                ? new GitHubTokenResult(false, null, "exchange_no_token")
                : new GitHubTokenResult(true, body.AccessToken, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Never include the exception detail verbatim in a client-facing path; log the type only.
            _logger.LogWarning("GitHub token exchange transport error: {Type}", ex.GetType().Name);
            return new GitHubTokenResult(false, null, "exchange_transport_error");
        }
    }

    public async Task<GitHubUserResult> GetUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new GitHubUserResult(false, 0, string.Empty, "missing_token");
        }

        try
        {
            using HttpResponseMessage response = await SendAuthorizedGetAsync(
                GitHubAuthConstants.UserApiUrl, accessToken, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub /user validation failed with status {Status}", (int)response.StatusCode);
                return new GitHubUserResult(false, 0, string.Empty, $"user_http_{(int)response.StatusCode}");
            }

            UserResponse? user = await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken);
            return user is null || user.Id <= 0 || string.IsNullOrWhiteSpace(user.Login)
                ? new GitHubUserResult(false, 0, string.Empty, "user_invalid")
                : new GitHubUserResult(true, user.Id, user.Login, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("GitHub /user transport error: {Type}", ex.GetType().Name);
            return new GitHubUserResult(false, 0, string.Empty, "user_transport_error");
        }
    }

    public async Task<GitHubOrgMembershipResult> GetActiveOrgMembershipAsync(
        string accessToken, string org, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(org))
        {
            return new GitHubOrgMembershipResult(false, "missing_token_or_org");
        }

        // The org path segment is a configured, validated allowlist value — never user input — and is
        // URL-escaped defensively. Fixed host/path, no redirects.
        string url = string.Format(
            CultureInfo.InvariantCulture,
            _orgMembershipUrlFormat,
            Uri.EscapeDataString(org));

        try
        {
            using HttpResponseMessage response = await SendAuthorizedGetAsync(url, accessToken, cancellationToken);

            // 404 = not a member (or membership not visible without read:org). 403 = insufficient scope.
            // Any non-success is treated as "not an active member" and reported as a fail-closed error.
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubOrgMembershipResult(false, $"org_http_{(int)response.StatusCode}");
            }

            MembershipResponse? membership = await response.Content.ReadFromJsonAsync<MembershipResponse>(cancellationToken);
            bool active = string.Equals(membership?.State, "active", StringComparison.OrdinalIgnoreCase);
            return new GitHubOrgMembershipResult(active, active ? null : "org_not_active");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("GitHub org membership transport error: {Type}", ex.GetType().Name);
            return new GitHubOrgMembershipResult(false, "org_transport_error");
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedGetAsync(
        string url, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return await _http.SendAsync(request, cancellationToken);
    }

    /// <summary>Whitelist a small set of known GitHub OAuth error codes; drop anything unexpected.</summary>
    private static string? SafeError(string? error)
    {
        return error switch
        {
            "bad_verification_code" => "exchange_bad_code",
            "incorrect_client_credentials" => "exchange_bad_credentials",
            "redirect_uri_mismatch" => "exchange_redirect_mismatch",
            "access_denied" => "access_denied",
            _ => string.IsNullOrEmpty(error) ? null : "exchange_error",
        };
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class UserResponse
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("login")] public string? Login { get; set; }
    }

    private sealed class MembershipResponse
    {
        [JsonPropertyName("state")] public string? State { get; set; }
    }
}
