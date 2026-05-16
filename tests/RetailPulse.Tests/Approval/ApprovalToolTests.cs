using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Tests for ApprovalTool — the AI-callable tool that agents use
/// to request human approval for high-impact actions.
/// </summary>
public class ApprovalToolTests
{
    private readonly Mock<IApprovalGate> _gateMock;
    private readonly ApprovalTool _tool;

    public ApprovalToolTests()
    {
        _gateMock = new Mock<IApprovalGate>();
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        _tool = new ApprovalTool(
            _gateMock.Object,
            hubContext,
            Mock.Of<ILogger<ApprovalTool>>());
    }

    private static IHubContext<TelemetryHub> CreateMockHubContext()
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.All).Returns(proxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }

    private void SetupApprovalFlow(ApprovalDecision decision, string? comment = null)
    {
        string requestId = Guid.NewGuid().ToString("N");

        _gateMock.Setup(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApprovalContext ctx, CancellationToken _) =>
                new ApprovalRequest(requestId, ctx, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)));

        _gateMock.Setup(g => g.WaitForApprovalAsync(requestId, It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalResult(requestId, decision, comment, DateTimeOffset.UtcNow));
    }

    #region Tool Creates Approval Request

    [Fact]
    public async Task Tool_CreatesApprovalRequestWithAgentContext()
    {
        SetupApprovalFlow(ApprovalDecision.Approved);

        string result = await _tool.RequestApproval(
            action: "Delete 500 forecast records",
            impact: "Affects all demand predictions",
            urgency: "high",
            reasoning: "Records are stale",
            agentId: "demand-agent",
            userId: "user-1");

        _gateMock.Verify(g => g.RequestApprovalAsync(
            It.Is<ApprovalContext>(c =>
                c.AgentId == "demand-agent" &&
                c.UserId == "user-1" &&
                c.Action == "Delete 500 forecast records" &&
                c.Urgency == "high"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Tool_ReturnsApprovalResult_AsJson()
    {
        SetupApprovalFlow(ApprovalDecision.Approved, "Go ahead");

        string result = await _tool.RequestApproval(
            "action", "impact", "medium", "reasoning", "agent", "user");

        result.Should().Contain("Approved");
    }

    [Fact]
    public async Task Tool_HandlesTimeout_Gracefully()
    {
        SetupApprovalFlow(ApprovalDecision.TimedOut);

        string result = await _tool.RequestApproval(
            "action", "impact", "medium", "reasoning", "agent", "user");

        result.Should().Contain("TimedOut");
    }

    [Fact]
    public async Task Tool_IncludesUrgencyAndImpact()
    {
        SetupApprovalFlow(ApprovalDecision.Approved);

        await _tool.RequestApproval(
            action: "Rerun all forecasts",
            impact: "2 hour compute cost",
            urgency: "high",
            reasoning: "Data drift detected",
            agentId: "agent-1",
            userId: "user-1");

        _gateMock.Verify(g => g.RequestApprovalAsync(
            It.Is<ApprovalContext>(c =>
                c.Impact == "2 hour compute cost" &&
                c.Urgency == "high"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Rejection Handling

    [Fact]
    public async Task Tool_ReturnsRejection_ToAgent()
    {
        SetupApprovalFlow(ApprovalDecision.Rejected, "Not authorized");

        string result = await _tool.RequestApproval(
            "action", "impact", "medium", "reasoning", "agent", "user");

        result.Should().Contain("Rejected");
    }

    [Fact]
    public async Task Tool_ReturnsModifiedDecision()
    {
        SetupApprovalFlow(ApprovalDecision.Modified, "Approved for 100 records only");

        string result = await _tool.RequestApproval(
            "Bulk update 5000 records", "High", "medium", "Batch job", "agent", "user");

        result.Should().Contain("Modified");
        result.Should().Contain("100 records");
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task Tool_GateThrows_ReturnsErrorJson()
    {
        _gateMock.Setup(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Gate unavailable"));

        string result = await _tool.RequestApproval(
            "action", "impact", "medium", "reasoning", "agent", "user");

        result.Should().Contain("Error");
        result.Should().Contain("Gate unavailable");
    }

    [Fact]
    public async Task Tool_CancellationRespected()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _gateMock.Setup(g => g.RequestApprovalAsync(It.IsAny<ApprovalContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        string result = await _tool.RequestApproval(
            "action", "impact", "medium", "reasoning", "agent", "user",
            cancellationToken: cts.Token);

        result.Should().Contain("Cancelled");
    }

    #endregion
}
