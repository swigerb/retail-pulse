using FluentAssertions;
using RetailPulse.Api.Packs;

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
    public void SeedFingerprint_IsDeterministicAcrossLoads()
    {
        string first = PackContentFingerprint.ComputePackFingerprint(Load());
        string second = PackContentFingerprint.ComputePackFingerprint(Load());

        first.Should().NotBeNullOrWhiteSpace();
        second.Should().Be(first);
    }
}
