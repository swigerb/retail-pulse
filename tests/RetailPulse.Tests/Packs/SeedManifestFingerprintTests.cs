using FluentAssertions;
using RetailPulse.Api.Packs;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Blocker #4: the pack fingerprint MUST include actual seed content
/// plus pack identity/config so a scenario-data edit forces a reseed on
/// its own. These tests mutate only files under <c>seed/</c> and prove
/// the fingerprint changes; they also prove that touching nothing keeps
/// it stable.
/// </summary>
public sealed class SeedManifestFingerprintTests : IDisposable
{
    private readonly List<string> _fixtures = [];

    private string NewFixture(string name)
    {
        string dir = PackTestPaths.CreateFixtureDirectory("fp-" + name);
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
    public void Fingerprint_Changes_WhenScenarioYamlContentChanges()
    {
        string root = NewFixture("seed-only");
        string packDir = Path.Combine(root, "seed-only");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("seed-only"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), MinimalValidAgentsYaml());
        string seedDir = Path.Combine(packDir, "seed");
        Directory.CreateDirectory(seedDir);
        string seedPath = Path.Combine(seedDir, "scenario.yaml");
        File.WriteAllText(seedPath, PackLoaderTests.MinimalValidSeedYaml());

        var loader = PackLoader.ForDirectory(root);
        string before = PackContentFingerprint.ComputePackFingerprint(loader.Load("seed-only"));

        // Mutate ONLY seed content — pack.yaml, agents.yaml, and every
        // other file are untouched. This proves the fingerprint is
        // truly seed-content sensitive, not just pack-identity sensitive.
        string swapped = PackLoaderTests.MinimalValidSeedYaml()
            .Replace("Flagship", "Concept-Store");
        File.WriteAllText(seedPath, swapped);

        string after = PackContentFingerprint.ComputePackFingerprint(loader.Load("seed-only"));

        after.Should().NotBe(before,
            "editing seed content alone must change the fingerprint so operators see a reseed");
    }

    [Fact]
    public void Fingerprint_Changes_WhenAdditionalSeedFileIsAdded()
    {
        // A pack that adds a second seed file (e.g., promo-calendar.yaml)
        // without touching scenario.yaml must still get a fresh
        // fingerprint — every file under seed/ contributes.
        string root = NewFixture("added-file");
        string packDir = Path.Combine(root, "added-file");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("added-file"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), MinimalValidAgentsYaml());
        string seedDir = Path.Combine(packDir, "seed");
        Directory.CreateDirectory(seedDir);
        File.WriteAllText(Path.Combine(seedDir, "scenario.yaml"), PackLoaderTests.MinimalValidSeedYaml());

        var loader = PackLoader.ForDirectory(root);
        string before = PackContentFingerprint.ComputePackFingerprint(loader.Load("added-file"));

        File.WriteAllText(Path.Combine(seedDir, "notes.md"), "additional seed notes");

        string after = PackContentFingerprint.ComputePackFingerprint(loader.Load("added-file"));

        after.Should().NotBe(before,
            "adding a new seed file must change the fingerprint");
    }

    [Fact]
    public void Fingerprint_IsStable_WhenNoContentChanges()
    {
        // The contrapositive: touching nothing must keep the fingerprint
        // stable so unchanged content never triggers a spurious reseed.
        string root = NewFixture("stable");
        string packDir = Path.Combine(root, "stable");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), MinimalValidPackYaml("stable"));
        File.WriteAllText(Path.Combine(packDir, "agents.yaml"), MinimalValidAgentsYaml());
        string seedDir = Path.Combine(packDir, "seed");
        Directory.CreateDirectory(seedDir);
        File.WriteAllText(Path.Combine(seedDir, "scenario.yaml"), PackLoaderTests.MinimalValidSeedYaml());

        var loader = PackLoader.ForDirectory(root);
        string first = PackContentFingerprint.ComputePackFingerprint(loader.Load("stable"));
        string second = PackContentFingerprint.ComputePackFingerprint(loader.Load("stable"));

        second.Should().Be(first, "unchanged content must not trigger a reseed");
    }

    [Fact]
    public void Fingerprint_VersionStamp_IsV2_ToForceUpgradeReseed()
    {
        // Blocker #4 requires bumping the schema/fingerprint version so
        // hosts with a stored v1 fingerprint re-seed on first boot after
        // an upgrade — the seed manifest wasn't part of v1.
        PackContentFingerprint.Version.Should().Be("v2");

        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        LoadedPack def = loader.Load("default");
        string fp = PackContentFingerprint.ComputePackFingerprint(def);
        fp.Should().StartWith("v2:");
    }

    private static string MinimalValidPackYaml(string key) => $$"""
        metadata:
          key: {{key}}
          displayName: "FP Pack"
          version: "0.1.0"
        tenant:
          company: "FP Co."
          industry: "Retail"
          brands:
            - name: "Widget"
              category: "General"
              variants: ["Standard"]
              priceSegment: "Standard"
          regions: ["Region One"]
          channels: ["Direct"]
          theme:
            primaryColor: "#112233"
            accentColor: "#445566"
          distribution:
            model: "Direct"
            distributorTypes: ["Retailer"]
        """;

    private static string MinimalValidAgentsYaml() => """
        agents:
          solo:
            name: "Solo"
            model: "gpt-5.4-mini"
            system_prompt: "You are solo."
            temperature: 0.3
            tools: []
        """;
}
