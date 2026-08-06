using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Auth;

namespace RetailPulse.Api.Security;

/// <summary>
/// Provider-neutral authentication factory boundary (Sprint 0 foundation).
///
/// This is the single entry point <c>Program.cs</c> calls to wire authentication. It resolves
/// the configured <see cref="AuthenticationMode"/> and dispatches to a mode-specific wiring
/// path:
/// <list type="bullet">
///   <item><see cref="AuthenticationMode.Entra"/> → the existing, unchanged Entra security
///     boundary (<see cref="AuthenticationSetup.AddRetailPulseAuthentication"/>). Security
///     semantics are identical to before this foundation existed.</item>
///   <item><see cref="AuthenticationMode.GitHub"/> / <see cref="AuthenticationMode.Anonymous"/>
///     → a deliberate fail-closed startup error. These modes are declared but NOT implemented in
///     this sprint; selecting one throws before any authentication scheme is registered, so the
///     app can never fall through to Entra, Development, or anonymous access.</item>
/// </list>
/// The boundary is intentionally thin: later sprints add a case per provider here without
/// reworking the Entra path or the authorization policy.
/// </summary>
public static class ProviderNeutralAuthentication
{
    /// <summary>
    /// Resolves the configured authentication mode and registers the matching authentication
    /// stack. Returns the resolved <see cref="EntraAuthOptions"/> so the caller can wire the
    /// authorization policy (only the Entra path returns; other modes throw).
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when the configured mode is recognized but not implemented in this sprint
    /// (GitHub, Anonymous). This is a fail-closed startup error by design.
    /// </exception>
    public static EntraAuthOptions AddProviderNeutralAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        AuthenticationMode mode = AuthenticationModeOptions.Resolve(configuration, environment);

        switch (mode)
        {
            case AuthenticationMode.Entra:
                // Register the resolved mode for observability/diagnostics, then wire the
                // existing Entra boundary exactly as before — no security-semantic change.
                services.AddSingleton(new AuthenticationModeOptions { Mode = mode });
                EntraAuthOptions options = services.AddRetailPulseAuthentication(configuration, environment);

                // Normalized principal seam for future providers. Registered (not dead) so the
                // Entra claims → normalized-identity mapping is exercised and testable today.
                services.AddSingleton<IPrincipalNormalizer>(new EntraPrincipalNormalizer(options));
                return options;

            case AuthenticationMode.GitHub:
            case AuthenticationMode.Anonymous:
                throw new NotSupportedException(
                    $"Authentication mode '{mode}' is not implemented in this sprint " +
                    "(Sprint 0: provider-neutral authentication foundation). Only 'Entra' is currently " +
                    "supported. GitHub and Anonymous are opt-in capabilities delivered in later sprints and " +
                    "are never enabled in production. This is a deliberate fail-closed startup error — " +
                    "authentication does not fall through to Entra, Development, or Anonymous.");

            default:
                // Unreachable: AuthenticationModeOptions.Resolve only returns defined members.
                throw new InvalidOperationException(
                    $"Unhandled authentication mode '{mode}'. This is a bug in the authentication factory.");
        }
    }
}
