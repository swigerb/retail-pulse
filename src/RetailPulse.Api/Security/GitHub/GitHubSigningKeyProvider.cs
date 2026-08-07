using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace RetailPulse.Api.Security.GitHub;

/// <summary>
/// Provides the symmetric HMAC key used to sign and validate the app's own GitHub session tokens.
///
/// Key resolution is fail-closed and environment-aware, mirroring the Anonymous mode seam:
/// <list type="bullet">
///   <item>When <see cref="GitHubAuthOptions.HasConfiguredSigningKey"/> is true, the configured
///     secret is used (required in hosted mode; validated to be ≥ 256-bit by the options).</item>
///   <item>Otherwise (Development only — hosted mode already failed startup) a cryptographically
///     random 256-bit key is generated ONCE per process. It is never persisted, so all GitHub
///     sessions are invalidated when the process restarts. This is documented, intentional dev
///     behavior, not a fallback that can leak into a hosted deployment.</item>
/// </list>
/// <see cref="ValidationKeys"/> is the rotation seam: additional retired keys can be accepted for
/// validation while a new key signs, so tokens issued before a rotation stay valid until they expire.
/// A single instance is registered as a singleton so the ephemeral key is stable within a process.
/// </summary>
public sealed class GitHubSigningKeyProvider
{
    /// <summary>True when the key was generated ephemerally (Development, no configured secret).</summary>
    public bool IsEphemeral { get; }

    public GitHubSigningKeyProvider(GitHubAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.HasConfiguredSigningKey)
        {
            Key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey!))
            {
                KeyId = "github-configured",
            };
            IsEphemeral = false;
        }
        else
        {
            // Development-only: a fresh 256-bit key per process. Sessions do not survive a restart.
            byte[] material = RandomNumberGenerator.GetBytes(32);
            Key = new SymmetricSecurityKey(material) { KeyId = "github-ephemeral" };
            IsEphemeral = true;
        }
    }

    /// <summary>The active signing key for token creation and validation.</summary>
    public SymmetricSecurityKey Key { get; }

    /// <summary>
    /// All keys accepted for validation. The active key first; retired keys can be appended here
    /// during a rotation so in-flight tokens validate until they expire.
    /// </summary>
    public IEnumerable<SecurityKey> ValidationKeys => [Key];
}
