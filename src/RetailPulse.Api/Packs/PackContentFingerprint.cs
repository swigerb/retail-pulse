using System.Security.Cryptography;
using System.Text;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Packs;

/// <summary>
/// Content-hash helpers used by pack-aware seeding paths. Every fingerprint
/// is derived from bytes the pack owner controls (pack key, tenant declaration,
/// knowledge bodies) so an unchanged active pack always produces the same
/// fingerprint and a real content or pack change always produces a
/// different one.
/// </summary>
/// <remarks>
/// The fingerprint is intentionally NOT derived from wall-clock timestamps
/// or file-system metadata. Timestamps are noisy (git checkout order,
/// CI cache warmth) and would either miss real content changes or fire
/// spurious re-seeds. The identity here is the pack itself.
/// </remarks>
public static class PackContentFingerprint
{
    /// <summary>
    /// Version stamp mixed into every fingerprint. Bump when the seeding
    /// logic that consumes the fingerprint changes so hosts with a stale
    /// stored fingerprint always re-run seeding after an upgrade.
    /// </summary>
    /// <remarks>
    /// v2 folds the pack's <c>seed/scenario.yaml</c> (and any other files
    /// under the pack's <c>seed/</c> directory) into the fingerprint so
    /// editing scenario data alone triggers a reseed. Upgrades from v1
    /// stores always miss a v2 lookup and re-seed on first boot.
    /// </remarks>
    public const string Version = "v2";

    /// <summary>
    /// Compute a stable fingerprint for the pack. The fingerprint mixes
    /// the pack key, the normalized <c>pack.yaml</c> content (tenant +
    /// metadata), and the normalized content of every knowledge document
    /// ordered by source. Any of those changing produces a new value.
    /// </summary>
    public static string ComputePackFingerprint(LoadedPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var sb = new StringBuilder();
        sb.Append("key=").Append(pack.Name.ToLowerInvariant()).Append('\n');
        sb.Append("metadataKey=").Append((pack.Metadata.Key ?? "").ToLowerInvariant()).Append('\n');
        sb.Append("version=").Append(pack.Metadata.Version ?? "").Append('\n');

        string packYaml = Path.Combine(pack.RootPath, "pack.yaml");
        if (File.Exists(packYaml))
        {
            sb.Append("packYaml=").Append(HashHex(NormalizeLineEndings(File.ReadAllText(packYaml)))).Append('\n');
        }

        foreach (PackKnowledgeDocument doc in pack.KnowledgeDocuments
            .OrderBy(d => d.Source, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("kn:").Append(doc.Source).Append('=').Append(ComputeContentHash(doc.Content)).Append('\n');
        }

        // Every file under seed/ (scenario.yaml today, additional
        // manifests later) contributes to the fingerprint so a change to
        // scenario data alone rehashes even when pack.yaml is untouched.
        // Files are visited in deterministic (relative-path) order so
        // Windows and Linux checkouts produce the same digest.
        string seedDir = Path.Combine(pack.RootPath, "seed");
        if (Directory.Exists(seedDir))
        {
            List<string> seedFiles =
            [
                .. Directory
                    .EnumerateFiles(seedDir, "*", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.Ordinal),
            ];
            foreach (string file in seedFiles)
            {
                string relative = Path.GetRelativePath(seedDir, file).Replace('\\', '/');
                sb.Append("seed:").Append(relative).Append('=')
                    .Append(HashHex(NormalizeLineEndings(File.ReadAllText(file))))
                    .Append('\n');
            }
        }

        return Version + ":" + HashHex(sb.ToString());
    }

    /// <summary>
    /// SHA-256 hex of a single document's content with line endings
    /// normalized so a Windows/Linux checkout produces the same hash.
    /// </summary>
    public static string ComputeContentHash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return HashHex(NormalizeLineEndings(content));
    }

    private static string HashHex(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
