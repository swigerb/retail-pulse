using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Persistence;

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
        _dbPath = Path.Combine(Path.GetTempPath(), $"plan_store_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
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
}
