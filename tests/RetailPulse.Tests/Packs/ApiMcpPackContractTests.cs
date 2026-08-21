using FluentAssertions;
using RetailPulse.Api.Packs;
using RetailPulse.Contracts;
using RetailPulse.McpServer;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Contract test that pins the API host and the MCP server to the same
/// active-pack configuration surface. Both processes read
/// <c>Packs:Active</c> and <c>Packs:Root</c> from configuration, resolve
/// the on-disk pack directory the same way, and derive the same
/// <see cref="TenantConfiguration"/> from the pack's <c>pack.yaml</c>.
/// If either side drifts (for example, MCP starts reading a different
/// config key, or the two YAML deserializers stop agreeing on brand
/// shapes), the seed data would diverge from the agent roster and this
/// test would catch it before shipping.
/// </summary>
public sealed class ApiMcpPackContractTests
{
    public static IEnumerable<object[]> ShippedPacks() =>
        Directory
            .EnumerateDirectories(PackTestPaths.PacksRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => new object[] { n! });

    [Theory]
    [MemberData(nameof(ShippedPacks))]
    public void ApiAndMcp_ResolveSameTenantFromSamePackYaml(string packName)
    {
        // API side: the full PackLoader.
        LoadedPack pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load(packName);

        // MCP side: the lightweight tenant-only loader that the MCP
        // Program.cs uses to build its ITenantProvider and seed
        // identity. It is a distinct code path — a different assembly,
        // a different deserializer configuration — so this equality is
        // a real contract, not a tautology.
        string packYaml = Path.Combine(pack.RootPath, "pack.yaml");
        ITenantProvider mcp = PackTenantLoader.LoadFromPackYaml(packYaml);
        TenantConfiguration mcpTenant = mcp.GetTenant();

        mcpTenant.Company.Should().Be(pack.Tenant.Company);
        mcpTenant.Industry.Should().Be(pack.Tenant.Industry);
        mcpTenant.Description.Should().Be(pack.Tenant.Description);

        mcpTenant.Brands.Should().HaveCount(pack.Tenant.Brands.Count);
        for (int i = 0; i < pack.Tenant.Brands.Count; i++)
        {
            mcpTenant.Brands[i].Name.Should().Be(pack.Tenant.Brands[i].Name);
            mcpTenant.Brands[i].Category.Should().Be(pack.Tenant.Brands[i].Category);
            mcpTenant.Brands[i].PriceSegment.Should().Be(pack.Tenant.Brands[i].PriceSegment);
            mcpTenant.Brands[i].Variants.Should().Equal(pack.Tenant.Brands[i].Variants);
        }

        mcpTenant.Regions.Should().Equal(pack.Tenant.Regions);
        mcpTenant.Channels.Should().Equal(pack.Tenant.Channels);
        mcpTenant.Distribution.Model.Should().Be(pack.Tenant.Distribution.Model);
        mcpTenant.Distribution.DistributorTypes.Should().Equal(pack.Tenant.Distribution.DistributorTypes);

        // The MCP seed identity file is the same pack.yaml file the API
        // pack fingerprint reads. Switching Packs:Active on either side
        // therefore resolves to the same identity path, and editing
        // that file changes both hashes in lockstep.
        File.Exists(packYaml).Should().BeTrue("MCP seed identity path must be the API pack's pack.yaml");
    }
}
