using RetailPulse.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.McpServer;

/// <summary>
/// Reads the tenant portion of a content pack's <c>pack.yaml</c> and
/// wraps it in an <see cref="ITenantProvider"/>. The MCP server only
/// consumes the tenant declaration (brands, regions, channels,
/// distribution) to seed its SQLite metrics — agent rosters, starting
/// tasks, and knowledge documents live entirely in the API host. Kept
/// here (rather than reaching into the API's pack loader) so the MCP
/// deployment surface remains a thin, self-contained assembly.
/// </summary>
internal static class PackTenantLoader
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ITenantProvider LoadFromPackYaml(string packYamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packYamlPath);
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

        if (doc is null || doc.Tenant is null)
        {
            throw new InvalidOperationException(
                $"pack.yaml at '{packYamlPath}' did not contain a tenant section.");
        }

        return new InMemoryTenantProvider(doc.Tenant);
    }

    private sealed class InMemoryTenantProvider : ITenantProvider
    {
        private readonly TenantConfiguration _tenant;
        public InMemoryTenantProvider(TenantConfiguration tenant) => _tenant = tenant;
        public TenantConfiguration GetTenant() => _tenant;
    }

    private sealed class PackDocument
    {
        public TenantConfiguration? Tenant { get; init; }
    }
}
