using FluentAssertions;
using RetailPulse.Api.Packs;

namespace RetailPulse.Tests.Packs;

/// <summary>Targeted contract coverage for the unofficial synthetic Lowe's pack (issue #326).</summary>
public sealed class LowesRetailPackContractTests
{
    private const string PackKey = "lowes-retail";

    private static LoadedPack Load() =>
        PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load(PackKey);

    [Fact]
    public void Pack_LoadsUnderExpectedKey()
    {
        LoadedPack pack = Load();

        pack.Name.Should().Be(PackKey);
        pack.Metadata.Key.Should().Be(PackKey);
        pack.Agents.Agents.Should().NotBeEmpty();
    }

    [Fact]
    public void Pack_CarriesDisclosure()
    {
        LoadedPack pack = Load();
        string text = (pack.Metadata.Attribution + " " + pack.Tenant.Description).ToLowerInvariant();

        text.Should().Contain("unofficial");
        text.Should().Contain("affiliation");
        text.Should().Contain("synthetic");
        pack.Metadata.Attribution.ToLowerInvariant().Should().Contain("trademark");
    }

    [Fact]
    public void StartingTasks_CoverInsightGovernanceAndApproval()
    {
        LoadedPack pack = Load();
        string[] ids = [.. pack.StartingTasks.Select(c => c.Id)];

        ids.Should().Contain(["storm-demand", "gateway-governance", "safety-approval"]);
        pack.StartingTasks.Single(c => c.Id == "gateway-governance").Tasks
            .Should().Contain(t => t.Prompt.Contains("governance", StringComparison.OrdinalIgnoreCase));
        pack.StartingTasks.Single(c => c.Id == "safety-approval").Tasks
            .Should().Contain(t => t.Prompt.Contains("human approval", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FulfillmentInstructions_KeepPlywoodBrandCategoryAndVariantDistinct()
    {
        LoadedPack pack = Load();
        string prompt = pack.Agents.Agents["fulfillment-continuity"].SystemPrompt;

        prompt.Should().Contain("Building Materials / Lumber & Building: Plywood");
        prompt.Should().Contain("brand=\"Building Materials\"");
        prompt.Should().Contain("category=\"Lumber & Building\"");
        prompt.Should().Contain("variant=\"Plywood\"");
        prompt.Should().Contain("preserve every");
        prompt.Should().Contain("Never replace an explicit conflicting filter");
        prompt.Should().Contain("Do not broaden filters");
        pack.StartingTasks.Single(category => category.Id == "storm-demand").Tasks
            .Single(task => task.Name == "Plywood days of supply").Prompt
            .Should().Be("Show days of supply for plywood across all regions");
    }

    [Fact]
    public void Tenant_HasNoLogoPath_AndUsesNeutralColors()
    {
        LoadedPack pack = Load();

        pack.Tenant.Theme.LogoPath.Should().BeNullOrEmpty();
        File.ReadAllText(Path.Combine(pack.RootPath, "pack.yaml")).Should().NotContain("logoPath");
        pack.Tenant.Theme.PrimaryColor.Should().NotBeEquivalentTo("#004990");
        pack.Tenant.Theme.AccentColor.Should().NotBeEquivalentTo("#C8102E");
    }

    [Fact]
    public void UserFacingText_UsesRetailerNativeWording()
    {
        LoadedPack pack = Load();
        string[] banned = ["depletion", "sell-in", "distributor", "retailer satisfaction"];

        string routerPrompt = pack.Agents.Agents["router"].SystemPrompt;
        var texts = new List<string> { routerPrompt, pack.Tenant.Description, pack.Metadata.Attribution };
        texts.AddRange(pack.StartingTasks.SelectMany(c => c.Tasks.Select(t => t.Name + " " + t.Prompt)));
        texts.AddRange(pack.KnowledgeDocuments.Select(d => d.Content));

        foreach (string term in banned)
        {
            texts.Should().NotContain(t => t.Contains(term, StringComparison.OrdinalIgnoreCase),
                "'{0}' is not natural retail wording", term);
        }
    }

    [Fact]
    public void Seed_IsDeterministicAcrossLoads()
    {
        string first = PackContentFingerprint.ComputePackFingerprint(Load());
        string second = PackContentFingerprint.ComputePackFingerprint(Load());

        second.Should().Be(first);
        Load().Seed.Should().NotBeNull();
    }
}
