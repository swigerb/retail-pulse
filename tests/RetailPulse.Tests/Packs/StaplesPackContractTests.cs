using FluentAssertions;
using RetailPulse.Api.Models;
using RetailPulse.Api.Packs;
using RetailPulse.Contracts;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Targeted contract coverage for the unofficial, synthetic Staples demo
/// pack (issue #325).
/// </summary>
public sealed class StaplesPackContractTests
{
    private const string PackName = "staples-retail";

    private static LoadedPack Load() =>
        PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load(PackName);

    private static IEnumerable<string> AllTaskText(LoadedPack pack) =>
        pack.StartingTasks.SelectMany(c => c.Tasks).SelectMany(t => new[] { t.Name, t.Prompt });

    [Fact]
    public void Pack_LoadsWithExpectedKey()
    {
        LoadedPack pack = Load();

        pack.Name.Should().Be(PackName);
        pack.Metadata.Key.Should().Be("staples-retail");
        pack.Agents.Agents.Should().ContainKey("router");
        pack.Agents.Agents.Should().ContainKey("memory-management");
    }

    [Fact]
    public void Disclosure_IsPresentInAttributionAndDisclosureDoc()
    {
        LoadedPack pack = Load();
        PackKnowledgeDocument doc = pack.KnowledgeDocuments
            .Single(d => d.RelativePath.Contains("staples-demo-disclosure", StringComparison.OrdinalIgnoreCase));

        foreach (string text in new[] { pack.Metadata.Attribution, doc.Content })
        {
            text.Should().ContainEquivalentOf("unofficial");
            text.Should().ContainEquivalentOf("no affiliation");
            text.Should().ContainEquivalentOf("synthetic");
            text.Should().ContainEquivalentOf("trademarks belong to");
        }
    }

    [Fact]
    public void StartingTasks_IncludeGovernanceAndApprovalPrompts()
    {
        LoadedPack pack = Load();
        string[] prompts = [.. pack.StartingTasks.SelectMany(c => c.Tasks).Select(t => t.Prompt)];

        prompts.Should().Contain(p => p.Contains("AI gateway", StringComparison.OrdinalIgnoreCase)
            && p.Contains("content safety", StringComparison.OrdinalIgnoreCase)
            && p.Contains("audit", StringComparison.OrdinalIgnoreCase));
        prompts.Should().Contain(p => p.Contains("request approval", StringComparison.OrdinalIgnoreCase)
            && p.Contains("quote", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void QuoteApprovalAgent_HasRequestApprovalTool()
    {
        LoadedPack pack = Load();

        pack.Agents.Agents["quote-approval"].Tools.Should().Contain("RequestApproval");
    }

    [Fact]
    public void PromptsAndTasks_UseRetailerNativeWording()
    {
        LoadedPack pack = Load();

        IEnumerable<string> promptText = pack.Agents.Agents.Values
            .Select(a => a.SystemPrompt)
            .Concat(AllTaskText(pack));

        promptText.Should().NotContain(t => t.Contains("depletion", StringComparison.OrdinalIgnoreCase));
        pack.StartingTasks.SelectMany(c => c.Tasks).Select(t => t.Prompt)
            .Should().Contain(p => p.Contains("sell-through", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InventoryInstructions_DistinguishVariants_AndPreserveUserScope()
    {
        LoadedPack pack = Load();
        BrandConfig brand = pack.Tenant.Brands.Single(b => b.Name == "Contract Catalog");
        brand.Category.Should().Be("Office Essentials");
        brand.Variants.Should().Contain("Paper & Toner");
        AgentDefinition definition = pack.Agents.Agents["fulfillment-ops"];
        definition.Tools.Should().Contain("GetInventoryLevels");
        string prompt = string.Join(" ", definition.SystemPrompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        prompt.Should().Contain("A variant is not a brand or category");
        prompt.Should().Contain("brand=\"Contract Catalog\", category=\"Office Essentials\", variant=\"Paper & Toner\"");
        prompt.Should().Contain("omit region and status");
        prompt.Should().Contain("not unconditional scenario filters");
        prompt.Should().Contain("preserve every explicit user brand, category, variant, region, status, threshold, and timeframe");
        prompt.Should().Contain("Never replace an explicit conflicting filter");
        prompt.Should().Contain("Apply a requested days-of-supply threshold");
        prompt.Should().Contain("current snapshot, not historical inventory");
        prompt.Should().Contain("Do not broaden filters, retry each region, or substitute reference knowledge figures");
        pack.StartingTasks.SelectMany(c => c.Tasks)
            .Single(t => t.Name == "Paper & toner days of supply").Prompt
            .Should().Be("Show days of supply for Paper & Toner by region");
    }

    [Fact]
    public void SeedFingerprint_IsDeterministicAcrossLoads()
    {
        string first = PackContentFingerprint.ComputePackFingerprint(Load());
        string second = PackContentFingerprint.ComputePackFingerprint(Load());

        first.Should().NotBeNullOrWhiteSpace();
        second.Should().Be(first);
    }
}
