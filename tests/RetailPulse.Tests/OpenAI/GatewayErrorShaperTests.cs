using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.OpenAI.Gateway;
using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Tests.OpenAI;

public sealed class GatewayErrorShaperTests
{
    private static GatewayEvidence Snapshot(int status, string decision, string safety = "blocked", string phase = "input")
    {
        var headers = new Dictionary<string, string>
        {
            [GatewayEvidenceHeaders.Safety] = safety,
            [GatewayEvidenceHeaders.SafetyPhase] = phase,
            [GatewayEvidenceHeaders.SafetyDecision] = decision,
            [GatewayEvidenceHeaders.SafetyPromptAttack] = "true",
            [GatewayEvidenceHeaders.SafetyCategory] = "Violence",
            [GatewayEvidenceHeaders.SafetySeverity] = "6",
            [GatewayEvidenceHeaders.SafetyMode] = "regulated",
            [GatewayEvidenceHeaders.SafetyLatencyMs] = "42",
            [GatewayEvidenceHeaders.SafetyOutputChecked] = "false",
        };
        GatewayCallEvidence call = GatewayEvidenceHeaders.Parse(n => headers.GetValueOrDefault(n), status);
        return new GatewayEvidence(GatewayEvidenceStatus.Blocked, 1, null, null, [call]);
    }

    [Theory]
    [InlineData(403, GatewayErrorShaper.Blocked)]
    [InlineData(400, GatewayErrorShaper.Rejected)]
    [InlineData(503, GatewayErrorShaper.Unavailable)]
    public void Classify_BlockedSafetyStatus_KeepsDistinctCodeAndEvidence(int status, string code)
    {
        GatewayErrorShaper.Shaped s = GatewayErrorShaper.Classify(status, "ai_service_error", "generic", Snapshot(status, "block"));

        s.Code.Should().Be(code);
        s.Gateway!.Calls[0].GatewaySafety.Should().BeEquivalentTo(new GatewaySafetyEvidence(
            GatewaySafetyPhase.Input, GatewaySafetyDecision.Block, true, "Violence", 6, "regulated", 42, false));
    }

    [Fact]
    public void Classify_SerializedBody_CarriesOnlyAllowListedSafetyFields()
    {
        GatewayErrorShaper.Shaped s = GatewayErrorShaper.Classify(403, "ai_service_error", "generic", Snapshot(403, "block"));

        string json = JsonSerializer.Serialize(new { error = s.Message, code = s.Code, gateway = s.Gateway }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var doc = JsonDocument.Parse(json);
        JsonElement safety = doc.RootElement.GetProperty("gateway").GetProperty("calls")[0].GetProperty("gatewaySafety");
        safety.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "phase", "decision", "promptAttack", "category", "severity", "mode", "latencyMs", "outputChecked", "outputCheckReason");
        safety.GetProperty("decision").GetString().Should().Be("block");
        safety.GetProperty("outputCheckReason").GetString().Should().Be("unknown");
    }

    [Fact]
    public void Classify_BypassedMode_IgnoresSpoofedSafetyHeaders()
    {
        // Non-APIM clients never parse x-rp-gw-* headers, so a backend-supplied block cannot masquerade as gateway evidence.
        var snapshot = new GatewayEvidence(GatewayEvidenceStatus.Bypassed, 1, null, null,
            [GatewayCallEvidence.Empty(GatewayEvidenceStatus.Bypassed, 403)]);

        GatewayErrorShaper.Shaped s = GatewayErrorShaper.Classify(403, "ai_service_error", "generic", snapshot);

        s.Code.Should().Be("ai_service_error");
    }

    [Fact]
    public void Classify_StatusMismatch_FallsBackToGenericError()
    {
        GatewayErrorShaper.Shaped s = GatewayErrorShaper.Classify(500, "ai_service_error", "generic", Snapshot(403, "block"));

        s.Code.Should().Be("ai_service_error");
    }

    [Fact]
    public void Classify_NoGatewayEvidence_FallsBackToGenericError()
    {
        GatewayErrorShaper.Classify(503, "ai_service_error", "generic", GatewayEvidence.None).Code.Should().Be("ai_service_error");
        GatewayErrorShaper.Classify(503, "ai_service_error", "generic", null).Code.Should().Be("ai_service_error");
    }

    [Fact]
    public void Classify_UnknownDecision_IsNeverTreatedAsSafetyBlock()
    {
        GatewayErrorShaper.Shaped s = GatewayErrorShaper.Classify(403, "ai_service_error", "generic", Snapshot(403, "mystery"));

        s.Code.Should().Be("ai_service_error");
    }

    [Fact]
    public void AgentPipeline_RethrowsGatewaySafetyStatuses_ForEndpointShaping()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "RetailPulse.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        root.Should().NotBeNull();
        string repoRoot = root ?? throw new InvalidOperationException("Repository root not found.");
        string source = File.ReadAllText(Path.Combine(repoRoot, "src", "RetailPulse.Api", "Agents", "AgentExecutionPipeline.cs"));
        const string marker = "catch (ClientResultException ex) when (ex.Status is 400 or 403 or 503)";
        (source.Split(marker, StringSplitOptions.None).Length - 1).Should().Be(2,
            "both specialist execution paths must preserve safety failures for the endpoint shaper");
    }
}
