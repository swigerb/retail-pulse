using FluentAssertions;
using RetailPulse.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.Tests;

/// <summary>
/// Tests for tenant.yaml loading, validation, and edge cases.
/// Covers <see cref="FileTenantProvider"/> and the <see cref="TenantConfiguration"/> model.
/// </summary>
public class TenantConfigurationTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string WriteTempYaml(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tenant-test-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, contents);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ActualTenantYaml_LoadsSuccessfully()
    {
        var projectDir = FindProjectRoot();
        var tenantPath = Path.Combine(projectDir, "tenant.yaml");

        File.Exists(tenantPath).Should().BeTrue("tenant.yaml must exist at the repo root");

        var provider = new FileTenantProvider(tenantPath);
        var tenant = provider.GetTenant();

        tenant.Should().NotBeNull();
        tenant.Company.Should().NotBeNullOrWhiteSpace();
        tenant.Industry.Should().NotBeNullOrWhiteSpace();
        tenant.Brands.Should().NotBeEmpty();
        tenant.Regions.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidYaml_LoadsAllSections()
    {
        var path = WriteTempYaml("""
            company: "Test Co"
            industry: "Test Industry"
            description: "A test"
            brands:
              - name: "Brand A"
                category: "Tequila"
                variants: ["V1", "V2"]
                priceSegment: "Premium"
            regions:
              - "Region 1"
              - "Region 2"
            channels:
              - "On-Premise"
            theme:
              primaryColor: "#111111"
              accentColor: "#222222"
              logoPath: "logo.png"
              fontFamily: "Inter"
            distribution:
              model: "Three-Tier"
              distributorTypes:
                - "Distributor"
            """);

        var tenant = new FileTenantProvider(path).GetTenant();

        tenant.Company.Should().Be("Test Co");
        tenant.Industry.Should().Be("Test Industry");
        tenant.Brands.Should().HaveCount(1);
        tenant.Brands[0].Name.Should().Be("Brand A");
        tenant.Brands[0].Variants.Should().HaveCount(2);
        tenant.Regions.Should().HaveCount(2);
        tenant.Channels.Should().ContainSingle();
        tenant.Theme.PrimaryColor.Should().Be("#111111");
        tenant.Distribution.Model.Should().Be("Three-Tier");
    }

    [Fact]
    public void MissingTenantYaml_ThrowsFileNotFound()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.yaml");

        Action act = () => new FileTenantProvider(bogus);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void EmptyBrandsArray_ThrowsValidationError()
    {
        var path = WriteTempYaml("""
            company: "Test Co"
            industry: "Test"
            brands: []
            regions:
              - "R1"
            """);

        Action act = () => new FileTenantProvider(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*brands*");
    }

    [Fact]
    public void EmptyRegionsArray_ThrowsValidationError()
    {
        var path = WriteTempYaml("""
            company: "Test Co"
            industry: "Test"
            brands:
              - name: "B"
                category: "C"
                variants: ["v"]
                priceSegment: "Premium"
            regions: []
            """);

        Action act = () => new FileTenantProvider(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*regions*");
    }

    [Fact]
    public void EmptyCompany_ThrowsValidationError()
    {
        // The TenantConfiguration model defaults Company to a non-empty string,
        // so simply omitting the key won't fail validation. Passing an empty
        // value, however, triggers the IsNullOrWhiteSpace check.
        var path = WriteTempYaml("""
            company: ""
            industry: "Test"
            brands:
              - name: "B"
                category: "C"
                variants: ["v"]
                priceSegment: "Premium"
            regions: ["R1"]
            """);

        Action act = () => new FileTenantProvider(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*company*");
    }

    [Fact]
    public void BrandWithoutName_ThrowsValidationError()
    {
        var path = WriteTempYaml("""
            company: "Test Co"
            industry: "Test"
            brands:
              - name: ""
                category: "C"
                variants: ["v"]
                priceSegment: "Premium"
            regions: ["R1"]
            """);

        Action act = () => new FileTenantProvider(path);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MissingDistributionSection_UsesDefault()
    {
        var path = WriteTempYaml("""
            company: "Test Co"
            industry: "Test"
            brands:
              - name: "B"
                category: "C"
                variants: ["v"]
                priceSegment: "Premium"
            regions: ["R1"]
            """);

        var tenant = new FileTenantProvider(path).GetTenant();

        tenant.Distribution.Should().NotBeNull("missing distribution section should fall back to default model");
        tenant.Distribution.Model.Should().Be("Three-Tier");
        tenant.Distribution.DistributorTypes.Should().NotBeEmpty();
    }

    [Fact]
    public void MissingThemeSection_UsesDefault()
    {
        var path = WriteTempYaml("""
            company: "Test Co"
            industry: "Test"
            brands:
              - name: "B"
                category: "C"
                variants: ["v"]
                priceSegment: "Premium"
            regions: ["R1"]
            """);

        var tenant = new FileTenantProvider(path).GetTenant();

        tenant.Theme.Should().NotBeNull();
        tenant.Theme.PrimaryColor.Should().StartWith("#");
        tenant.Theme.AccentColor.Should().StartWith("#");
    }

    [Fact]
    public void CamelCaseKeys_AreRecognized()
    {
        // priceSegment is camelCase; the production deserializer uses CamelCaseNamingConvention
        var path = WriteTempYaml("""
            company: "Test Co"
            industry: "Test"
            brands:
              - name: "B"
                category: "C"
                variants: ["v"]
                priceSegment: "Ultra-Premium"
            regions: ["R1"]
            """);

        var tenant = new FileTenantProvider(path).GetTenant();

        tenant.Brands[0].PriceSegment.Should().Be("Ultra-Premium");
    }

    [Fact]
    public void SnakeCaseKeys_AreNotRecognizedByCamelCaseDeserializer()
    {
        // Production uses CamelCaseNamingConvention with IgnoreUnmatchedProperties.
        // snake_case keys should be silently ignored, leaving the property at default.
        var path = WriteTempYaml("""
            company: "Test Co"
            industry: "Test"
            brands:
              - name: "B"
                category: "C"
                variants: ["v"]
                price_segment: "Ultra-Premium"
            regions: ["R1"]
            """);

        var tenant = new FileTenantProvider(path).GetTenant();
        tenant.Brands[0].PriceSegment.Should().Be("Premium",
            "snake_case keys are not bound by the CamelCase deserializer used in production");
    }

    [Fact]
    public void TenantConfiguration_DefaultsAreSafe()
    {
        var tenant = new TenantConfiguration();

        tenant.Brands.Should().NotBeNull().And.BeEmpty();
        tenant.Regions.Should().NotBeNull().And.BeEmpty();
        tenant.Channels.Should().NotBeNull().And.BeEmpty();
        tenant.Theme.Should().NotBeNull();
        tenant.Distribution.Should().NotBeNull();
        tenant.Distribution.DistributorTypes.Should().NotBeNullOrEmpty();
    }

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "RetailPulse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
