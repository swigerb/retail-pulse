using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.OpenAI.Gateway;
using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Tests.OpenAI;

public sealed class GatewayEvidenceTests
{
    private static GatewayCallEvidence Parse(Dictionary<string, string> headers) =>
        GatewayEvidenceHeaders.Parse(n => headers.GetValueOrDefault(n), 200);

    [Fact]
    public void Parse_FullHeaderSet_MapsEveryField()
    {
        var e = Parse(new()
        {
            [GatewayEvidenceHeaders.RequestId] = "abc-123",
            [GatewayEvidenceHeaders.Backend] = "retail-pulse-foundry",
            [GatewayEvidenceHeaders.Route] = "failover",
            [GatewayEvidenceHeaders.TokensConsumed] = "120",
            [GatewayEvidenceHeaders.TokensRemaining] = "79880",
            [GatewayEvidenceHeaders.Quota] = "allowed",
            [GatewayEvidenceHeaders.Cache] = "not-applicable",
            [GatewayEvidenceHeaders.Safety] = "passed",
            [GatewayEvidenceHeaders.PolicyVersion] = "2026-10-rp327-v1",
            [GatewayEvidenceHeaders.LatencyMs] = "850",
            [GatewayEvidenceHeaders.BackendLatencyMs] = "800",
        });

        e.Should().BeEquivalentTo(new GatewayCallEvidence(
            "abc-123", "retail-pulse-foundry", GatewayRouteState.Failover, 120, 79880,
            GatewayOutcome.Allowed, GatewayOutcome.NotApplicable, GatewayOutcome.Passed,
            "2026-10-rp327-v1", 850, 800, 200));
    }

    [Fact]
    public void Parse_NoHeaders_YieldsExplicitUnknownNotSuccess()
    {
        var e = Parse([]);

        e.IsEmpty.Should().BeTrue();
        e.Quota.Should().Be(GatewayOutcome.Unknown);
        e.Safety.Should().Be(GatewayOutcome.Unknown);
        e.Route.Should().Be(GatewayRouteState.Unknown);
        e.TokensConsumed.Should().BeNull();
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("12abc")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("99999999999999999999")]
    public void Parse_MalformedCounts_BecomeNull(string value)
    {
        var e = Parse(new() { [GatewayEvidenceHeaders.TokensConsumed] = value, [GatewayEvidenceHeaders.LatencyMs] = value });

        e.TokensConsumed.Should().BeNull();
        e.GatewayLatencyMs.Should().BeNull();
    }

    [Theory]
    [InlineData("sk-secret value with spaces")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("line\r\nbreak")]
    public void Parse_UnsafeIdentifiers_AreRedactedToNull(string value)
    {
        var e = Parse(new()
        {
            [GatewayEvidenceHeaders.RequestId] = value,
            [GatewayEvidenceHeaders.Backend] = value,
            [GatewayEvidenceHeaders.PolicyVersion] = value,
        });

        e.GatewayRequestId.Should().BeNull();
        e.BackendAlias.Should().BeNull();
        e.PolicyVersion.Should().BeNull();
    }

    [Fact]
    public void Parse_OverlongIdentifier_IsDropped()
    {
        Parse(new() { [GatewayEvidenceHeaders.RequestId] = new string('a', 65) })
            .GatewayRequestId.Should().BeNull();
    }

    [Theory]
    [InlineData(GatewayEvidenceHeaders.Quota, "hit")]
    [InlineData(GatewayEvidenceHeaders.Cache, "allowed")]
    [InlineData(GatewayEvidenceHeaders.Safety, "throttled")]
    [InlineData(GatewayEvidenceHeaders.Quota, "OK")]
    public void Parse_OutcomeOutsideFieldVocabulary_IsUnknown(string header, string value)
    {
        var e = Parse(new() { [header] = value });

        e.Quota.Should().Be(GatewayOutcome.Unknown);
        e.Cache.Should().Be(GatewayOutcome.Unknown);
        e.Safety.Should().Be(GatewayOutcome.Unknown);
    }

    [Fact]
    public void Parse_IgnoresNonAllowlistedHeaders_SoSecretsCannotLeak()
    {
        var requested = new List<string>();
        GatewayEvidenceHeaders.Parse(n => { requested.Add(n); return "x"; });

        requested.Should().OnlyContain(n => GatewayEvidenceHeaders.AllNames.Contains(n));
        requested.Should().NotContain(n => n.Contains("subscription", StringComparison.OrdinalIgnoreCase)
            || n.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Collector_WithoutScope_RecordIsNoOp()
    {
        var act = () => GatewayEvidenceCollector.Record(Parse([]));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Collector_AggregatesAcrossAsyncCalls_AndSeparatesUnreported()
    {
        var scope = GatewayEvidenceCollector.Begin();
        await Task.WhenAll(
            Task.Run(() => GatewayEvidenceCollector.Record(Parse(new()
            { [GatewayEvidenceHeaders.TokensConsumed] = "10", [GatewayEvidenceHeaders.TokensRemaining] = "90" }))),
            Task.Run(() => GatewayEvidenceCollector.Record(Parse(new() { [GatewayEvidenceHeaders.TokensConsumed] = "5" }))));
        GatewayEvidenceCollector.Record(Parse([]));

        var snap = scope.Snapshot();

        snap.Observed.Should().BeTrue();
        snap.CallCount.Should().Be(3);
        snap.TotalTokensConsumed.Should().Be(15);
        snap.TokensRemaining.Should().Be(90);
    }

    [Fact]
    public void Snapshot_NoCalls_IsNotObserved()
    {
        GatewayEvidenceCollector.Begin().Snapshot().Should().BeSameAs(GatewayEvidence.None);
        GatewayEvidence.None.Observed.Should().BeFalse();
    }

    [Fact]
    public void Snapshot_OnlyEmptyCalls_IsNotObserved()
    {
        var scope = GatewayEvidenceCollector.Begin();
        GatewayEvidenceCollector.Record(Parse([]));

        var snap = scope.Snapshot();

        snap.Observed.Should().BeFalse();
        snap.TotalTokensConsumed.Should().BeNull();
    }

    [Fact]
    public void Serialization_UsesCamelCaseAndKebabOutcomes_AndHasNoSensitiveFields()
    {
        var json = JsonSerializer.Serialize(
            new GatewayEvidence(true, 1, 1, 2, [Parse(new() { [GatewayEvidenceHeaders.Cache] = "not-applicable" })]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"cache\":\"not-applicable\"").And.Contain("\"quota\":\"unknown\"");
        json.Should().NotContainEquivalentOf("subscription").And.NotContainEquivalentOf("prompt");
    }

    [Fact]
    public void ChatResponse_GatewayDefaultsToNull_PreservingExistingShape()
    {
        new RetailPulse.Contracts.ChatResponse("r", "s", []).Gateway.Should().BeNull();
    }

    [Fact]
    public void ApimPolicy_EmitsEvidenceHeadersAndStripsInboundSpoofing()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "infra", "modules", "apim-openai-policy.xml")))
            dir = Path.GetDirectoryName(dir)!;
        string xml = File.ReadAllText(Path.Combine(dir, "infra", "modules", "apim-openai-policy.xml"));

        foreach (string name in GatewayEvidenceHeaders.AllNames.Where(n => n is not (GatewayEvidenceHeaders.BackendLatencyMs or GatewayEvidenceHeaders.TokensRemaining)))
        {
            xml.Should().Contain($"<set-header name=\"{name}\" exists-action=\"delete\" />", name);
            xml.Should().Contain($"<set-header name=\"{name}\" exists-action=\"override\">", name);
        }

        xml.Should().NotContain("context.Subscription.Key").And.NotContain("Authorization");
    }
}
