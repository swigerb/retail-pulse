using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Security;

namespace RetailPulse.Api.Auth;

/// <summary>
/// Authentication handler that auto-succeeds in Development mode.
/// Produces a synthetic ClaimsPrincipal so the demo runs without real tokens.
///
/// The synthetic identity carries the same app role (<c>roles</c>) and delegated scope
/// (<c>scp</c>) that the production authorization policy requires, so local runs exercise
/// the real policy path without a real Entra token. This handler is only ever registered
/// in the Development environment.
/// </summary>
public class DevelopmentAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Development";

    public DevelopmentAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, "dev-user"),
            new Claim(ClaimTypes.Name, "Development User"),
            new Claim(ClaimTypes.Role, "Admin"),
            // App role + scope so the strong RetailPulseUser policy passes under dev auth.
            new Claim(ClaimTypes.Role, EntraAuthOptions.DefaultAppRole),
            new Claim("roles", EntraAuthOptions.DefaultAppRole),
            new Claim("scp", EntraAuthOptions.DefaultApiScope),
            new Claim("oid", "00000000-0000-0000-0000-000000000000")
        ];

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
