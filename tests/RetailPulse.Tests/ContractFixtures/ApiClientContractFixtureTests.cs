using System.Text.Json;
using System.Text.Json.Nodes;
using RetailPulse.Api.Scorecard;
using RetailPulse.Contracts.Cards;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.ContractFixtures;

/// <summary>
/// Client/API contract fixtures.
///
/// Both sides of every response used to be tested in isolation: the C# tests
/// asserted what the API produced and the TypeScript tests asserted what the SPA
/// consumed, but nothing asserted the two agreed. Five field mismatches reached
/// production through that gap. This suite closes it.
///
/// Each fact serialises a real response DTO with the same options ASP.NET Core
/// minimal APIs use for <c>Results.Ok(...)</c> (<see cref="JsonSerializerDefaults.Web"/>:
/// camelCase names, enums as integers because no <c>JsonStringEnumConverter</c> is
/// registered in Program.cs, ISO-8601 dates) and asserts the JSON matches a
/// committed fixture under <c>contracts/fixtures</c>. The identical fixtures are
/// consumed by the TypeScript suite
/// (<c>src/RetailPulse.Web/src/__tests__/apiClientContract.contract.test.ts</c>),
/// so a field renamed, removed, or retyped on either side breaks a test that names
/// the endpoint and the field.
///
/// Why a snapshot and not a live call: CI has no server and no Azure. Serialising
/// the DTO the endpoint returns reproduces the exact wire shape without a host, and
/// a committed snapshot turns drift into a reviewable diff instead of a silent
/// change. Regeneration is deliberate, never automatic: set
/// <c>UPDATE_CONTRACT_FIXTURES=1</c> to rewrite the committed fixtures.
/// </summary>
public sealed class ApiClientContractFixtureTests
{
    private static readonly JsonSerializerOptions WireOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // A fixed instant keeps serialised fixtures deterministic across runs; the
    // contract under test is the shape, not the value.
    private static readonly DateTime Instant =
        new(2026, 8, 30, 17, 5, 24, DateTimeKind.Utc);

    [Fact]
    public void GuardrailsStats_matches_committed_fixture()
    {
        // GuardrailEndpoints /api/guardrails/stats projects GuardrailsStats 1:1 to
        // camelCase. Serialising the DTO tracks additive counter fields
        // automatically, which is the intent: a new fail-open counter surfaces as a
        // reviewable fixture diff rather than silent drift.
        var stats = new GuardrailsStats(
            TotalBlocked: 128,
            JailbreakAttempts: 37,
            PiiDetections: 52,
            AccessDenials: 14,
            Since: Instant,
            ContentSafetyBlocks: 19,
            ContentSafetyFlags: 8,
            FailOpenPasses: 5);

        AssertMatchesFixture("guardrails-stats", stats);
    }

    [Fact]
    public void GuardrailsLog_matches_committed_fixture()
    {
        // GuardrailEndpoints /api/guardrails/log projects SuspiciousRequest 1:1 to
        // camelCase. Every optional detail field is populated so the fixture proves
        // the whole audit-row surface the dashboard reads, not just the required
        // prefix.
        SuspiciousRequest[] log =
        [
            new SuspiciousRequest(
                Id: "req-1024",
                Timestamp: Instant,
                RequestText: "Tool result from 'GetStorePerformance' blocked by Content Safety",
                DetectionType: "content-safety-sexual",
                UserContext: "analyst@contoso.com",
                Action: "blocked",
                Category: "Sexual",
                Severity: 4,
                Decision: "Blocked",
                Stage: "ToolResult",
                Threshold: 4,
                Reason: "Content Safety classified the tool result as Sexual content at severity 4, which met threshold 4.",
                Subject: "GetStorePerformance"),
        ];

        AssertMatchesFixture("guardrails-log", log);
    }

    [Fact]
    public void Cards_matches_committed_fixture()
    {
        // CardEndpoints GET /api/cards returns AdaptiveCard directly. Enums cross the
        // wire as integers (Type/Lifecycle) and votes carry vote/timestamp: exactly
        // the shape cardsApi.ts normalises. Two of the five production mismatches
        // lived here (lifecycle vs state, vote vs choice).
        AdaptiveCard[] cards =
        [
            new AdaptiveCard(
                Id: "card-council-42",
                Title: "Contoso council verdict",
                Type: CardType.Voting,
                Lifecycle: CardLifecycle.Voting,
                CreatedBy: "system",
                CreatedAt: Instant,
                Votes: [new CardVote("user-7", "Ada Lovelace", "Red", Instant)],
                Comments: [new CardComment("user-9", "Alan Turing", "Agree with the downgrade.", Instant)],
                Data: new Dictionary<string, object>
                {
                    ["brand"] = "Contoso",
                    ["overall_rating"] = "Red",
                    ["synthesis"] = "Demand is softening faster than supply can adjust.",
                },
                EscalationReason: "Two specialists rated Red."),
        ];

        AssertMatchesFixture("cards", cards);
    }

    [Fact]
    public void PortfolioScorecard_matches_committed_fixture()
    {
        // ScorecardEndpoints POST /api/scorecard returns
        // ScorecardOrchestrator.PortfolioScorecard directly. The dimension agentKey
        // values are the join the SPA uses (dimensionKeyFromAgent) to bind each
        // dimension to a card, so all five are exercised.
        var dimensions = new Dictionary<string, ScorecardOrchestrator.DimensionScore>
        {
            ["Demand Momentum"] = new("Demand Momentum", 7.5, 0.25, 1.875, "Strong sell-through.", "demand-forecasting"),
            ["Competitive Position"] = new("Competitive Position", 6.0, 0.20, 1.2, "Holding share.", "competitive-intel"),
            ["Supply Reliability"] = new("Supply Reliability", 5.5, 0.20, 1.1, "Some stockout risk.", "supply-chain"),
            ["Store Execution"] = new("Store Execution", 6.5, 0.20, 1.3, "Good placement.", "store-ops"),
            ["Margin Health"] = new("Margin Health", 4.5, 0.15, 0.675, "Cost pressure.", "margin-analysis"),
        };

        var scorecard = new ScorecardOrchestrator.PortfolioScorecard(
            Brands:
            [
                new ScorecardOrchestrator.BrandScore(
                    Brand: "Contoso",
                    OverallScore: 6.1,
                    Dimensions: dimensions,
                    Summary: "Contoso is mixed: strong demand, soft margin.",
                    ActionItems: ["Protect margin on hero SKUs.", "Lean into demand momentum."],
                    DurationMs: 8421),
            ],
            // The SPA passes includeSummary=false and renders per-brand cards, so the
            // executive summary is empty on the contract path.
            ExecutiveSummary: "",
            TopActions: ["Protect margin on hero SKUs.", "Lean into demand momentum."],
            GeneratedAt: Instant,
            TotalDurationMs: 8421);

        AssertMatchesFixture("portfolio-scorecard", scorecard);
    }

    private static void AssertMatchesFixture<T>(string name, T value)
    {
        string actualJson = JsonSerializer.Serialize(value, WireOptions);
        string fixturePath = Path.Combine(FixturesDirectory(), name + ".json");

        if (ShouldUpdate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            // LF endings so the committed fixture is stable across platforms and the
            // gitattributes eol=lf normalisation is a no-op.
            File.WriteAllText(fixturePath, actualJson.ReplaceLineEndings("\n") + "\n");
            return;
        }

        Assert.True(
            File.Exists(fixturePath),
            $"Contract fixture '{name}' is missing at {fixturePath}. Regenerate committed " +
            "fixtures with: UPDATE_CONTRACT_FIXTURES=1 dotnet test " +
            "--filter FullyQualifiedName~ApiClientContractFixtureTests");

        var expected = JsonNode.Parse(File.ReadAllText(fixturePath));
        var actual = JsonNode.Parse(actualJson);

        var diffs = new List<string>();
        DiffJson(name, expected, actual, diffs);

        Assert.True(
            diffs.Count == 0,
            $"API response shape for '{name}' drifted from the committed contract fixture " +
            $"({fixturePath}). Each line below names the JSON path that changed. If the change " +
            "is intentional, regenerate with UPDATE_CONTRACT_FIXTURES=1, review the diff, and " +
            "update the matching TypeScript client types/mappers so both sides agree:\n  " +
            string.Join("\n  ", diffs));
    }

    // Structural, order-insensitive JSON compare that reports one line per drifted
    // path so a failure names the endpoint and the exact field.
    private static void DiffJson(string path, JsonNode? expected, JsonNode? actual, List<string> diffs)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
                diffs.Add($"{path}: {(expected is null ? "value present in API output but null in contract" : "value missing from API output")}");
            return;
        }

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (KeyValuePair<string, JsonNode?> field in expectedObject)
            {
                if (!actualObject.ContainsKey(field.Key))
                    diffs.Add($"{path}.{field.Key}: field in committed contract but missing from API output (removed or renamed)");
                else
                    DiffJson($"{path}.{field.Key}", field.Value, actualObject[field.Key], diffs);
            }

            foreach (KeyValuePair<string, JsonNode?> field in actualObject)
            {
                if (!expectedObject.ContainsKey(field.Key))
                    diffs.Add($"{path}.{field.Key}: field in API output but not in committed contract (added or renamed)");
            }

            return;
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
                diffs.Add($"{path}: array length changed ({expectedArray.Count} -> {actualArray.Count})");

            for (int i = 0; i < Math.Min(expectedArray.Count, actualArray.Count); i++)
                DiffJson($"{path}[{i}]", expectedArray[i], actualArray[i], diffs);

            return;
        }

        JsonValueKind expectedKind = expected.GetValueKind();
        JsonValueKind actualKind = actual.GetValueKind();
        if (expectedKind != actualKind)
        {
            diffs.Add($"{path}: type changed ({expectedKind} -> {actualKind})");
            return;
        }

        if (!string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal))
            diffs.Add($"{path}: value changed ({expected.ToJsonString()} -> {actual.ToJsonString()})");
    }

    private static bool ShouldUpdate =>
        string.Equals(Environment.GetEnvironmentVariable("UPDATE_CONTRACT_FIXTURES"), "1", StringComparison.Ordinal)
        || string.Equals(Environment.GetEnvironmentVariable("UPDATE_CONTRACT_FIXTURES"), "true", StringComparison.OrdinalIgnoreCase);

    private static string FixturesDirectory() => Path.Combine(RepositoryRoot(), "contracts", "fixtures");

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (no RetailPulse.slnx found above {AppContext.BaseDirectory}).");
    }
}
