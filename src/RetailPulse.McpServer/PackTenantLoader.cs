using RetailPulse.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.McpServer;

/// <summary>
/// Reads the tenant portion of a content pack's <c>pack.yaml</c> and
/// the pack's <c>seed/scenario.yaml</c> manifest, and wraps them in an
/// <see cref="ITenantProvider"/> plus a <see cref="SeedManifest"/> the
/// SQLite seeder consumes. Kept here (rather than reaching into the
/// API's pack loader) so the MCP deployment surface remains a thin,
/// self-contained assembly — no API → MCP project reference is needed.
/// The YAML deserializer opts in to
/// <c>WithDuplicateKeyChecking()</c> so a copy-paste mistake in a
/// pack's <c>pack.yaml</c> is rejected identically here and in the API
/// pack loader (issue #108, blocker #1).
/// </summary>
internal static class PackTenantLoader
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        // Reject duplicate keys inside pack.yaml so an accidentally
        // repeated 'tenant:' block, doubled 'brands:' node, or
        // repeated metadata field fails MCP boot instead of silently
        // losing content. The API PackLoader uses the same guard.
        .WithDuplicateKeyChecking()
        .Build();

    /// <summary>
    /// Backwards-compatible tenant-only load. Kept because the MCP boot
    /// path historically resolves <c>pack.yaml</c> alone; the seed
    /// manifest is loaded through
    /// <see cref="LoadFromPackDirectory"/> below.
    /// </summary>
    public static ITenantProvider LoadFromPackYaml(string packYamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packYamlPath);
        return LoadPack(packYamlPath).Tenant;
    }

    /// <summary>
    /// Load the full pack surface the MCP server consumes: tenant
    /// declaration plus scenario seed manifest. Fails fast (with an
    /// operator-actionable message) when either <c>pack.yaml</c> or
    /// <c>seed/scenario.yaml</c> is missing or malformed.
    /// </summary>
    public static PackLoadResult LoadFromPackDirectory(string packDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packDir);

        if (!Directory.Exists(packDir))
        {
            throw new DirectoryNotFoundException(
                $"Pack directory not found: {packDir}. Set Packs:Active/Packs:Root or ship the pack under the packs root.");
        }

        string packYamlPath = Path.Combine(packDir, "pack.yaml");
        PackLoadResult tenantOnly = LoadPack(packYamlPath);

        string seedDir = Path.Combine(packDir, "seed");
        SeedManifest seed;
        try
        {
            seed = SeedManifestLoader.LoadFromDirectory(seedDir);
        }
        catch (SeedManifestLoadException ex)
        {
            throw new InvalidOperationException(
                $"Pack '{Path.GetFileName(packDir)}' seed manifest problem: {ex.Message}", ex);
        }

        return new PackLoadResult(tenantOnly.Tenant, seed, packYamlPath, seedDir);
    }

    private static PackLoadResult LoadPack(string packYamlPath)
    {
        if (!File.Exists(packYamlPath))
        {
            throw new FileNotFoundException(
                $"pack.yaml not found: {packYamlPath}", packYamlPath);
        }

        string yaml = File.ReadAllText(packYamlPath);
        PackDocument? doc;
        try
        {
            doc = _deserializer.Deserialize<PackDocument>(yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse pack.yaml at '{packYamlPath}': {ex.Message}", ex);
        }

        return doc is null || doc.Tenant is null
            ? throw new InvalidOperationException(
                $"pack.yaml at '{packYamlPath}' did not contain a tenant section.")
            : new PackLoadResult(new InMemoryTenantProvider(doc.Tenant), new SeedManifest(), packYamlPath, "");
    }

    private sealed class InMemoryTenantProvider : ITenantProvider
    {
        private readonly TenantConfiguration _tenant;
        public InMemoryTenantProvider(TenantConfiguration tenant)
        {
            _tenant = tenant;
        }

        public TenantConfiguration GetTenant() => _tenant;
    }

    private sealed class PackDocument
    {
        public TenantConfiguration? Tenant { get; init; }
    }
}

/// <summary>
/// Result of a full pack directory load.
/// </summary>
/// <param name="Tenant">Tenant provider derived from pack.yaml.</param>
/// <param name="Seed">Scenario seed manifest derived from seed/scenario.yaml.</param>
/// <param name="PackYamlPath">Absolute path to the pack.yaml file.</param>
/// <param name="SeedDir">Absolute path to the seed directory (empty for tenant-only loads).</param>
internal sealed record PackLoadResult(
    ITenantProvider Tenant,
    SeedManifest Seed,
    string PackYamlPath,
    string SeedDir);
