using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;

namespace RetailPulse.Tests.Explainability;

/// <summary>
/// Tests for the explainability/tracing system: tool call capture,
/// step ordering, trace retrieval via GET /api/explain/{traceId},
/// confidence scoring, and immutability after creation.
/// Test-first: defines expected explainability contracts before Phase 4.3 implementation.
/// </summary>
public class ExplainabilityTests
{
    #region Tool Call Capture

    [Fact]
    public void Explanation_CapturesToolCallsInOrder()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        store.RecordStep(traceId, new ExplanationStep("GetDepletionStats", /*lang=json,strict*/ "{\"brand\":\"Sierra Gold\"}", /*lang=json,strict*/ "{\"depletions\":1200}", "Fetching depletion data for the requested brand"));
        store.RecordStep(traceId, new ExplanationStep("CreateChart", /*lang=json,strict*/ "{\"type\":\"bar\"}", /*lang=json,strict*/ "{\"chartUrl\":\"/charts/123\"}", "Visualizing the depletion data as a bar chart"));

        ExplanationChain? chain = store.GetChain(traceId);

        chain.Should().NotBeNull();
        chain.Steps.Should().HaveCount(2);
        chain.Steps[0].ToolName.Should().Be("GetDepletionStats");
        chain.Steps[1].ToolName.Should().Be("CreateChart");
    }

    [Fact]
    public void Explanation_StepsAreInInsertionOrder()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        string[] toolNames = ["ToolA", "ToolB", "ToolC", "ToolD"];
        foreach (string? name in toolNames)
        {
            store.RecordStep(traceId, new ExplanationStep(name, "{}", "{}", "reasoning"));
        }

        ExplanationChain? chain = store.GetChain(traceId);
        chain!.Steps.Select(s => s.ToolName).Should().BeEquivalentTo(
            toolNames, opts => opts.WithStrictOrdering());
    }

    #endregion

    #region Step Structure

    [Fact]
    public void Explanation_EachStepHasToolNameInputOutputReasoning()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        store.RecordStep(traceId, new ExplanationStep(
            "GetMarginByBrand",
                                 /*lang=json,strict*/
                                 "{\"brand\":\"Ridgeline Bourbon\"}",
                                 /*lang=json,strict*/
                                 "{\"revenue\":5000000,\"gross_margin\":2000000}",
            "Retrieving P&L breakdown for margin analysis"));

        ExplanationChain? chain = store.GetChain(traceId);
        ExplanationStep step = chain!.Steps.Single();

        step.ToolName.Should().Be("GetMarginByBrand");
        step.Input.Should().NotBeNullOrEmpty("step should have input");
        step.Output.Should().NotBeNullOrEmpty("step should have output");
        step.Reasoning.Should().NotBeNullOrEmpty("step should have reasoning");

        // Input and Output should be valid JSON
        var inputDoc = JsonDocument.Parse(step.Input);
        inputDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        var outputDoc = JsonDocument.Parse(step.Output);
        outputDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    #endregion

    #region Trace Retrieval

    [Fact]
    public void GetExplanation_ValidTraceId_ReturnsValidChain()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        store.RecordStep(traceId, new ExplanationStep("Tool1", "{}", "{}", "step 1"));
        store.RecordStep(traceId, new ExplanationStep("Tool2", "{}", "{}", "step 2"));

        ExplanationChain? chain = store.GetChain(traceId);

        chain.Should().NotBeNull();
        chain.TraceId.Should().Be(traceId);
        chain.Steps.Should().HaveCount(2);
    }

    [Fact]
    public void GetExplanation_UnknownTraceId_ReturnsNull()
    {
        var store = new InMemoryExplanationStore();

        ExplanationChain? chain = store.GetChain("nonexistent-trace-id");

        chain.Should().BeNull("unknown traceId should return null (maps to 404 in API)");
    }

    #endregion

    #region Confidence Score

    [Fact]
    public void Explanation_IncludesConfidenceScore()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        store.RecordStep(traceId, new ExplanationStep("Tool1", "{}", "{}", "reasoning", 0.92));

        ExplanationChain? chain = store.GetChain(traceId);
        chain!.Steps[0].Confidence.Should().Be(0.92);
    }

    [Fact]
    public void Explanation_ConfidenceScoreRange_0To1()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        store.RecordStep(traceId, new ExplanationStep("HighConf", "{}", "{}", "very sure", 0.99));
        store.RecordStep(traceId, new ExplanationStep("LowConf", "{}", "{}", "uncertain", 0.15));
        store.RecordStep(traceId, new ExplanationStep("MedConf", "{}", "{}", "moderate", 0.55));

        ExplanationChain? chain = store.GetChain(traceId);

        foreach (ExplanationStep step in chain!.Steps)
        {
            step.Confidence.Should().BeGreaterThanOrEqualTo(0.0,
                $"step '{step.ToolName}' confidence should be >= 0");
            step.Confidence.Should().BeLessThanOrEqualTo(1.0,
                $"step '{step.ToolName}' confidence should be <= 1");
        }
    }

    #endregion

    #region Multiple Tool Calls

    [Fact]
    public void Explanation_MultipleToolCalls_CreateMultipleSteps()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        for (int i = 1; i <= 5; i++)
        {
            store.RecordStep(traceId, new ExplanationStep(
                $"Tool{i}", $"{{\"param\":{i}}}", $"{{\"result\":{i}}}", $"Step {i} reasoning"));
        }

        ExplanationChain? chain = store.GetChain(traceId);
        chain!.Steps.Should().HaveCount(5, "5 tool calls should create 5 steps");
    }

    [Fact]
    public void Explanation_DifferentTraces_AreIsolated()
    {
        var store = new InMemoryExplanationStore();
        string traceA = Guid.NewGuid().ToString("N");
        string traceB = Guid.NewGuid().ToString("N");

        store.RecordStep(traceA, new ExplanationStep("ToolA", "{}", "{}", "trace A"));
        store.RecordStep(traceA, new ExplanationStep("ToolB", "{}", "{}", "trace A"));
        store.RecordStep(traceB, new ExplanationStep("ToolX", "{}", "{}", "trace B"));

        ExplanationChain? chainA = store.GetChain(traceA);
        ExplanationChain? chainB = store.GetChain(traceB);

        chainA!.Steps.Should().HaveCount(2);
        chainB!.Steps.Should().HaveCount(1);
        chainA.Steps.Should().OnlyContain(s => s.Reasoning.Contains("trace A"));
        chainB.Steps.Should().OnlyContain(s => s.Reasoning.Contains("trace B"));
    }

    #endregion

    #region Immutability

    [Fact]
    public void Explanation_ChainIsImmutableAfterCreation()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        store.RecordStep(traceId, new ExplanationStep("Tool1", "{}", "{}", "step 1"));
        store.RecordStep(traceId, new ExplanationStep("Tool2", "{}", "{}", "step 2"));

        // Finalize the chain (mark as complete)
        store.FinalizeChain(traceId);

        // Attempting to add more steps after finalization should fail or be ignored
        Action act = () => store.RecordStep(traceId, new ExplanationStep("Tool3", "{}", "{}", "too late"));

        act.Should().Throw<InvalidOperationException>(
            "should not allow adding steps to a finalized explanation chain");
    }

    [Fact]
    public void Explanation_RetrievedChainIsSnapshot()
    {
        var store = new InMemoryExplanationStore();
        string traceId = Guid.NewGuid().ToString("N");

        store.RecordStep(traceId, new ExplanationStep("Tool1", "{}", "{}", "step 1"));

        ExplanationChain? chain1 = store.GetChain(traceId);
        int count1 = chain1!.Steps.Count;

        // Add another step
        store.RecordStep(traceId, new ExplanationStep("Tool2", "{}", "{}", "step 2"));

        // Original retrieved chain should not have been mutated
        chain1.Steps.Should().HaveCount(count1,
            "retrieved chain should be an immutable snapshot");
    }

    #endregion
}

#region Explainability Contracts (test-first definitions)

public record ExplanationStep(
    string ToolName,
    string Input,
    string Output,
    string Reasoning,
    double Confidence
)
{
    public ExplanationStep(string toolName, string input, string output, string reasoning)
        : this(toolName, input, output, reasoning, 0.0) { }
}

public record ExplanationChain(
    string TraceId,
    IReadOnlyList<ExplanationStep> Steps,
    bool IsFinalized = false
);

/// <summary>
/// In-memory explanation store for testing. Thread-safe.
/// Supports recording steps, finalization (immutability), and retrieval.
/// </summary>
internal sealed class InMemoryExplanationStore
{
    private readonly ConcurrentDictionary<string, (List<ExplanationStep> Steps, bool Finalized)> _chains = new();

    public void RecordStep(string traceId, ExplanationStep step)
    {
        _chains.AddOrUpdate(traceId,
            _ => ([step], false),
            (_, existing) =>
            {
                if (existing.Finalized)
                    throw new InvalidOperationException($"Explanation chain '{traceId}' is finalized and immutable.");
                existing.Steps.Add(step);
                return existing;
            });
    }

    public ExplanationChain? GetChain(string traceId)
    {
        if (!_chains.TryGetValue(traceId, out (List<ExplanationStep> Steps, bool Finalized) data))
            return null;

        // Return immutable snapshot
        return new ExplanationChain(traceId, data.Steps.ToList().AsReadOnly(), data.Finalized);
    }

    public void FinalizeChain(string traceId)
    {
        if (_chains.TryGetValue(traceId, out (List<ExplanationStep> Steps, bool Finalized) data))
        {
            _chains[traceId] = (data.Steps, true);
        }
    }
}

#endregion
