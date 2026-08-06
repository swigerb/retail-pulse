using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace RetailPulse.Api.Security;

/// <summary>
/// Deterministic, fail-closed resolution of the active <see cref="AuthenticationMode"/>
/// from configuration.
///
/// The mode is read from the <c>Authentication:Mode</c> configuration key
/// (<c>Authentication__Mode</c> as an environment variable). Resolution is intentionally
/// explicit and never auto-detects a provider from ambient signals:
/// <list type="bullet">
///   <item>An explicit, recognized mode (case-insensitive: <c>Entra</c>, <c>GitHub</c>,
///     <c>Anonymous</c>) is honored as-is.</item>
///   <item>A missing/blank mode defaults to <see cref="AuthenticationMode.Entra"/> ONLY in
///     Development, preserving the existing local demo experience. This default is documented,
///     not inferred.</item>
///   <item>A missing/blank mode outside Development throws at startup — production must pin the
///     mode explicitly (<c>Authentication__Mode=Entra</c>) and fails closed otherwise.</item>
///   <item>An unknown or malformed value (including a bare number) throws at startup.</item>
/// </list>
/// This class only <i>resolves</i> the mode. The decision to accept or reject a resolved mode
/// (e.g. GitHub/Anonymous being unimplemented in this sprint) belongs to the
/// <see cref="ProviderNeutralAuthentication"/> factory boundary, which fails closed for any
/// mode it cannot wire.
/// </summary>
public sealed class AuthenticationModeOptions
{
    /// <summary>Configuration section that carries the provider selector.</summary>
    public const string SectionName = "Authentication";

    /// <summary>Full configuration key for the mode selector.</summary>
    public const string ModeKey = "Authentication:Mode";

    /// <summary>The resolved, active authentication mode.</summary>
    public required AuthenticationMode Mode { get; init; }

    /// <summary>
    /// Resolves the active <see cref="AuthenticationMode"/> from configuration, applying the
    /// documented Development default and failing closed on a missing/unknown/malformed value
    /// outside Development.
    /// </summary>
    public static AuthenticationMode Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        string? raw = configuration[ModeKey]?.Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Documented default: Development keeps the existing Entra-backed synthetic auth so
            // the local demo runs without configuration. Every other environment must pin the
            // mode explicitly and fails closed here — never silently assuming a provider.
            return environment.IsDevelopment()
                ? AuthenticationMode.Entra
                : throw new InvalidOperationException(
                $"Authentication:Mode is not configured. It must be set explicitly " +
                $"(Authentication__Mode=Entra) in the '{environment.EnvironmentName}' environment. " +
                "Authentication never auto-detects a provider outside Development — the app fails closed.");
        }

        // Reject bare numeric input (e.g. "1") so the mode is always a documented, readable name
        // and can never be selected by an accidental integer.
        return int.TryParse(raw, out _) ||
            !Enum.TryParse(raw, ignoreCase: true, out AuthenticationMode mode) ||
            !Enum.IsDefined(mode)
            ? throw new InvalidOperationException(
                $"Authentication:Mode '{raw}' is not a recognized authentication mode. " +
                "Valid modes are: Entra, GitHub, Anonymous.")
            : mode;
    }
}
