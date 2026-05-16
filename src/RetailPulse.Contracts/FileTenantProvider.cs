using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.Contracts;

/// <summary>
/// Shared YAML-backed tenant provider used by both the API and MCP server.
/// Validates required fields at load time so a misconfigured tenant.yaml fails fast
/// instead of silently producing an empty tenant.
/// </summary>
public class FileTenantProvider : ITenantProvider
{
    private readonly TenantConfiguration _tenant;

    public FileTenantProvider(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new ArgumentException("Tenant config path is required.", nameof(configPath));
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                $"Tenant configuration file not found: '{Path.GetFileName(configPath)}'. " +
                "Copy tenant.yaml.example to tenant.yaml at the repo root.",
                configPath);
        }

        string yaml = File.ReadAllText(configPath);
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        TenantConfiguration? tenant;
        try
        {
            tenant = deserializer.Deserialize<TenantConfiguration>(yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse tenant configuration '{Path.GetFileName(configPath)}': {ex.Message}", ex);
        }

        _tenant = tenant ?? throw new InvalidOperationException(
            $"Tenant configuration '{Path.GetFileName(configPath)}' deserialized to null.");

        Validate(_tenant, configPath);
    }

    public TenantConfiguration GetTenant() => _tenant;

    private static void Validate(TenantConfiguration tenant, string configPath)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(tenant.Company))
        {
            errors.Add("'company' is required.");
        }

        if (string.IsNullOrWhiteSpace(tenant.Industry))
        {
            errors.Add("'industry' is required.");
        }

        if (tenant.Brands is null || tenant.Brands.Count == 0)
        {
            errors.Add("At least one entry under 'brands' is required.");
        }
        else
        {
            for (int i = 0; i < tenant.Brands.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(tenant.Brands[i].Name))
                {
                    errors.Add($"brands[{i}].name is required.");
                }
            }
        }

        if (tenant.Regions is null || tenant.Regions.Count == 0)
        {
            errors.Add("At least one entry under 'regions' is required.");
        }

        if (tenant.Channels is null || tenant.Channels.Count == 0)
        {
            errors.Add("At least one entry under 'channels' is required.");
        }

        if (tenant.Distribution is null || string.IsNullOrWhiteSpace(tenant.Distribution.Model))
        {
            errors.Add("'distribution.model' is required.");
        }

        if (tenant.Theme is null || string.IsNullOrWhiteSpace(tenant.Theme.PrimaryColor))
        {
            errors.Add("'theme.primaryColor' is required.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid tenant configuration '{Path.GetFileName(configPath)}':{Environment.NewLine}- " +
                string.Join($"{Environment.NewLine}- ", errors));
        }
    }
}
