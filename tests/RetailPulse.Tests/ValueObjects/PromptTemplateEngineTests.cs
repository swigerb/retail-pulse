using FluentAssertions;
using RetailPulse.Api.Models;
using RetailPulse.Api.Prompts;
using RetailPulse.Contracts;

namespace RetailPulse.Tests.ValueObjects;

public class PromptTemplateEngineTests
{
    private static TenantConfiguration CreateTestTenant() => new()
    {
        Company = "TestCo",
        Industry = "Retail",
        BrandsList =
        [
            new() { Name = "Brand A", VariantsList = ["V1", "V2"] },
            new() { Name = "Brand B", VariantsList = ["V3"] }
        ],
        RegionsList = ["Northeast", "West Coast"],
        Theme = new ThemeConfig { PrimaryColor = "#FF0000", AccentColor = "#00FF00" },
        Distribution = new DistributionConfig { Model = "Direct" }
    };

    [Fact]
    public void Hydrate_ReplacesAllTenantPlaceholders()
    {
        var engine = new PromptTemplateEngine(CreateTestTenant());
        var agentDef = new AgentDefinition
        {
            Name = "test-agent",
            SystemPrompt = "You work for {tenant.company} in {tenant.industry}. Brands: {tenant.brands}. Regions: {tenant.regions}. Colors: {tenant.primary_color}/{tenant.accent_color}. Model: {tenant.distribution_model}."
        };

        engine.Hydrate(agentDef);

        agentDef.SystemPrompt.Should().Contain("TestCo");
        agentDef.SystemPrompt.Should().Contain("Retail");
        agentDef.SystemPrompt.Should().Contain("Brand A (V1, V2)");
        agentDef.SystemPrompt.Should().Contain("Brand B (V3)");
        agentDef.SystemPrompt.Should().Contain("Northeast, West Coast");
        agentDef.SystemPrompt.Should().Contain("#FF0000");
        agentDef.SystemPrompt.Should().Contain("#00FF00");
        agentDef.SystemPrompt.Should().Contain("Direct");
        agentDef.SystemPrompt.Should().NotContain("{tenant.");
    }

    [Fact]
    public void Hydrate_WithNoPlaceholders_LeavesPromptUnchanged()
    {
        var engine = new PromptTemplateEngine(CreateTestTenant());
        var agentDef = new AgentDefinition
        {
            Name = "router",
            SystemPrompt = "You are a router. Classify intents."
        };

        engine.Hydrate(agentDef);

        agentDef.SystemPrompt.Should().Be("You are a router. Classify intents.");
    }

    [Fact]
    public void Hydrate_WithEmptySystemPrompt_ReturnsUnchanged()
    {
        var engine = new PromptTemplateEngine(CreateTestTenant());
        var agentDef = new AgentDefinition { Name = "empty", SystemPrompt = "" };

        engine.Hydrate(agentDef);

        agentDef.SystemPrompt.Should().BeEmpty();
    }

    [Fact]
    public void HydrateTemplate_ReplacesPlaceholdersInRawString()
    {
        var engine = new PromptTemplateEngine(CreateTestTenant());
        var result = engine.HydrateTemplate("Hello {tenant.company}!");
        result.Should().Be("Hello TestCo!");
    }

    [Fact]
    public void Hydrate_UsesDefaults_WhenOptionalFieldsNull()
    {
        var tenant = new TenantConfiguration
        {
            Company = "NullCo",
            Industry = "Tech",
            BrandsList = [],
            RegionsList = [],
            Theme = null!,
            Distribution = null!
        };

        var engine = new PromptTemplateEngine(tenant);
        var agentDef = new AgentDefinition
        {
            Name = "test",
            SystemPrompt = "Model: {tenant.distribution_model}, Color: {tenant.primary_color}/{tenant.accent_color}"
        };

        engine.Hydrate(agentDef);

        agentDef.SystemPrompt.Should().Contain("Three-Tier");
        agentDef.SystemPrompt.Should().Contain("#1A73E8");
        agentDef.SystemPrompt.Should().Contain("#FFC107");
    }
}
