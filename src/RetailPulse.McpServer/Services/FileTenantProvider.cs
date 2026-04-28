using RetailPulse.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.McpServer.Services;

public class FileTenantProvider : ITenantProvider
{
    private readonly TenantConfiguration _tenant;

    public FileTenantProvider(string configPath)
    {
        var yaml = File.ReadAllText(configPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        _tenant = deserializer.Deserialize<TenantConfiguration>(yaml);
    }

    public TenantConfiguration GetTenant() => _tenant;
}
