using FluentAssertions;
using RetailPulse.Contracts;

namespace RetailPulse.Tests.Tenant;

/// <summary>
/// Sprint 4 cleanup — validates that <see cref="FileTenantProvider"/> rejects
/// tenant.yaml files that are missing required fields and accepts valid ones.
/// </summary>
public class TenantValidationTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    /// <summary>
    /// Minimal valid YAML that satisfies all validation rules.
    /// Individual tests remove one field at a time to verify the guard.
    /// </summary>
    private const string ValidYaml = """
        company: "Test Corp"
        industry: "Retail"
        brands:
          - name: "Brand A"
            category: "General"
            variants: ["Standard"]
            priceSegment: "Standard"
        regions:
          - "Northeast"
        channels:
          - "E-Commerce"
        theme:
          primaryColor: "#ABCDEF"
          accentColor: "#123456"
        distribution:
          model: "Direct"
          distributorTypes:
            - "Retailer"
        """;

    private string WriteTempYaml(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tenant-val-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, contents);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task MissingIndustry_FailsStartupValidation()
    {
        string yaml = ValidYaml.Replace("industry: \"Retail\"", "industry: \"\"");
        string path = WriteTempYaml(yaml);

        Func<FileTenantProvider> act = () => new FileTenantProvider(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*industry*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MissingChannels_FailsStartupValidation()
    {
        string yaml = ValidYaml
            .Replace("channels:", "# channels removed:")
            .Replace("  - \"E-Commerce\"", "  # removed");
        string path = WriteTempYaml(yaml);

        Func<FileTenantProvider> act = () => new FileTenantProvider(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*channels*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MissingDistributionModel_FailsStartupValidation()
    {
        string yaml = ValidYaml
            .Replace("distribution:", "# distribution removed:")
            .Replace("  model: \"Direct\"", "  # removed")
            .Replace("  distributorTypes:", "  # removed")
            .Replace("    - \"Retailer\"", "  # removed");
        string path = WriteTempYaml(yaml);

        Func<FileTenantProvider> act = () => new FileTenantProvider(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*distribution.model*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MissingThemePrimaryColor_FailsStartupValidation()
    {
        string yaml = ValidYaml
            .Replace("theme:", "# theme removed:")
            .Replace("  primaryColor: \"#ABCDEF\"", "  # removed")
            .Replace("  accentColor: \"#123456\"", "  # removed");
        string path = WriteTempYaml(yaml);

        Func<FileTenantProvider> act = () => new FileTenantProvider(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*primaryColor*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ValidTenantYaml_PassesValidation()
    {
        string path = WriteTempYaml(ValidYaml);

        var provider = new FileTenantProvider(path);
        TenantConfiguration tenant = provider.GetTenant();

        tenant.Should().NotBeNull();
        tenant.Company.Should().Be("Test Corp");
        tenant.Industry.Should().Be("Retail");
        tenant.Channels.Should().ContainSingle().Which.Should().Be("E-Commerce");
        tenant.Distribution.Model.Should().Be("Direct");
        tenant.Theme.PrimaryColor.Should().Be("#ABCDEF");
        await Task.CompletedTask;
    }
}
