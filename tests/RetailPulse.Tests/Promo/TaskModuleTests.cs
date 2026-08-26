using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Promo;

/// <summary>
/// Tests for the promo task module approval workflow:
/// PromoPlanningAgent.CheckApprovalAsync, IApprovalGate integration,
/// and EstimateROI approval flag consistency via RetailPulseDb.
/// </summary>
public class TaskModuleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public TaskModuleTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_taskmod_test");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    private static JsonElement Parse(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    #region Helpers

    private static PromoPlanningAgent CreateAgent(IApprovalGate? gate = null)
    {
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var pipeline = new AgentExecutionPipeline(
            Mock.Of<IChatClient>(),
            hubContext,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        return new PromoPlanningAgent(
            pipeline,
            new AgentDefinition { Name = "PromoPlanningAgent", Model = "gpt-5.4-mini", SystemPrompt = "test", Temperature = 0.3 },
            [], gate);
    }

    private static IHubContext<TelemetryHub> CreateMockHubContext()
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }

    private static Mock<IApprovalGate> CreateMockGate(ApprovalDecision decision, string? comment = null)
    {
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApprovalContext ctx, CancellationToken _) => new ApprovalRequest(
                RequestId: Guid.NewGuid().ToString("N"),
                Context: ctx,
                CreatedAt: DateTimeOffset.UtcNow,
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                Decision: ApprovalDecision.Pending));

        gate.Setup(g => g.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string reqId, CancellationToken _) => new ApprovalResult(
                RequestId: reqId,
                Decision: decision,
                Comment: comment,
                RespondedAt: DateTimeOffset.UtcNow));

        return gate;
    }

    #endregion

    #region CheckApprovalAsync — approval threshold logic

    [Fact]
    public async Task CheckApproval_HighSpend_RequiresApproval()
    {
        Mock<IApprovalGate> gate = CreateMockGate(ApprovalDecision.Approved);
        PromoPlanningAgent agent = CreateAgent(gate.Object);

        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 600_000, roi: 50, userId: "user-1", description: "High-budget campaign");

        result.Should().NotBeNull();
        result.Decision.Should().Be(ApprovalDecision.Approved);
        gate.Verify(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckApproval_MediumSpendLowRoi_RequiresApproval()
    {
        Mock<IApprovalGate> gate = CreateMockGate(ApprovalDecision.Approved);
        PromoPlanningAgent agent = CreateAgent(gate.Object);

        // spend > $100K and ROI < 10% triggers approval
        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 150_000, roi: 5.0, userId: "user-2", description: "Low ROI campaign");

        result.Should().NotBeNull();
        gate.Verify(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckApproval_NormalSpendGoodRoi_NoApprovalNeeded()
    {
        Mock<IApprovalGate> gate = CreateMockGate(ApprovalDecision.Approved);
        PromoPlanningAgent agent = CreateAgent(gate.Object);

        // $50K spend with 50% ROI — well below both thresholds
        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 50_000, roi: 50, userId: "user-3", description: "Normal campaign");

        result.Should().BeNull("no approval needed for low spend with good ROI");
        gate.Verify(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckApproval_BoundarySpend500K_RequiresApproval()
    {
        Mock<IApprovalGate> gate = CreateMockGate(ApprovalDecision.Approved);
        PromoPlanningAgent agent = CreateAgent(gate.Object);

        // Exactly $500,001 — just above the high-spend threshold
        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 500_001, roi: 50, userId: "user-4", description: "Boundary campaign");

        result.Should().NotBeNull("spend > 500K triggers approval regardless of ROI");
        gate.Verify(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckApproval_BoundarySpend100K_WithLowRoi_RequiresApproval()
    {
        Mock<IApprovalGate> gate = CreateMockGate(ApprovalDecision.Approved);
        PromoPlanningAgent agent = CreateAgent(gate.Object);

        // $100,001 with ROI 9.9 — both conditions met for medium-spend low-ROI rule
        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 100_001, roi: 9.9, userId: "user-5", description: "Boundary low-ROI campaign");

        result.Should().NotBeNull("spend > 100K and ROI < 10 triggers approval");
        gate.Verify(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckApproval_MediumSpendHighRoi_NoApprovalNeeded()
    {
        Mock<IApprovalGate> gate = CreateMockGate(ApprovalDecision.Approved);
        PromoPlanningAgent agent = CreateAgent(gate.Object);

        // $150K with 15% ROI — above spend threshold but ROI >= 10, so no approval
        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 150_000, roi: 15, userId: "user-6", description: "Good ROI campaign");

        result.Should().BeNull("medium spend with good ROI does not require approval");
        gate.Verify(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Approval Gate Integration — mock IApprovalGate

    [Fact]
    public async Task ApprovalGate_WhenNull_ReturnsNull()
    {
        PromoPlanningAgent agent = CreateAgent(gate: null);

        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 600_000, roi: 50, userId: "user-1", description: "No gate configured");

        result.Should().BeNull("no approval gate configured — returns null immediately");
    }

    [Fact]
    public async Task ApprovalGate_WhenApproved_ReturnsApproved()
    {
        Mock<IApprovalGate> gate = CreateMockGate(ApprovalDecision.Approved, "Looks good");
        PromoPlanningAgent agent = CreateAgent(gate.Object);

        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 600_000, roi: 50, userId: "user-1", description: "Approved campaign");

        result.Should().NotBeNull();
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.Comment.Should().Be("Looks good");
    }

    [Fact]
    public async Task ApprovalGate_WhenDenied_ReturnsDenied()
    {
        Mock<IApprovalGate> gate = CreateMockGate(ApprovalDecision.Rejected, "Too risky");
        PromoPlanningAgent agent = CreateAgent(gate.Object);

        ApprovalResult? result = await agent.CheckApprovalAsync(
            spend: 600_000, roi: 50, userId: "user-1", description: "Rejected campaign");

        result.Should().NotBeNull();
        result.Decision.Should().Be(ApprovalDecision.Rejected);
        result.Comment.Should().Be("Too risky");
    }

    [Fact]
    public async Task ApprovalGate_ContextIncludesSpendAndRoi()
    {
        ApprovalContext? capturedContext = null;
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()))
            .Callback<ApprovalContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync((ApprovalContext ctx, CancellationToken _) => new ApprovalRequest(
                RequestId: "req-123",
                Context: ctx,
                CreatedAt: DateTimeOffset.UtcNow,
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)));

        gate.Setup(g => g.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalResult("req-123", ApprovalDecision.Pending, null, null));

        PromoPlanningAgent agent = CreateAgent(gate.Object);

        await agent.CheckApprovalAsync(
            spend: 750_000, roi: 3.5, userId: "analyst-1", description: "Premium launch");

        capturedContext.Should().NotBeNull();
        capturedContext.AgentId.Should().Be("promo-planning");
        capturedContext.UserId.Should().Be("analyst-1");
        capturedContext.Action.Should().Be("Premium launch");
        capturedContext.Impact.Should().Contain("$750,000");
        capturedContext.Impact.Should().Contain("3.5");
        capturedContext.Urgency.Should().Be("High", "spend > 500K sets urgency to High");
    }

    #endregion

    #region EstimateROI — approval flag consistency

    [Fact]
    public void EstimateROI_HighSpend_SetsRequiresApprovalTrue()
    {
        JsonElement result = Parse(_db.EstimateROI(
            brand: "Sierra Gold Tequila",
            region: "Northeast",
            promoType: "Discount",
            spend: 600_000,
            durationWeeks: 4));

        result.TryGetProperty("error", out _).Should().BeFalse(
            "should return valid ROI estimate for known brand/region");
        result.GetProperty("requires_approval").GetBoolean().Should().BeTrue(
            "spend of $600K exceeds the $500K threshold");
    }

    [Fact]
    public void EstimateROI_NormalSpend_SetsRequiresApprovalFalse()
    {
        JsonElement result = Parse(_db.EstimateROI(
            brand: "Sierra Gold Tequila",
            region: "Northeast",
            promoType: "Discount",
            spend: 50_000,
            durationWeeks: 4));

        result.TryGetProperty("error", out _).Should().BeFalse(
            "should return valid ROI estimate for known brand/region");
        result.GetProperty("requires_approval").GetBoolean().Should().BeFalse(
            "spend of $50K is well below the $500K threshold");
    }

    #endregion
}
