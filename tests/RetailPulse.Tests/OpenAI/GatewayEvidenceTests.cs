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
        GatewayCallEvidence e = Parse(new()
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
        GatewayCallEvidence e = Parse([]);

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
        GatewayCallEvidence e = Parse(new() { [GatewayEvidenceHeaders.TokensConsumed] = value, [GatewayEvidenceHeaders.LatencyMs] = value });

        e.TokensConsumed.Should().BeNull();
        e.GatewayLatencyMs.Should().BeNull();
    }

    [Theory]
    [InlineData("sk-secret value with spaces")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("line\r\nbreak")]
    public void Parse_UnsafeIdentifiers_AreRedactedToNull(string value)
    {
        GatewayCallEvidence e = Parse(new()
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
        GatewayCallEvidence e = Parse(new() { [header] = value });

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
        Action act = () => GatewayEvidenceCollector.Record(Parse([]));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Collector_AggregatesAcrossAsyncCalls_AndSeparatesUnreported()
    {
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        await Task.WhenAll(
            Task.Run(() => GatewayEvidenceCollector.Record(Parse(new()
            { [GatewayEvidenceHeaders.TokensConsumed] = "10", [GatewayEvidenceHeaders.TokensRemaining] = "90" }))),
            Task.Run(() => GatewayEvidenceCollector.Record(Parse(new() { [GatewayEvidenceHeaders.TokensConsumed] = "5" }))));
        GatewayEvidenceCollector.Record(Parse([]));

        GatewayEvidence snap = scope.Snapshot();

        snap.Status.Should().Be(GatewayEvidenceStatus.Unavailable);
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
        GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
        GatewayEvidenceCollector.Record(Parse([]));

        GatewayEvidence snap = scope.Snapshot();

        snap.Observed.Should().BeFalse();
        snap.TotalTokensConsumed.Should().BeNull();
    }

    [Fact]
    public void Serialization_UsesCamelCaseAndKebabOutcomes_AndHasNoSensitiveFields()
    {
        string json = JsonSerializer.Serialize(
            new GatewayEvidence(GatewayEvidenceStatus.Observed, 1, 1, 2, [Parse(new() { [GatewayEvidenceHeaders.Cache] = "not-applicable" })]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"cache\":\"not-applicable\"").And.Contain("\"quota\":\"unknown\"");
        json.Should().NotContainEquivalentOf("subscription").And.NotContainEquivalentOf("prompt");
    }

    [Fact]
    public void ChatResponse_GatewayDefaultsToNull_PreservingExistingShape() => new Contracts.ChatResponse("r", "s", []).Gateway.Should().BeNull();

    [Fact]
    public void ApimPolicy_EmitsEvidenceHeadersAndStripsInboundSpoofing()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "infra", "modules", "apim-openai-policy.xml")))
            dir = Path.GetDirectoryName(dir)!;
        string xml = File.ReadAllText(Path.Combine(dir, "infra", "modules", "apim-openai-policy.xml"));

        // Cache is intentionally never emitted: the policy has no cache signal, so it stays unknown.
        foreach (string name in GatewayEvidenceHeaders.AllNames)
        {
            xml.Should().Contain($"<set-header name=\"{name}\" exists-action=\"delete\" />", name);
            bool demoOnly = name is GatewayEvidenceHeaders.FailoverDemo or GatewayEvidenceHeaders.PrimaryStatus
                or GatewayEvidenceHeaders.Attempts or GatewayEvidenceHeaders.Fallback;
            if (name != GatewayEvidenceHeaders.Cache && !demoOnly)
            {
                xml.Should().Contain($"<set-header name=\"{name}\" exists-action=\"override\">", name);
            }
        }

        xml.Should().NotContain("context.Subscription.Key").And.NotContain("Authorization");
    }

    [Fact]
    public void ApimFailoverDemoFragment_OverridesDemoHeadersOnlyFromDemoPolicy()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "infra", "modules", "apim-openai-policy.xml")))
            dir = Path.GetDirectoryName(dir)!;
        string demo = File.ReadAllText(Path.Combine(dir, "infra", "modules", "apim-openai-failover-demo-outbound.xml"));

        foreach (string name in new[] { GatewayEvidenceHeaders.FailoverDemo, GatewayEvidenceHeaders.PrimaryStatus, GatewayEvidenceHeaders.Attempts, GatewayEvidenceHeaders.Fallback })
        {
            demo.Should().Contain($"<set-header name=\"{name}\" exists-action=\"override\">", name);
        }
    }

    private static string Policy()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "infra", "modules", "apim-openai-policy.xml")))
            dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(dir, "infra", "modules", "apim-openai-policy.xml"));
    }

    private static string OutboundValue(string xml, string header)
    {
        int i = xml.IndexOf($"name=\"{header}\" exists-action=\"override\"", StringComparison.Ordinal);
        i.Should().BeGreaterThan(0, header);
        int v = xml.IndexOf("<value>", i, StringComparison.Ordinal);
        return xml[(v + 7)..xml.IndexOf("</value>", v, StringComparison.Ordinal)];
    }

    [Fact]
    public void ApimPolicy_DoesNotHardcodeSuccessValues()
    {
        string xml = Policy();

        foreach (string h in new[] { GatewayEvidenceHeaders.Route, GatewayEvidenceHeaders.Quota, GatewayEvidenceHeaders.Cache, GatewayEvidenceHeaders.Safety })
        {
            if (xml.Contains($"name=\"{h}\" exists-action=\"override\"", StringComparison.Ordinal))
            {
                int start = xml.IndexOf($"name=\"{h}\" exists-action=\"override\"", StringComparison.Ordinal);
                // Each emitted success-like value must sit inside a <when> guarded by a real signal.
                string before = xml[..start];
                int lastWhen = before.LastIndexOf("<when condition", StringComparison.Ordinal);
                int lastClose = before.LastIndexOf("</when>", StringComparison.Ordinal);
                (lastWhen > lastClose).Should().BeTrue($"{h} must be emitted only under a conditional signal");
            }

        }

        xml.Should().NotContain("\"primary\"");
        xml.Should().NotContain("not-applicable");
        xml.Should().NotContain("? \"passed\"");
    }

    [Fact]
    public void ApimPolicy_EmitsRemainingTokensAndBackendLatencyFromRealSignals()
    {
        string xml = Policy();

        xml.Should().Contain("remaining-tokens-variable-name=\"rp-tokens-remaining\"");
        OutboundValue(xml, GatewayEvidenceHeaders.TokensRemaining).Should().Contain("rp-tokens-remaining");
        OutboundValue(xml, GatewayEvidenceHeaders.BackendLatencyMs).Should().Contain("rp-backend-start");
        xml.Should().Contain("<set-variable name=\"rp-backend-start\"");
        OutboundValue(xml, GatewayEvidenceHeaders.Route).Should().Contain("rp-route");
    }

    [Fact]
    public void Parse_FailoverDemo_RequiresActiveMarker_SoSpoofedFieldsAreIgnored()
    {
        GatewayCallEvidence spoof = Parse(new()
        {
            [GatewayEvidenceHeaders.PrimaryStatus] = "503",
            [GatewayEvidenceHeaders.Attempts] = "2",
            [GatewayEvidenceHeaders.Fallback] = "used",
            [GatewayEvidenceHeaders.RequestId] = "r1",
        });

        spoof.FailoverDemo.Should().BeNull();
    }

    [Fact]
    public void Parse_FailoverDemo_MapsObservedAttempts()
    {
        GatewayCallEvidence e = Parse(new()
        {
            [GatewayEvidenceHeaders.FailoverDemo] = "active",
            [GatewayEvidenceHeaders.PrimaryStatus] = "400",
            [GatewayEvidenceHeaders.Attempts] = "2",
            [GatewayEvidenceHeaders.Fallback] = "used",
            [GatewayEvidenceHeaders.Route] = "failover",
            [GatewayEvidenceHeaders.Backend] = "retail-pulse-foundry-secondary",
        });

        e.FailoverDemo.Should().Be(new GatewayFailoverDemoEvidence(400, 2, true));
        e.Route.Should().Be(GatewayRouteState.Failover);
        e.BackendAlias.Should().Be("retail-pulse-foundry-secondary");
    }

    [Theory]
    [InlineData("99", "0", "maybe")]
    [InlineData("abc", "99", "")]
    public void Parse_FailoverDemo_DoesNotFabricateMissingOrInvalidFields(string primary, string attempts, string fallback)
    {
        GatewayCallEvidence e = Parse(new()
        {
            [GatewayEvidenceHeaders.FailoverDemo] = "active",
            [GatewayEvidenceHeaders.PrimaryStatus] = primary,
            [GatewayEvidenceHeaders.Attempts] = attempts,
            [GatewayEvidenceHeaders.Fallback] = fallback,
        });

        e.FailoverDemo.Should().Be(new GatewayFailoverDemoEvidence(null, null, null));
        e.Route.Should().Be(GatewayRouteState.Unknown);
    }

    [Fact]
    public void ApimPolicy_SafetyOnlyEmitsBlockedFromContentFilterSignal()
    {
        string xml = Policy();

        OutboundValue(xml, GatewayEvidenceHeaders.Safety).Should().Be("blocked");
        xml.Should().Contain("content_filter");
    }
}
