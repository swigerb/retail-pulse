namespace RetailPulse.Api.Security;

/// <summary>
/// The authentication provider the API runs under. This is the provider-neutral
/// selector introduced by the Sprint 0 authentication foundation.
///
/// All three members are implemented: <see cref="Entra"/> (Sprint 0 — the existing,
/// unchanged Microsoft Entra security boundary, see <see cref="AuthenticationSetup"/>),
/// <see cref="Anonymous"/> (Sprint 1), and <see cref="GitHub"/> (Sprint 2). Entra is the
/// only live/production mode; GitHub and Anonymous are opt-in, fail-closed capabilities for
/// non-production environments and are never deployed. Selecting a missing, unknown,
/// malformed, or (for a hosted GitHub/Anonymous deploy) incompletely configured mode fails
/// startup with a precise error — authentication never falls through to a weaker provider.
/// </summary>
public enum AuthenticationMode
{
    /// <summary>Microsoft Entra ID (single-tenant JWT bearer). The only live/production mode.</summary>
    Entra = 0,

    /// <summary>GitHub confidential OAuth BFF. Opt-in, fail-closed for non-production; implemented Sprint 2. Never deployed.</summary>
    GitHub = 1,

    /// <summary>Anonymous server-minted session. Opt-in, fail-closed for non-production; implemented Sprint 1. Never deployed.</summary>
    Anonymous = 2,
}
