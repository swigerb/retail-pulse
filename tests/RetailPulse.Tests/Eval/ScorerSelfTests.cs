using System.Text.Json;
using FluentAssertions;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Proves the deterministic scorer actually detects wrong answers. A scorer that cannot
/// catch a deliberately incorrect expectation is worse than useless — it silently green-
/// lights regressions. This suite feeds both known-good and known-bad expectations and
/// asserts:
/// <list type="bullet">
/// <item>Every golden case in the shipped dataset passes (known-good baseline).</item>
/// <item>Every case in the known-bad fixture fails at least one graded property.</item>
/// <item>The specific property each known-bad case targets is the one that fails.</item>
/// </list>
/// If either half of this suite goes green when it shouldn't, the scorer or the fixtures
/// have drifted and the harness must not be relied on as a CI gate.
/// </summary>
public sealed class ScorerSelfTests
{
    [Fact]
    public void KnownGood_GoldenDataset_AllCasesPass()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();
        var evaluator = new DeterministicEvaluator();

        foreach (GoldenCase c in dataset.Cases)
        {
            CaseResult result = evaluator.Evaluate(c);
            result.AllPropertiesPassed.Should().BeTrue(
                $"case {c.Id}: golden expectations must match live scorer output "
                + $"(explicit_chart pass={result.ExplicitChart.Pass}, "
                + $"chart_type pass={result.ChartType.Pass}, "
                + $"routing_intent pass={result.RoutingIntent.Pass}, "
                + $"llm_call pass={result.LlmCallMade.Pass}, "
                + $"memory_command pass={result.MemoryCommand.Pass})");
        }
    }

    [Fact]
    public void KnownBad_Fixture_EveryCaseFailsAtLeastOneProperty()
    {
        string json = File.ReadAllText(GoldenDatasetLoader.KnownBadPath());
        KnownBadFixture fixture = JsonSerializer.Deserialize<KnownBadFixture>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("known-bad-cases.json deserialized to null");

        var evaluator = new DeterministicEvaluator();
        fixture.Cases.Should().NotBeEmpty("known-bad fixture must actually contain cases");

        foreach (GoldenCase c in fixture.Cases)
        {
            CaseResult result = evaluator.Evaluate(c);
            result.AllPropertiesPassed.Should().BeFalse(
                $"known-bad case {c.Id} was expected to fail scoring, but the scorer reported success. "
                + "This means the scorer is not detecting an intentional regression — the CI gate would "
                + "silently accept broken behavior. Investigate the scorer before trusting the harness.");
        }
    }

    [Fact]
    public void KnownBad_TargetProperty_IsTheOneThatFails()
    {
        string json = File.ReadAllText(GoldenDatasetLoader.KnownBadPath());
        KnownBadFixture fixture = JsonSerializer.Deserialize<KnownBadFixture>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var evaluator = new DeterministicEvaluator();

        foreach (GoldenCase c in fixture.Cases)
        {
            CaseResult result = evaluator.Evaluate(c);

            switch (c.Id)
            {
                case "known-bad-01-wrong-chart-type":
                    result.ChartType.Pass.Should().BeFalse(
                        "chart_type mismatch must fail the chart_type property specifically");
                    break;
                case "known-bad-02-wrong-explicit-chart":
                    result.ExplicitChart.Pass.Should().BeFalse(
                        "explicit_chart mismatch must fail the explicit_chart property specifically");
                    break;
                case "known-bad-03-wrong-routing-intent":
                    result.RoutingIntent.Pass.Should().BeFalse(
                        "routing_intent mismatch must fail the routing_intent property specifically");
                    break;
                case "known-bad-04-wrong-memory-command":
                    result.MemoryCommand.Pass.Should().BeFalse(
                        "memory_command mismatch must fail the memory_command property specifically");
                    break;
                case "known-bad-05-wrong-fastpath-declared-llm":
                    result.LlmCallMade.Pass.Should().BeFalse(
                        "declared llm-required on a keyword-fast-path prompt must fail the llm_call_made property");
                    break;
                default:
                    // A new fixture case that this test doesn't yet know about — the safer
                    // assertion is "some property failed", which is already covered.
                    result.AllPropertiesPassed.Should().BeFalse();
                    break;
            }
        }
    }

    private sealed record KnownBadFixture(int Version, string Notes, IReadOnlyList<GoldenCase> Cases);
}
