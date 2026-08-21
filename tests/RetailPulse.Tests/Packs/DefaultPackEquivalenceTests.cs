using FluentAssertions;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Models;
using RetailPulse.Api.Packs;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Foundation guarantee for issue #108: extracting the sample tenant
/// into <c>packs/default</c> is a no-op. The default pack MUST reproduce
/// the legacy tenant.yaml, prompts.yaml, and knowledge corpus byte-equal
/// (line endings normalized) so downstream wiring can flip to
/// PackLoader without observable behaviour change.
/// </summary>
public sealed class DefaultPackEquivalenceTests
{
    private static LoadedPack LoadDefault() =>
        PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load("default");

    [Fact]
    public void DefaultPack_TenantMatchesLegacyTenantYaml()
    {
        LoadedPack pack = LoadDefault();

        FileTenantProvider legacy = new(Path.Combine(PackTestPaths.RepoRoot, "tenant.yaml"));
        TenantConfiguration expected = legacy.GetTenant();
        TenantConfiguration actual = pack.Tenant;

        actual.Company.Should().Be(expected.Company);
        actual.Industry.Should().Be(expected.Industry);
        actual.Description.Should().Be(expected.Description);

        actual.Brands.Should().HaveCount(expected.Brands.Count);
        for (int i = 0; i < expected.Brands.Count; i++)
        {
            actual.Brands[i].Name.Should().Be(expected.Brands[i].Name);
            actual.Brands[i].Category.Should().Be(expected.Brands[i].Category);
            actual.Brands[i].PriceSegment.Should().Be(expected.Brands[i].PriceSegment);
            actual.Brands[i].Variants.Should().Equal(expected.Brands[i].Variants);
        }

        actual.Regions.Should().Equal(expected.Regions);
        actual.Channels.Should().Equal(expected.Channels);

        actual.Theme.Should().NotBeNull();
        actual.Theme.PrimaryColor.Should().Be(expected.Theme.PrimaryColor);
        actual.Theme.AccentColor.Should().Be(expected.Theme.AccentColor);
        actual.Theme.LogoPath.Should().Be(expected.Theme.LogoPath);
        actual.Theme.FontFamily.Should().Be(expected.Theme.FontFamily);

        actual.Distribution.Should().NotBeNull();
        actual.Distribution.Model.Should().Be(expected.Distribution.Model);
        actual.Distribution.DistributorTypes.Should().Equal(expected.Distribution.DistributorTypes);
    }

    [Fact]
    public void DefaultPack_AgentsMatchLegacyPromptsYaml()
    {
        LoadedPack pack = LoadDefault();

        PromptConfiguration expected =
            RetailPulseAgent.LoadPrompts(Path.Combine(PackTestPaths.RepoRoot, "src", "RetailPulse.Api", "prompts.yaml"));
        PromptConfiguration actual = pack.Agents;

        actual.Agents.Keys.Should().BeEquivalentTo(expected.Agents.Keys);

        foreach ((string key, AgentDefinition expectedAgent) in expected.Agents)
        {
            AgentDefinition actualAgent = actual.Agents[key];

            actualAgent.Name.Should().Be(expectedAgent.Name, "agent {0}", key);
            actualAgent.Model.Should().Be(expectedAgent.Model, "agent {0}", key);
            actualAgent.SystemPrompt.Should().Be(expectedAgent.SystemPrompt, "agent {0}", key);
            actualAgent.Temperature.Should().Be(expectedAgent.Temperature, "agent {0}", key);
            actualAgent.Tools.Should().Equal(expectedAgent.Tools, "agent {0}", key);
            actualAgent.DisplayName.Should().Be(expectedAgent.DisplayName, "agent {0}", key);
            actualAgent.Intents.Should().Equal(expectedAgent.Intents, "agent {0}", key);
            actualAgent.KeywordFastPaths.Should().Equal(expectedAgent.KeywordFastPaths, "agent {0}", key);
            actualAgent.FallbackReply.Should().Be(expectedAgent.FallbackReply, "agent {0}", key);
            actualAgent.CouncilParticipant.Should().Be(expectedAgent.CouncilParticipant, "agent {0}", key);
            actualAgent.ScorecardDimension.Should().Be(expectedAgent.ScorecardDimension, "agent {0}", key);
            actualAgent.ScorecardWeight.Should().Be(expectedAgent.ScorecardWeight, "agent {0}", key);
            actualAgent.Role.Should().Be(expectedAgent.Role, "agent {0}", key);
            actualAgent.Prefetchable.Should().Be(expectedAgent.Prefetchable, "agent {0}", key);
            actualAgent.UseKnowledgeBase.Should().Be(expectedAgent.UseKnowledgeBase, "agent {0}", key);
            actualAgent.KnowledgeBaseName.Should().Be(expectedAgent.KnowledgeBaseName, "agent {0}", key);

            // Key defaulting: legacy loader relies on Program.cs
            // normalization; PackLoader applies the same default at
            // load time. Either the YAML declared a Key or the section
            // name became the Key.
            string expectedKey = string.IsNullOrWhiteSpace(expectedAgent.Key) ? key : expectedAgent.Key;
            actualAgent.Key.Should().Be(expectedKey, "agent {0}", key);
        }
    }

    [Fact]
    public void DefaultPack_KnowledgeMatchesSeederCorpus()
    {
        LoadedPack pack = LoadDefault();

        IReadOnlyList<(string Title, string Source, string Content)> expected =
            KnowledgeBaseSeeder.GetSampleDocuments();

        pack.KnowledgeDocuments.Should().HaveCount(expected.Count);

        foreach ((string _, string source, string content) in expected)
        {
            PackKnowledgeDocument doc =
                pack.KnowledgeDocuments.Single(d => d.Source == source);

            // Title comes from the markdown H1 in the shipped pack; the
            // seeder's own short label is metadata used only inside the
            // in-memory knowledge base for dedup. The equivalence
            // contract is on the ingest identity (Source) and the
            // grounding payload (Content) — downstream composition
            // decides how to project pack knowledge docs into the store.
            doc.Title.Should().NotBeNullOrWhiteSpace("knowledge doc {0} title", source);
            Normalize(doc.Content).Should().Be(Normalize(content), "knowledge doc {0} content", source);
        }
    }

    [Fact]
    public void DefaultPack_StartingTasksExposeExpectedCategories()
    {
        LoadedPack pack = LoadDefault();

        pack.StartingTasks.Should().HaveCount(7);
        pack.StartingTasks.Select(c => c.Id).Should().Equal(
            "general",
            "grocery",
            "qsr",
            "home-improvement",
            "office-supply",
            "furniture",
            "charts");
        pack.StartingTasks.Should().OnlyContain(c => c.Prompts.Count > 0);
        pack.StartingTasks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Label));
    }

    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n").TrimEnd('\n');
}
