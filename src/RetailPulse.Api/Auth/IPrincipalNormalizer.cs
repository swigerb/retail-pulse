using System.Security.Claims;
using RetailPulse.Api.Security;

namespace RetailPulse.Api.Auth;

/// <summary>
/// Maps a provider-specific <see cref="ClaimsPrincipal"/> to the provider-neutral
/// <see cref="NormalizedPrincipal"/>. Each authentication provider ships one implementation;
/// the active one is registered by the <see cref="ProviderNeutralAuthentication"/> factory.
/// </summary>
public interface IPrincipalNormalizer
{
    /// <summary>The authentication mode this normalizer handles.</summary>
    AuthenticationMode Mode { get; }

    /// <summary>Projects the given principal onto the normalized identity/claims model.</summary>
    NormalizedPrincipal Normalize(ClaimsPrincipal principal);
}
