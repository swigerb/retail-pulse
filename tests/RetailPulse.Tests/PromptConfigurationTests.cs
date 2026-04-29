using FluentAssertions;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.Tests;

public class PromptConfigurationTests
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    [Fact]
    public void ValidYaml_ParsesCorrectly()
    {
        var yaml = """
            agents:
              test-agent:
                name: "Test Agent"
                model: "gpt-4o"
                system_prompt: "You are a test agent."
                temperature: 0.5
                tools:
                  - ToolA
                  - ToolB
            """;

        var config = Deserializer.Deserialize<PromptConfiguration>(yaml);

        config.Agents.Should().ContainKey("test-agent");
        var agent = config.Agents["test-agent"];
        agent.Name.Should().Be("Test Agent");
        agent.Model.Should().Be("gpt-4o");
        agent.SystemPrompt.Should().Be("You are a test agent.");
        agent.Temperature.Should().Be(0.5);
        agent.Tools.Should().HaveCount(2);
        agent.Tools.Should().Contain("ToolA");
        agent.Tools.Should().Contain("ToolB");
    }

    [Fact]
    public void AgentDefinition_MissingFields_UseDefaults()
    {
        var yaml = """
            agents:
              minimal:
                name: "Minimal Agent"
            """;

        var config = Deserializer.Deserialize<PromptConfiguration>(yaml);
        var agent = config.Agents["minimal"];

        agent.Name.Should().Be("Minimal Agent");
        agent.Model.Should().Be("gpt-4o");
        agent.SystemPrompt.Should().BeEmpty();
        agent.Temperature.Should().Be(0.7);
        agent.Tools.Should().BeEmpty();
    }

    [Fact]
    public void AgentDefinition_HasExpectedFields()
    {
        var agent = new AgentDefinition
        {
            Name = "Test",
            Model = "gpt-4o",
            SystemPrompt = "prompt",
            Temperature = 0.5,
            Tools = ["ToolA"]
        };

        agent.Name.Should().NotBeNullOrEmpty();
        agent.Model.Should().NotBeNullOrEmpty();
        agent.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.Tools.Should().NotBeEmpty();
    }

    [Fact]
    public void ActualPromptsYaml_LoadsWithoutErrors()
    {
        var projectDir = FindProjectRoot();
        var promptsPath = Path.Combine(projectDir, "src", "RetailPulse.Api", "prompts.yaml");

        File.Exists(promptsPath).Should().BeTrue(
            $"prompts.yaml must exist at {promptsPath} — silent skip would mask a missing config file");

        var yaml = File.ReadAllText(promptsPath);
        var config = Deserializer.Deserialize<PromptConfiguration>(yaml);

        config.Should().NotBeNull();
        config.Agents.Should().NotBeEmpty();

        config.Agents.Should().ContainKey("retail-pulse");
        var agent = config.Agents["retail-pulse"];
        agent.Name.Should().Be("Retail Pulse Agent");
        agent.Model.Should().NotBeNullOrEmpty();
        agent.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.Tools.Should().NotBeEmpty();
        agent.Tools.Should().Contain("GetDepletionStats");
        agent.Tools.Should().Contain("GetFieldSentiment");
    }

    [Fact]
    public void ActualPromptsYaml_ContainsTenantPlaceholders()
    {
        var projectDir = FindProjectRoot();
        var promptsPath = Path.Combine(projectDir, "src", "RetailPulse.Api", "prompts.yaml");

        File.Exists(promptsPath).Should().BeTrue(
            $"prompts.yaml must exist at {promptsPath}");

        var yaml = File.ReadAllText(promptsPath);
        var config = Deserializer.Deserialize<PromptConfiguration>(yaml);
        var agent = config.Agents["retail-pulse"];

        // Prompt should contain tenant placeholders before resolution
        agent.SystemPrompt.Should().Contain("{tenant.company}");
        agent.SystemPrompt.Should().Contain("{tenant.industry}");
        agent.SystemPrompt.Should().Contain("{tenant.brands}");
        agent.SystemPrompt.Should().Contain("{tenant.regions}");
    }

    [Fact]
    public void TenantPlaceholders_ResolveCorrectly()
    {
        var projectDir = FindProjectRoot();
        var promptsPath = Path.Combine(projectDir, "src", "RetailPulse.Api", "prompts.yaml");
        var tenantPath = Path.Combine(projectDir, "tenant.yaml");

        File.Exists(promptsPath).Should().BeTrue($"prompts.yaml must exist at {promptsPath}");
        File.Exists(tenantPath).Should().BeTrue($"tenant.yaml must exist at {tenantPath}");

        var yaml = File.ReadAllText(promptsPath);
        var config = Deserializer.Deserialize<PromptConfiguration>(yaml);
        var agent = config.Agents["retail-pulse"];

        // Load tenant config via the production provider so we exercise the actual
        // deserialization path (CamelCase naming, defaults, etc).
        var tenant = new RetailPulse.Contracts.FileTenantProvider(tenantPath).GetTenant();

        // Resolve placeholders. Note: the resolution logic mirrors Program.cs —
        // ideally this should be extracted into a reusable helper in source code
        // so tests don't reimplement it. Tracked separately.
        var resolved = ResolveTenantPlaceholders(agent.SystemPrompt, tenant);

        // Resolved prompt should contain tenant values
        resolved.Should().Contain("Apex Brands");
        resolved.Should().Contain("Spirits & Beverages");
        resolved.Should().Contain("Sierra Gold Tequila");
        resolved.Should().Contain("Northeast");

        // No Bacardi/Patrón references should remain
        resolved.Should().NotContain("Bacardi");
        resolved.Should().NotContain("Patrón");
        resolved.Should().NotContain("Patron");
        resolved.Should().NotContain("Grey Goose");
        resolved.Should().NotContain("Cazadores");

        // No unresolved placeholders should remain
        resolved.Should().NotContain("{tenant.");
    }

    [Fact]
    public void PromptsYaml_NoBacardiReferences()
    {
        var projectDir = FindProjectRoot();
        var promptsPath = Path.Combine(projectDir, "src", "RetailPulse.Api", "prompts.yaml");

        File.Exists(promptsPath).Should().BeTrue(
            $"prompts.yaml must exist at {promptsPath}");

        var rawYaml = File.ReadAllText(promptsPath);

        rawYaml.Should().NotContain("Bacardi");
        rawYaml.Should().NotContain("Patrón");
        rawYaml.Should().NotContain("Grey Goose");
        rawYaml.Should().NotContain("Cazadores");
        rawYaml.Should().NotContain("Angel's Envy");
    }

    /// <summary>
    /// Mirrors the placeholder resolution chain in <c>Program.cs</c>. Kept private
    /// so when the production logic moves to a helper class, this test can swap
    /// to call it directly.
    /// </summary>
    private static string ResolveTenantPlaceholders(string template, TenantConfiguration tenant)
    {
        return template
            .Replace("{tenant.company}", tenant.Company)
            .Replace("{tenant.industry}", tenant.Industry)
            .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
            .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
            .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
            .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
            .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));
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
