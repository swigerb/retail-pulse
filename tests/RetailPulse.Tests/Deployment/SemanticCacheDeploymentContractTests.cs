using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>Issue #338 — opt-in Azure Managed Redis vector cache for APIM semantic caching.</summary>
public class SemanticCacheDeploymentContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. parts]));

    [Fact]
    public void Module_UsesManagedRedisWithSearchModuleAndLowestSku()
    {
        string m = Read("infra", "modules", "managed-redis-cache.bicep");
        m.Should().Contain("Microsoft.Cache/redisEnterprise@");
        m.Should().Contain("name: 'RediSearch'");
        m.Should().Contain("clusteringPolicy: 'EnterpriseCluster'");
        m.Should().Contain("evictionPolicy: 'NoEviction'");
        m.Should().Contain("param skuName string = 'Balanced_B0'");
    }

    [Fact]
    public void Module_IsSecureByDefault()
    {
        string m = Read("infra", "modules", "managed-redis-cache.bicep");
        m.Should().Contain("accessKeysAuthentication: 'Disabled'");
        m.Should().Contain("minimumTlsVersion: '1.2'");
        m.Should().Contain("clientProtocol: 'Encrypted'");
        m.Should().Contain("param publicNetworkAccess string = 'Disabled'");
        m.Should().Contain("type: 'SystemAssigned'");
        m.Should().Contain("accessPolicyAssignments");
        m.Should().Contain("Microsoft.Network/privateEndpoints");
        m.Should().NotContain("listKeys(");
        m.Should().NotContain("primaryKey");
        m.Should().NotContain("accessKeys");
    }

    [Fact]
    public void Module_ExposesNoSecretOutputs()
    {
        string m = Read("infra", "modules", "managed-redis-cache.bicep");
        foreach (string line in m.Split('\n').Where(l => l.StartsWith("output ")))
            line.ToLowerInvariant().Should().NotContainAny("key", "password", "connectionstring", "secret");
    }

    [Fact]
    public void Main_IsDisabledByDefaultAndConditional()
    {
        string main = Read("infra", "main.bicep");
        main.Should().Contain("param semanticCacheEnabled bool = false");
        main.Should().Contain("param semanticCacheSku string = 'Balanced_B0'");
        main.Should().Contain("param semanticCacheTtlSeconds int = 300");
        main.Should().Contain("param semanticCachePublicNetworkAccess string = 'Disabled'");
        main.Should().Contain("if (semanticCacheEnabled)");
        main.Should().Contain("semanticCacheEnabled ? semanticCache!.outputs.name : ''");
        main.Should().Contain("AZURE_SEMANTIC_CACHE_RESOURCE_ID");
    }

    [Fact]
    public void BicepParam_ReadsToggleWithFalseDefault()
    {
        string p = Read("infra", "main.bicepparam");
        p.Should().Contain("readEnvironmentVariable('AZURE_SEMANTIC_CACHE_ENABLED', 'false')");
        p.Should().Contain("readEnvironmentVariable('AZURE_SEMANTIC_CACHE_SKU', 'Balanced_B0')");
    }

    [Fact]
    public void Docs_StateCostClassAndTeardown()
    {
        string d = Read("docs", "deployment-azd.md");
        d.Should().Contain("Optional semantic cache");
        d.Should().Contain("Cost class");
        d.Should().Contain("az redisenterprise delete");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root not found");
    }
}
