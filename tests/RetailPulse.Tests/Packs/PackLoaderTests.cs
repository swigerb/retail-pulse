using FluentAssertions;
using RetailPulse.Api.Packs;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Contract tests for <see cref="PackLoader"/>. The critical property is
/// aggregate reporting — a broken pack surfaces every issue in a single
/// exception so operators are not caught in a whack-a-mole loop.
/// </summary>
public sealed class PackLoaderTests : IDisposable
{
    private readonly List<string> _fixtures = [];

    private string NewFixture(string name)
    {
        string dir = PackTestPaths.CreateFixtureDirectory(name);
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
    public void Load_UnknownPack_ReportsAvailablePacks()
    {
        string root = NewFixture("empty-root");

        var loader = PackLoader.ForDirectory(root);

        Action act = () => loader.Load("does-not-exist");

        act.Should().Throw<PackValidationException>()
            .Which.Issues.Should().ContainSingle(i => i.Code == "pack.missing");
    }

    [Fact]
    public void Load_MissingPackYamlAndAgentsYaml_ReportsBothInOneException()
    {
        string root = NewFixture("both-missing");
        Directory.CreateDirectory(Path.Combine(root, "sample"));

        var loader = PackLoader.ForDirectory(root);

        PackValidationException ex =
            Assert.Throws<PackValidationException>(() => loader.Load("sample"));

        ex.Issues.Should().Contain(i => i.Section == "pack.yaml" && i.Code == "pack.section-missing");
        ex.Issues.Should().Contain(i => i.Section == "agents.yaml" && i.Code == "pack.section-missing");
        ex.Message.Should().Contain("sample");
        ex.Message.Should().Contain("pack.yaml");
        ex.Message.Should().Contain("agents.yaml");
    }

    [Fact]
    public void Load_MetadataKeyMismatch_IsAggregatedWithOtherIssues()
    {
        string root = NewFixture("key-mismatch");
        string packDir = Path.Combine(root, "wobble");
        Directory.CreateDirectory(packDir);

        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), """
            metadata:
              key: some-other-key
              displayName: ""
            tenant:
              company: ""
              industry: ""
              brands: []
              regions: []
              channels: []
              theme:
                primaryColor: ""
              distribution:
                model: ""
            """);
        // agents.yaml intentionally missing — aggregate reporting means
        // we should still see the metadata + tenant issues alongside it.

        var loader = PackLoader.ForDirectory(root);

        PackValidationException ex =
            Assert.Throws<PackValidationException>(() => loader.Load("wobble"));

        ex.Issues.Should().Contain(i => i.Code == "pack.metadata.key-mismatch");
        ex.Issues.Should().Contain(i => i.Code == "pack.metadata.display-name-missing");
        ex.Issues.Should().Contain(i => i.Code == "pack.tenant.company-missing");
        ex.Issues.Should().Contain(i => i.Code == "pack.tenant.industry-missing");
        ex.Issues.Should().Contain(i => i.Code == "pack.tenant.brands-empty");
        ex.Issues.Should().Contain(i => i.Code == "pack.tenant.regions-empty");
        ex.Issues.Should().Contain(i => i.Code == "pack.tenant.channels-empty");
        ex.Issues.Should().Contain(i => i.Code == "pack.tenant.distribution-missing");
        ex.Issues.Should().Contain(i => i.Code == "pack.tenant.theme-missing");
        ex.Issues.Should().Contain(i => i.Section == "agents.yaml" && i.Code == "pack.section-missing");

        // Every issue names the pack + section — the diagnostic contract
        // the coordinator asked us to hold.
        ex.Issues.Should().OnlyContain(i => i.PackName == "wobble");
        ex.Issues.Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.Section));
    }

    [Fact]
    public void Load_DuplicateAgentName_IsFlagged()
    {
        string root = NewFixture("dup-agent-name");
        string packDir = Path.Combine(root, "duplo");
        Directory.CreateDirectory(packDir);

        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("duplo"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), """
            agents:
              alpha:
                name: "Same Name"
                model: "gpt-5.4-mini"
                system_prompt: "You are alpha."
                temperature: 0.3
                tools: []
              beta:
                name: "Same Name"
                model: "gpt-5.4-mini"
                system_prompt: "You are beta."
                temperature: 0.3
                tools: []
            """);

        var loader = PackLoader.ForDirectory(root);

        PackValidationException ex =
            Assert.Throws<PackValidationException>(() => loader.Load("duplo"));

        ex.Issues.Should().ContainSingle(i => i.Code == "pack.agents.duplicate-name");
    }

    [Fact]
    public void Load_EmptyKnowledgeFileAndDuplicateStartingTaskId_ReportedTogether()
    {
        string root = NewFixture("optional-issues");
        string packDir = Path.Combine(root, "quibble");
        Directory.CreateDirectory(packDir);
        Directory.CreateDirectory(Path.Combine(packDir, "knowledge"));

        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("quibble"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), MinimalValidAgentsYaml());
        File.WriteAllText(Path.Combine(packDir, "knowledge", "blank.md"), "   \n\n");
        File.WriteAllText(Path.Combine(packDir, "starting-tasks.yaml"), """
            categories:
              - id: focus
                label: "Focus"
                emoji: "🎯"
                prompts:
                  - "Prompt one"
              - id: focus
                label: "Focus Two"
                emoji: "🎯"
                prompts:
                  - "Prompt two"
            """);

        var loader = PackLoader.ForDirectory(root);

        PackValidationException ex =
            Assert.Throws<PackValidationException>(() => loader.Load("quibble"));

        ex.Issues.Should().Contain(i => i.Code == "pack.knowledge.empty");
        ex.Issues.Should().Contain(i => i.Code == "pack.starting-tasks.duplicate-id");
    }

    [Fact]
    public void Load_ValidMinimalPack_ProducesLoadedPack()
    {
        string root = NewFixture("minimal-valid");
        string packDir = Path.Combine(root, "tinypack");
        Directory.CreateDirectory(packDir);

        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("tinypack"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), MinimalValidAgentsYaml());

        var loader = PackLoader.ForDirectory(root);
        LoadedPack pack = loader.Load("tinypack");

        pack.Name.Should().Be("tinypack");
        pack.Metadata.Key.Should().Be("tinypack");
        pack.Tenant.Company.Should().Be("Tinypack Co.");
        pack.Agents.Agents.Should().ContainKey("solo");
        pack.StartingTasks.Should().BeEmpty();
        pack.KnowledgeDocuments.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverPacks_ReturnsSortedDirectoryNames()
    {
        string root = NewFixture("discover");
        Directory.CreateDirectory(Path.Combine(root, "zeta"));
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        Directory.CreateDirectory(Path.Combine(root, "mu"));

        var loader = PackLoader.ForDirectory(root);

        loader.DiscoverPacks().Should().Equal(["alpha", "mu", "zeta"]);
    }

    [Fact]
    public void ForDirectory_MissingPacksRoot_Throws()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "no-such-root-" + Guid.NewGuid().ToString("N"));

        Action act = () => PackLoader.ForDirectory(root);

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Load_DuplicateAgentSectionKeyInYaml_IsFlaggedAsParseError()
    {
        // Regression guard for the acceptance hole where YamlDotNet's
        // default deserializer silently last-one-wins on duplicate keys.
        // Two 'demand-forecast:' entries would otherwise ship a
        // half-defined roster; the loader now enforces
        // WithDuplicateKeyChecking so the parser rejects the file and
        // the aggregate report surfaces a clear parse error.
        string root = NewFixture("dup-yaml-key");
        string packDir = Path.Combine(root, "twinsy");
        Directory.CreateDirectory(packDir);

        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("twinsy"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), """
            agents:
              demand-forecast:
                name: "Alpha"
                model: "gpt-5.4-mini"
                system_prompt: "You are alpha."
                temperature: 0.3
                tools: []
              demand-forecast:
                name: "Beta"
                model: "gpt-5.4-mini"
                system_prompt: "You are beta."
                temperature: 0.3
                tools: []
            """);

        var loader = PackLoader.ForDirectory(root);

        PackValidationException ex =
            Assert.Throws<PackValidationException>(() => loader.Load("twinsy"));

        PackValidationIssue parseError = ex.Issues.Should().ContainSingle(i =>
            i.Section == "agents.yaml" && i.Code == "pack.parse-error").Subject;
        parseError.Message.Should().Contain("duplicate", "the parser must name the failure mode");
        parseError.PackName.Should().Be("twinsy");
    }

    [Fact]
    public void Load_MalformedAgentsYaml_IsFlaggedAsParseError()
    {
        string root = NewFixture("malformed-agents");
        string packDir = Path.Combine(root, "brokenpack");
        Directory.CreateDirectory(packDir);

        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("brokenpack"));
        // Broken YAML: unclosed mapping.
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), """
            agents:
              solo:
                name: "Broken
                model: gpt-5.4-mini
                tools: [
            """);

        var loader = PackLoader.ForDirectory(root);

        PackValidationException ex =
            Assert.Throws<PackValidationException>(() => loader.Load("brokenpack"));

        ex.Issues.Should().Contain(i =>
            i.Section == "agents.yaml" && i.Code == "pack.parse-error");
        ex.Issues.Should().OnlyContain(i => i.PackName == "brokenpack");
        ex.Issues.Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.Section));
    }

    [Fact]
    public void Load_UnknownStartingTaskFields_DoNotBlockLoad_ButDuplicateKeysDo()
    {
        // starting-tasks.yaml uses IgnoreUnmatchedProperties so a
        // forward-compatible extra field (e.g., an author annotation)
        // doesn't break the load — but duplicate keys within a single
        // mapping still surface as parse errors so accidental prompt
        // loss cannot ship silently.
        string root = NewFixture("starting-tasks-dup");
        string packDir = Path.Combine(root, "dupcats");
        Directory.CreateDirectory(packDir);

        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("dupcats"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), MinimalValidAgentsYaml());
        File.WriteAllText(Path.Combine(packDir, "starting-tasks.yaml"), """
            categories:
              - id: focus
                label: "Focus"
                emoji: "🎯"
                prompts:
                  - "One"
                prompts:
                  - "Two"
            """);

        var loader = PackLoader.ForDirectory(root);

        PackValidationException ex =
            Assert.Throws<PackValidationException>(() => loader.Load("dupcats"));

        ex.Issues.Should().Contain(i =>
            i.Section == "starting-tasks.yaml" && i.Code == "pack.parse-error");
    }

    [Fact]
    public void Load_EmptyKnowledgeDirectory_IsTreatedAsAbsent_NotAsFailure()
    {
        // Missing optional sections degrade sensibly: a knowledge dir
        // that exists but contains only a README (no *.md corpus files)
        // must not fail load. Guards the "empty seed/ + README" pattern
        // used by the shipped fictional packs.
        string root = NewFixture("no-corpus");
        string packDir = Path.Combine(root, "emptycorpus");
        Directory.CreateDirectory(packDir);
        Directory.CreateDirectory(Path.Combine(packDir, "knowledge"));
        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("emptycorpus"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), MinimalValidAgentsYaml());

        var loader = PackLoader.ForDirectory(root);
        LoadedPack pack = loader.Load("emptycorpus");

        pack.KnowledgeDocuments.Should().BeEmpty();
        pack.StartingTasks.Should().BeEmpty();
    }

    private static string MinimalValidPackYaml(string key) => $$"""
        metadata:
          key: {{key}}
          displayName: "Tinypack"
          version: "0.1.0"
        tenant:
          company: "Tinypack Co."
          industry: "Retail"
          brands:
            - name: "Widget"
              category: "General"
              variants: ["Standard"]
              priceSegment: "Standard"
          regions:
            - "Region One"
          channels:
            - "Direct"
          theme:
            primaryColor: "#112233"
            accentColor: "#445566"
          distribution:
            model: "Direct"
            distributorTypes:
              - "Retailer"
        """;

    private static string MinimalValidAgentsYaml() => """
        agents:
          solo:
            name: "Solo Agent"
            model: "gpt-5.4-mini"
            system_prompt: "You are solo."
            temperature: 0.3
            tools: []
        """;
}
