using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Security;

/// <summary>
/// Provider-neutral authentication factory boundary.
///
/// This is the single entry point <c>Program.cs</c> calls to wire authentication. It resolves
/// the configured <see cref="AuthenticationMode"/> and dispatches to a mode-specific wiring
/// path:
/// <list type="bullet">
///   <item><see cref="AuthenticationMode.Entra"/> → the existing, unchanged Entra security
///     boundary (<see cref="AuthenticationSetup.AddRetailPulseAuthentication"/>). Security
///     semantics are identical to before this foundation existed; it returns the resolved
///     <see cref="EntraAuthOptions"/> so the caller wires the Entra authorization policy.</item>
///   <item><see cref="AuthenticationMode.Anonymous"/> → the Sprint 1 Anonymous boundary. It wires
///     the anonymous session scheme, the constrained authorization policy, and all billable-use
///     guardrails INTERNALLY, then returns <c>null</c> (the caller must not layer the Entra policy
///     on top). A hosted deployment must additionally pass the second explicit opt-in and complete
///     guardrail configuration, or startup fails closed (see <see cref="AnonymousAuthOptions"/>).</item>
///   <item><see cref="AuthenticationMode.GitHub"/> → a deliberate fail-closed startup error. This
///     mode is declared but NOT implemented in this sprint; selecting it throws before any
///     authentication scheme is registered, so the app can never fall through to Entra,
///     Development, or anonymous access.</item>
/// </list>
/// The boundary is intentionally thin: later sprints add a case per provider here without
/// reworking the Entra path or the authorization policy.
/// </summary>
public static class ProviderNeutralAuthentication
{
    /// <summary>
    /// Resolves the configured authentication mode and registers the matching authentication
    /// stack. Returns the resolved <see cref="EntraAuthOptions"/> for the Entra path so the
    /// caller can wire the authorization policy; returns <c>null</c> for the Anonymous path,
    /// which wires its own authorization internally. GitHub throws.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when the configured mode is recognized but not implemented in this sprint (GitHub).
    /// This is a fail-closed startup error by design.
    /// </exception>
    public static EntraAuthOptions? AddProviderNeutralAuthentication(
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

            case AuthenticationMode.Anonymous:
                services.AddSingleton(new AuthenticationModeOptions { Mode = mode });
                AddAnonymousMode(services, configuration, environment);
                return null;
            case AuthenticationMode.GitHub:
                throw new NotSupportedException(
                    $"Authentication mode '{mode}' is not implemented in this sprint. Only 'Entra' and " +
                    "'Anonymous' are currently supported. GitHub is an opt-in capability delivered in a later " +
                    "sprint and is never enabled in production. This is a deliberate fail-closed startup error — " +
                    "authentication does not fall through to Entra, Development, or Anonymous.");

            default:
                // Unreachable: AuthenticationModeOptions.Resolve only returns defined members.
                throw new InvalidOperationException(
                    $"Unhandled authentication mode '{mode}'. This is a bug in the authentication factory.");
        }
    }

    /// <summary>
    /// Wires the Anonymous authentication scheme, the constrained authorization policy, and every
    /// billable-use guardrail. Resolution is fail-closed: a hosted (non-Development) environment
    /// throws here unless <c>Anonymous:AllowHosted=true</c> AND a strong signing key AND positive
    /// daily request/token/cost ceilings are supplied.
    /// </summary>
    private static void AddAnonymousMode(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = AnonymousAuthOptions.FromConfiguration(configuration, environment);

        // Session scheme + constrained authz policy (default + fallback) + token service +
        // signing key provider + anonymous principal normalizer.
        services.AddAnonymousAuthentication(options);

        // The chat tool filter + output cap decide from the current request principal.
        services.AddHttpContextAccessor();
        services.AddSingleton<IAnonymousChatPolicy, AnonymousChatPolicy>();

        // Billable-use guardrails: per-subject/per-IP minute limits + global daily circuit breaker.
        services.AddSingleton<AnonymousRateLimiter>();
        services.AddSingleton<AnonymousUsageBudget>();
        services.AddSingleton<AnonymousGuardMiddleware>();
    }
}
