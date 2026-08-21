using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Contract tests for the MCP-side pack loader (issue #108).
/// Blocker #1 requires MCP to reject duplicate-key pack.yaml documents
/// so a doubled tenant block never silently ships through the MCP
/// deployment path just because the API-side loader was skipped. This
/// suite also covers the seed manifest surface: MCP LoadFromPackDirectory
/// must fail fast when seed/scenario.yaml is missing or malformed.
/// </summary>
public sealed class McpPackTenantLoaderTests : IDisposable
{
    private readonly List<string> _fixtures = [];

    private string NewFixture(string name)
    {
        string dir = PackTestPaths.CreateFixtureDirectory("mcp-" + name);
        _fixtures.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _fixtures)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void LoadFromPackYaml_DuplicateTenantKey_IsRejected()
    {
        // Kroger's API PackLoader rejects duplicate-key pack.yaml files
        // via WithDuplicateKeyChecking; MCP must do the same or an
        // operator who accidentally pastes a second 'tenant:' block will
        // see only the first block on the API side and only the second
        // on the MCP side. Parity is enforced here.
        string dir = NewFixture("dup-tenant");
        string yamlPath = Path.Combine(dir, "pack.yaml");
        File.WriteAllText(yamlPath, """
            metadata:
              key: dup-tenant
              displayName: "Dup Tenant"
              version: "0.1.0"
            tenant:
              company: "Company A"
              industry: "Retail"
              brands:
                - name: "Widget"
                  category: "General"
                  variants: ["Standard"]
                  priceSegment: "Standard"
              regions: ["Region One"]
              channels: ["Direct"]
              theme:
                primaryColor: "#000000"
                accentColor: "#ffffff"
              distribution:
                model: "Direct"
                distributorTypes: ["Retailer"]
            tenant:
              company: "Company B"
              industry: "Retail"
              brands:
                - name: "Gadget"
                  category: "General"
                  variants: ["Standard"]
                  priceSegment: "Standard"
              regions: ["Region Two"]
              channels: ["Direct"]
              theme:
                primaryColor: "#111111"
                accentColor: "#eeeeee"
              distribution:
                model: "Direct"
                distributorTypes: ["Retailer"]
            """);

        Action act = () => PackTenantLoader.LoadFromPackYaml(yamlPath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate*", "MCP must surface the same duplicate-key error the API pack loader does");
    }

    [Fact]
    public void LoadFromPackYaml_DuplicateBrandsKey_IsRejected()
    {
        string dir = NewFixture("dup-brands");
        string yamlPath = Path.Combine(dir, "pack.yaml");
        File.WriteAllText(yamlPath, """
            metadata:
              key: dup-brands
              displayName: "Dup Brands"
              version: "0.1.0"
            tenant:
              company: "Test Co"
              industry: "Retail"
              brands:
                - name: "Alpha"
                  category: "General"
                  variants: ["Standard"]
                  priceSegment: "Standard"
              brands:
                - name: "Beta"
                  category: "General"
                  variants: ["Standard"]
                  priceSegment: "Standard"
              regions: ["Region"]
              channels: ["Direct"]
              theme:
                primaryColor: "#000000"
                accentColor: "#ffffff"
              distribution:
                model: "Direct"
                distributorTypes: ["Retailer"]
            """);

        Action act = () => PackTenantLoader.LoadFromPackYaml(yamlPath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate*");
    }

    [Fact]
    public void LoadFromPackDirectory_DuplicateTenantKey_IsRejected()
    {
        // Same duplicate-key rejection when loading through the full
        // pack-directory surface — a poisoned pack.yaml must not sneak
        // through the seed-manifest path either.
        string dir = NewFixture("dup-dir-tenant");
        WriteValidSeed(dir);
        File.WriteAllText(Path.Combine(dir, "pack.yaml"), """
            metadata:
              key: dup-dir
              displayName: "Dup Dir"
              version: "0.1.0"
            tenant:
              company: "Company A"
              industry: "Retail"
              brands:
                - name: "Widget"
                  category: "General"
                  variants: ["Standard"]
                  priceSegment: "Standard"
              regions: ["Region"]
              channels: ["Direct"]
              theme:
                primaryColor: "#000000"
                accentColor: "#ffffff"
              distribution:
                model: "Direct"
                distributorTypes: ["Retailer"]
            tenant:
              company: "Company B"
              industry: "Retail"
              brands:
                - name: "Gadget"
                  category: "General"
                  variants: ["Standard"]
                  priceSegment: "Standard"
              regions: ["Region"]
              channels: ["Direct"]
              theme:
                primaryColor: "#111111"
                accentColor: "#eeeeee"
              distribution:
                model: "Direct"
                distributorTypes: ["Retailer"]
            """);

        Action act = () => PackTenantLoader.LoadFromPackDirectory(dir);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate*");
    }

    [Fact]
    public void LoadFromPackDirectory_ValidPack_ExposesTenantAndSeed()
    {
        string dir = NewFixture("valid-full");
        WriteValidPackYaml(dir, "valid-full");
        WriteValidSeed(dir);

        PackLoadResult result = PackTenantLoader.LoadFromPackDirectory(dir);

        result.Tenant.GetTenant().Company.Should().Be("Test Co");
        result.Seed.Should().NotBeNull();
        result.Seed.Stores.Types.Should().ContainSingle().Which.Should().Be("Flagship");
        result.Seed.Promos.Types.Should().ContainSingle().Which.Name.Should().Be("Discount");
        result.PackYamlPath.Should().EndWith("pack.yaml");
        result.SeedDir.Should().EndWith("seed");
    }

    [Fact]
    public void LoadFromPackDirectory_MissingSeedManifest_FailsFast()
    {
        string dir = NewFixture("no-seed");
        WriteValidPackYaml(dir, "no-seed");
        // Intentionally no seed/ directory.

        Action act = () => PackTenantLoader.LoadFromPackDirectory(dir);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*seed manifest*");
    }

    [Fact]
    public void LoadFromPackDirectory_MalformedSeedManifest_FailsFast()
    {
        string dir = NewFixture("bad-seed");
        WriteValidPackYaml(dir, "bad-seed");
        string seedDir = Path.Combine(dir, "seed");
        Directory.CreateDirectory(seedDir);
        // Malformed YAML — a mapping node truncated by a stray tab.
        File.WriteAllText(Path.Combine(seedDir, "scenario.yaml"),
            "seasonality:\n  factors: [not-a-mapping\n");

        Action act = () => PackTenantLoader.LoadFromPackDirectory(dir);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*seed manifest*");
    }

    private static void WriteValidPackYaml(string dir, string key)
    {
        File.WriteAllText(Path.Combine(dir, "pack.yaml"), $$"""
            metadata:
              key: {{key}}
              displayName: "Valid"
              version: "0.1.0"
            tenant:
              company: "Test Co"
              industry: "Retail"
              brands:
                - name: "Widget"
                  category: "General"
                  variants: ["Standard"]
                  priceSegment: "Standard"
              regions: ["Region One"]
              channels: ["Direct"]
              theme:
                primaryColor: "#000000"
                accentColor: "#ffffff"
              distribution:
                model: "Direct"
                distributorTypes: ["Retailer"]
            """);
    }

    private static void WriteValidSeed(string dir)
    {
        string seedDir = Path.Combine(dir, "seed");
        Directory.CreateDirectory(seedDir);
        File.WriteAllText(Path.Combine(seedDir, "scenario.yaml"),
            PackLoaderTests.MinimalValidSeedYaml());
    }
}
