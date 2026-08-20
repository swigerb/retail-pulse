using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Persistence;

/// <summary>
/// Restart-durability test for the SQLite plan store — plan-side sibling of
/// <see cref="SessionStoreRestartTests"/>. Persist a plan through one store
/// instance, dispose the connection scope, then open a brand-new
/// <see cref="SqlitePlanStore"/> pointed at the same file. Every field must
/// survive the process boundary because #93 requires "plans persist and
/// survive API restart".
/// </summary>
public sealed class PlanStoreRestartTests : IDisposable
{
    private readonly string _dbPath;

    public PlanStoreRestartTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"plan_restart_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Full_Plan_Survives_A_Store_Restart_With_Full_Fidelity()
    {
        string planId = "plan-restart-" + Guid.NewGuid().ToString("N");
        DateTimeOffset started = DateTimeOffset.UtcNow.AddSeconds(-30);
        DateTimeOffset completed = DateTimeOffset.UtcNow;

        // "First process" — writer.
        {
            var writer = new SqlitePlanStore(_dbPath, Mock.Of<ILogger<SqlitePlanStore>>());
            await writer.CreatePlanAsync(new PlanWrite
            {
                PlanId = planId,
                Subject = "alice",
                SessionId = "session-1",
                TenantId = "Contoso",
                Request = "give me a scorecard and a comparison",
                DetectedIntents = ["scorecard", "comparison", "risk"],
                Status = PlanStatus.Running,
                CreatedAt = started,
                Steps =
                [
                    new PlanStepWrite
                    {
                        StepId = $"{planId}-s0",
                        StepIndex = 0,
                        SpecialistKey = "scorecard",
                        Intent = "scorecard",
                        Action = "produce scorecard",
                        Status = PlanStepStatus.Pending
                    },
                    new PlanStepWrite
                    {
                        StepId = $"{planId}-s1",
                        StepIndex = 1,
                        SpecialistKey = "comparison",
                        Intent = "comparison",
                        Action = "compare vs peers",
                        Status = PlanStepStatus.Pending
                    }
                ]
            });

            await writer.UpdateStepAsync(new PlanStepUpdate
            {
                StepId = $"{planId}-s0",
                PlanId = planId,
                Subject = "alice",
                Status = PlanStepStatus.Completed,
                Result = "scorecard body",
                InputTokens = 200,
                OutputTokens = 100,
                TotalTokens = 300,
                DurationMs = 450,
                StartedAt = started,
                CompletedAt = completed
            });
            await writer.UpdateStepAsync(new PlanStepUpdate
            {
                StepId = $"{planId}-s1",
                PlanId = planId,
                Subject = "alice",
                Status = PlanStepStatus.Failed,
                Error = "comparison unavailable"
            });
            await writer.UpdatePlanStatusAsync(new PlanStatusUpdate
            {
                PlanId = planId,
                Subject = "alice",
                Status = PlanStatus.Failed,
                FailureReason = "step 1 failed",
                TotalInputTokens = 200,
                TotalOutputTokens = 100,
                TotalTokens = 300,
                TotalDurationMs = 450,
                UpdatedAt = completed
            });
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // "Second process" — reader.
        var reader = new SqlitePlanStore(_dbPath, Mock.Of<ILogger<SqlitePlanStore>>());

        PlanDetailDto? read = await reader.GetPlanAsync("alice", planId);
        read.Should().NotBeNull("plans must survive a process restart");
        read.Status.Should().Be(PlanStatus.Failed);
        read.FailureReason.Should().Be("step 1 failed");
        read.TotalInputTokens.Should().Be(200);
        read.TotalOutputTokens.Should().Be(100);
        read.TotalTokens.Should().Be(300);
        read.TotalDurationMs.Should().Be(450);
        read.DetectedIntents.Should().BeEquivalentTo(["scorecard", "comparison", "risk"]);

        read.Steps.Should().HaveCount(2);
        read.Steps[0].Status.Should().Be(PlanStepStatus.Completed);
        read.Steps[0].Result.Should().Be("scorecard body");
        read.Steps[0].InputTokens.Should().Be(200);
        read.Steps[0].OutputTokens.Should().Be(100);
        read.Steps[0].TotalTokens.Should().Be(300);
        read.Steps[0].DurationMs.Should().Be(450);
        read.Steps[0].StartedAt.Should().NotBeNull();
        read.Steps[0].CompletedAt.Should().NotBeNull();
        read.Steps[1].Status.Should().Be(PlanStepStatus.Failed);
        read.Steps[1].Error.Should().Be("comparison unavailable");

        // Ownership still enforced across a restart.
        (await reader.GetPlanAsync("bob", planId)).Should().BeNull();

        // SMB-safe pragmas: no persistent WAL sidecars.
        File.Exists(_dbPath + "-wal").Should().BeFalse();
        File.Exists(_dbPath + "-shm").Should().BeFalse();
    }
}
