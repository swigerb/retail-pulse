using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Mid-plan clarification round-trip (#94). Exercises the shared-gate storage
/// path so the same audit history holds both plan review and clarification.
/// </summary>
public sealed class PlanClarifierTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _checkpointDir;

    public PlanClarifierTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"plan_clarify_{Guid.NewGuid():N}.db");
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"plan_clarify_ckpt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_checkpointDir);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { Directory.Delete(_checkpointDir, recursive: true); } catch { }
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private SqliteApprovalGate CreateGate() =>
        new(_dbPath, Mock.Of<ILogger<SqliteApprovalGate>>(),
            TimeSpan.FromSeconds(30), TimeProvider.System);

    private PlanReviewCheckpointService CreateCheckpointService()
    {
        FileSystemJsonCheckpointStore store =
            new(new DirectoryInfo(_checkpointDir));
        CheckpointManager manager = CheckpointManager.CreateJson(store, customOptions: null);
        return new PlanReviewCheckpointService(store, manager, Mock.Of<ILogger<PlanReviewCheckpointService>>());
    }

    private PlanClarifier CreateClarifier(SqliteApprovalGate gate, PlanReviewOptions options) =>
        new(gate, Options.Create(options), CreateCheckpointService(), Mock.Of<ILogger<PlanClarifier>>());

    [Fact]
    public async Task Clarification_round_trip_returns_reviewer_answer()
    {
        SqliteApprovalGate gate = CreateGate();
        var options = new PlanReviewOptions { ClarificationTimeout = TimeSpan.FromSeconds(30) };
        PlanClarifier clarifier = CreateClarifier(gate, options);

        var prompt = new PlanClarificationPrompt
        {
            PlanId = "p-1",
            StepIndex = 1,
            SpecialistKey = "demand-forecasting",
            Question = "Which region should we forecast?",
        };

        // Fire the clarification and wait for the row to appear.
        Task<PlanClarificationResult> task = clarifier.AskAsync(prompt, "user-1");
        ApprovalRequest row = await WaitForPending(gate, "user-1");

        row.Context.Kind.Should().Be(ApprovalKind.Clarification);
        row.Context.PlanId.Should().Be("p-1");

        var answer = new PlanClarificationAnswer { Answer = "Northeast" };
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "Northeast",
            responsePayload: JsonSerializer.Serialize(answer, _json));

        PlanClarificationResult result = await task;
        result.IsAnswered.Should().BeTrue();
        result.Answer.Should().Be("Northeast");
    }

    [Fact]
    public async Task Clarification_without_response_payload_returns_ClarificationInvalid()
    {
        SqliteApprovalGate gate = CreateGate();
        var options = new PlanReviewOptions();
        PlanClarifier clarifier = CreateClarifier(gate, options);

        var prompt = new PlanClarificationPrompt
        {
            PlanId = "p-1",
            StepIndex = 0,
            SpecialistKey = "scorecard",
            Question = "Which brand?",
        };

        Task<PlanClarificationResult> task = clarifier.AskAsync(prompt, "user-1");
        ApprovalRequest row = await WaitForPending(gate, "user-1");

        // Respond WITHOUT a payload — should surface as an invalid clarification.
        await gate.RespondAsync(row.RequestId, ApprovalDecision.Approved, "answered");

        PlanClarificationResult result = await task;
        result.IsAnswered.Should().BeFalse();
        result.TerminalReason.Should().Be(PlanReviewTerminalReason.ClarificationInvalid);
    }

    private static async Task<ApprovalRequest> WaitForPending(SqliteApprovalGate gate, string subject)
    {
        for (int i = 0; i < 400; i++)
        {
            IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(subject);
            if (pending.Count > 0) return pending[^1];
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Timed out waiting for pending row.");
    }
}
