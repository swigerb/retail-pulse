using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Persistence;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Persistence;

/// <summary>
/// Core round-trip and ownership tests for <see cref="SqlitePlanStore"/> — the
/// plan-side sibling of <see cref="SqliteSessionStoreTests"/>.
/// </summary>
public sealed class SqlitePlanStoreTests : IDisposable
{
    private readonly string _dbPath;

    public SqlitePlanStoreTests()
    {
        _dbPath = SqliteTestCleanup.NewDbPath("plan_store");
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    private SqlitePlanStore NewStore() =>
        new(_dbPath, Mock.Of<ILogger<SqlitePlanStore>>());

    private static PlanWrite MakePlan(
        string planId,
        string subject,
        string request = "multi-domain question")
    {
        (string Key, string Action)[] stepList =
        [
            ("scorecard", "summarize scorecard"),
            ("comparison", "compare vs peer"),
        ];

        return new PlanWrite
        {
            PlanId = planId,
            Subject = subject,
            SessionId = "session-" + planId,
            TenantId = "Contoso",
            Request = request,
            DetectedIntents = ["scorecard", "comparison"],
            Status = PlanStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            Steps = [.. stepList.Select((s, i) => new PlanStepWrite
            {
                StepId = $"{planId}-s{i}",
                StepIndex = i,
                SpecialistKey = s.Key,
                Intent = s.Key,
                Action = s.Action,
                Status = PlanStepStatus.Pending
            })]
        };
    }

    [Fact]
    public async Task Creating_A_Plan_Roundtrips_All_Fields()
    {
        SqlitePlanStore store = NewStore();
        PlanWrite write = MakePlan("plan-1", "alice");

        await store.CreatePlanAsync(write);

        PlanDetailDto? read = await store.GetPlanAsync("alice", "plan-1");
        read.Should().NotBeNull();
        read.PlanId.Should().Be("plan-1");
        read.SessionId.Should().Be("session-plan-1");
        read.TenantId.Should().Be("Contoso");
        read.Request.Should().Be("multi-domain question");
        read.Status.Should().Be(PlanStatus.Running);
        read.DetectedIntents.Should().BeEquivalentTo(["scorecard", "comparison"]);
        read.Steps.Should().HaveCount(2);
        read.Steps[0].SpecialistKey.Should().Be("scorecard");
        read.Steps[0].StepIndex.Should().Be(0);
        read.Steps[1].SpecialistKey.Should().Be("comparison");
        read.Steps[1].StepIndex.Should().Be(1);
    }

    [Fact]
    public async Task Cross_Subject_Reads_Return_Null()
    {
        SqlitePlanStore store = NewStore();
        await store.CreatePlanAsync(MakePlan("plan-2", "alice"));

        (await store.GetPlanAsync("bob", "plan-2")).Should().BeNull();
        (await store.ListPlansForSubjectAsync("bob")).Should().BeEmpty();
    }

    [Fact]
    public async Task Status_And_Step_Updates_Are_Applied()
    {
        SqlitePlanStore store = NewStore();
        await store.CreatePlanAsync(MakePlan("plan-3", "alice"));

        DateTimeOffset started = DateTimeOffset.UtcNow;
        await store.UpdateStepAsync(new PlanStepUpdate
        {
            StepId = "plan-3-s0",
            PlanId = "plan-3",
            Subject = "alice",
            Status = PlanStepStatus.Running,
            StartedAt = started
        });
        await store.UpdateStepAsync(new PlanStepUpdate
        {
            StepId = "plan-3-s0",
            PlanId = "plan-3",
            Subject = "alice",
            Status = PlanStepStatus.Completed,
            Result = "step 0 said hello",
            InputTokens = 100,
            OutputTokens = 50,
            TotalTokens = 150,
            DurationMs = 250,
            CompletedAt = DateTimeOffset.UtcNow
        });

        await store.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = "plan-3",
            Subject = "alice",
            Status = PlanStatus.Completed,
            TotalTokens = 150,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        PlanDetailDto? read = await store.GetPlanAsync("alice", "plan-3");
        read.Should().NotBeNull();
        read.Status.Should().Be(PlanStatus.Completed);
        read.TotalTokens.Should().Be(150);
        read.Steps[0].Status.Should().Be(PlanStepStatus.Completed);
        read.Steps[0].Result.Should().Be("step 0 said hello");
        read.Steps[0].TotalTokens.Should().Be(150);
        read.Steps[0].DurationMs.Should().Be(250);
        read.Steps[0].StartedAt.Should().NotBeNull();
        read.Steps[0].CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Step_Update_From_Wrong_Subject_Is_Silently_Rejected()
    {
        SqlitePlanStore store = NewStore();
        await store.CreatePlanAsync(MakePlan("plan-4", "alice"));

        await store.UpdateStepAsync(new PlanStepUpdate
        {
            StepId = "plan-4-s0",
            PlanId = "plan-4",
            Subject = "bob",
            Status = PlanStepStatus.Completed,
            Result = "should not stick"
        });

        PlanDetailDto? read = await store.GetPlanAsync("alice", "plan-4");
        read.Should().NotBeNull();
        read.Steps[0].Status.Should().Be(PlanStepStatus.Pending);
        read.Steps[0].Result.Should().BeNull();
    }

    [Fact]
    public async Task List_Plans_Returns_Newest_First()
    {
        SqlitePlanStore store = NewStore();
        await store.CreatePlanAsync(MakePlan("plan-a", "alice") with { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        await store.CreatePlanAsync(MakePlan("plan-b", "alice") with { CreatedAt = DateTimeOffset.UtcNow });

        await store.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = "plan-b",
            Subject = "alice",
            Status = PlanStatus.Running,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        IReadOnlyList<PlanSummaryDto> list = await store.ListPlansForSubjectAsync("alice");
        list.Should().HaveCount(2);
        list[0].PlanId.Should().Be("plan-b");
        list[0].StepCount.Should().Be(2);
    }

    [Fact]
    public async Task Delete_Only_Owner_Can_Delete()
    {
        SqlitePlanStore store = NewStore();
        await store.CreatePlanAsync(MakePlan("plan-5", "alice"));

        (await store.DeletePlanAsync("bob", "plan-5")).Should().BeFalse("bob does not own plan-5");
        (await store.GetPlanAsync("alice", "plan-5")).Should().NotBeNull();

        (await store.DeletePlanAsync("alice", "plan-5")).Should().BeTrue();
        (await store.GetPlanAsync("alice", "plan-5")).Should().BeNull();
    }

    [Fact]
    public async Task Purge_Expired_Deletes_Old_Plans_And_Their_Steps()
    {
        SqlitePlanStore store = NewStore();

        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-30);
        await store.CreatePlanAsync(MakePlan("plan-old", "alice") with { CreatedAt = old });
        await store.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = "plan-old",
            Subject = "alice",
            Status = PlanStatus.Completed,
            UpdatedAt = old
        });
        await store.CreatePlanAsync(MakePlan("plan-fresh", "alice"));

        PlanCleanupResult result = await store.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(-7));

        result.PlansDeleted.Should().Be(1);
        result.StepsDeleted.Should().Be(2);
        (await store.GetPlanAsync("alice", "plan-old")).Should().BeNull();
        (await store.GetPlanAsync("alice", "plan-fresh")).Should().NotBeNull();
    }

    /// <summary>
    /// Issue #149: a terminal plan-status write must atomically sweep every
    /// remaining Pending / Running step row for the plan to Skipped so no
    /// orphan step rows survive after the plan reaches its terminal state.
    /// The parallel `{planId}-r{round}-s{i}` execution rows written by the
    /// review-approved path leave the initial `{planId}-s{i}` rows Pending
    /// forever without this guarantee.
    /// </summary>
    [Theory]
    [InlineData(PlanStatus.Completed)]
    [InlineData(PlanStatus.Failed)]
    [InlineData(PlanStatus.Cancelled)]
    [InlineData(PlanStatus.Unusable)]
    public async Task Terminal_Status_Sweeps_Orphaned_Pending_Steps(string terminalStatus)
    {
        SqlitePlanStore store = NewStore();
        await store.CreatePlanAsync(MakePlan("plan-149", "alice"));

        // Baseline: both initial rows are Pending, as SuspendForReviewAsync writes them.
        PlanDetailDto? before = await store.GetPlanAsync("alice", "plan-149");
        before.Should().NotBeNull();
        before.Steps.Should().OnlyContain(s => s.Status == PlanStepStatus.Pending);

        await store.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = "plan-149",
            Subject = "alice",
            Status = terminalStatus,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        PlanDetailDto? after = await store.GetPlanAsync("alice", "plan-149");
        after.Should().NotBeNull();
        after.Status.Should().Be(terminalStatus);
        after.Steps.Should().NotContain(s => s.Status == PlanStepStatus.Pending,
            $"a {terminalStatus} plan must not leave step rows in Pending (issue #149).");
        after.Steps.Should().NotContain(s => s.Status == PlanStepStatus.Running,
            $"a {terminalStatus} plan must not leave step rows in Running (issue #149).");
        after.Steps.Should().OnlyContain(s => s.Status == PlanStepStatus.Skipped,
            "the initial planner-proposed step rows are swept to Skipped once the plan is terminal.");
        after.Steps.Should().OnlyContain(s => s.CompletedAt != null,
            "the sweep must stamp CompletedAt so downstream consumers see the row as terminal-with-timestamp.");
    }

    [Fact]
    public async Task Terminal_Status_Sweep_Preserves_Steps_Already_Completed()
    {
        SqlitePlanStore store = NewStore();
        await store.CreatePlanAsync(MakePlan("plan-mixed", "alice"));

        DateTimeOffset step0Completed = DateTimeOffset.UtcNow.AddSeconds(-5);
        await store.UpdateStepAsync(new PlanStepUpdate
        {
            StepId = "plan-mixed-s0",
            PlanId = "plan-mixed",
            Subject = "alice",
            Status = PlanStepStatus.Completed,
            Result = "s0 done",
            CompletedAt = step0Completed,
        });

        await store.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = "plan-mixed",
            Subject = "alice",
            Status = PlanStatus.Completed,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        PlanDetailDto? after = await store.GetPlanAsync("alice", "plan-mixed");
        after.Should().NotBeNull();

        PlanStepRecordDto step0 = after.Steps.Single(s => s.StepId == "plan-mixed-s0");
        step0.Status.Should().Be(PlanStepStatus.Completed,
            "a step that reached Completed before the plan terminal write must NOT be rewritten by the sweep.");
        step0.Result.Should().Be("s0 done",
            "the sweep must leave the existing Result untouched — it only transitions Pending/Running rows.");

        PlanStepRecordDto step1 = after.Steps.Single(s => s.StepId == "plan-mixed-s1");
        step1.Status.Should().Be(PlanStepStatus.Skipped,
            "the step that never ran must be swept to Skipped by the terminal transition.");
    }

    [Fact]
    public async Task Non_Terminal_Status_Update_Does_Not_Sweep_Pending_Steps()
    {
        SqlitePlanStore store = NewStore();
        await store.CreatePlanAsync(MakePlan("plan-run", "alice"));

        await store.UpdatePlanStatusAsync(new PlanStatusUpdate
        {
            PlanId = "plan-run",
            Subject = "alice",
            Status = PlanStatus.Running,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        PlanDetailDto? after = await store.GetPlanAsync("alice", "plan-run");
        after.Should().NotBeNull();
        after.Status.Should().Be(PlanStatus.Running);
        after.Steps.Should().OnlyContain(s => s.Status == PlanStepStatus.Pending,
            "non-terminal status transitions must NOT sweep step rows — steps are still legitimately Pending.");
    }
}
