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
/// Signing-key ROTATION is genuine, not merely a seam: <see cref="ValidationKeys"/> is the CURRENT
/// signing key first, followed by every strong key configured in
/// <see cref="GitHubAuthOptions.AdditionalValidationKeys"/>. New tokens are always signed with
/// <see cref="Key"/>, but tokens signed by a key that has just been demoted to the additional list keep
/// validating until they expire — so a rotation never invalidates in-flight sessions. A single instance
/// is registered as a singleton so the ephemeral key is stable within a process.
/// </summary>
public sealed class GitHubSigningKeyProvider
{
    private readonly IReadOnlyList<SecurityKey> _validationKeys;

    /// <summary>True when the key was generated ephemerally (Development, no configured secret).</summary>
    public bool IsEphemeral { get; }

    public GitHubSigningKeyProvider(GitHubAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.HasConfiguredSigningKey)
        {
            byte[] material = Encoding.UTF8.GetBytes(options.SigningKey!);
            Key = new SymmetricSecurityKey(material) { KeyId = KeyIdFor(material) };
            IsEphemeral = false;
        }
        else
        {
            // Development-only: a fresh 256-bit key per process. Sessions do not survive a restart.
            byte[] material = RandomNumberGenerator.GetBytes(32);
            Key = new SymmetricSecurityKey(material) { KeyId = "github-ephemeral" };
            IsEphemeral = true;
        }

        // Current signing key FIRST, then each configured rotation (validation-only) key. Each key gets
        // a STABLE, key-material-derived id so a token keeps the same kid after its key is demoted to
        // validation-only — that is what lets an in-flight token resolve to its original key across a
        // rotation. Options validation already rejected weak/placeholder rotation keys.
        var keys = new List<SecurityKey> { Key };
        for (int i = 0; i < options.AdditionalValidationKeys.Count; i++)
        {
            byte[] extra = Encoding.UTF8.GetBytes(options.AdditionalValidationKeys[i]);
            keys.Add(new SymmetricSecurityKey(extra) { KeyId = KeyIdFor(extra) });
        }

        _validationKeys = keys;
    }

    // A deterministic, non-reversible id for a key: the first bytes of SHA-256(key material). Stable
    // across process restarts and rotation "slots", so a demoted signing key keeps the same kid.
    private static string KeyIdFor(byte[] material) =>
        "github-" + Convert.ToHexString(SHA256.HashData(material))[..12].ToLowerInvariant();

    /// <summary>The active signing key for token creation and validation.</summary>
    public SymmetricSecurityKey Key { get; }

    /// <summary>
    /// All keys accepted for validation: the active signing key first, then every configured rotation
    /// (validation-only) key, so tokens issued before a rotation stay valid until they expire.
    /// </summary>
    public IEnumerable<SecurityKey> ValidationKeys => _validationKeys;
}
