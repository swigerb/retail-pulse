namespace RetailPulse.Api.Security;

/// <summary>
/// The authentication provider the API runs under. This is the provider-neutral
/// selector introduced by the Sprint 0 authentication foundation.
///
/// Only <see cref="Entra"/> is implemented today; it routes to the existing, unchanged
/// Microsoft Entra security boundary (see <see cref="AuthenticationSetup"/>). The other
/// members are declared so the contract, configuration surface, and tests are stable for
/// later sprints, but selecting them fails startup with a precise "not implemented in this
/// sprint" error — authentication never falls through to Entra, Development, or Anonymous.
/// </summary>
public enum AuthenticationMode
{
    /// <summary>Microsoft Entra ID (single-tenant JWT bearer). The only live/production mode.</summary>
    Entra = 0,

    /// <summary>GitHub OAuth/OIDC. Opt-in for non-production; NOT implemented in Sprint 0.</summary>
    GitHub = 1,

    /// <summary>Anonymous (no authentication). Opt-in for non-production; NOT implemented in Sprint 0.</summary>
    Anonymous = 2,
}
