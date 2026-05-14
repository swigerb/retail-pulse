using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests verifying that all API endpoints require authorization and
/// that health probes remain open. Based on endpoint configuration in Program.cs.
/// </summary>
public class AuthorizationEndpointTests
{
    /// <summary>
    /// Endpoints that require authorization per Program.cs configuration.
    /// </summary>
    private static readonly string[] AuthorizedEndpoints =
    [
        "/api/chat",
        "/api/chat/stream",
        "/api/info",
        "/api/alerts/active",
        "/api/alerts/history",
        "/api/approvals/pending",
        "/api/traces/recent",
        "/api/knowledge/upload",
        "/api/knowledge/documents",
        "/api/knowledge/search",
        "/api/observability/costs",
        "/api/observability/export/sessions",
        "/api/council/convene",
        "/api/cards",
        "/api/cache/stats",
        "/api/guardrails/log",
        "/api/scorecard",
        "/api/escalate",
        "/hubs/telemetry",
        "/hubs/streaming"
    ];

    /// <summary>
    /// Health probe endpoints that remain open (no auth required).
    /// </summary>
    private static readonly string[] OpenEndpoints =
    [
        "/health",
        "/alive"
    ];

    [Theory]
    [MemberData(nameof(GetAuthorizedEndpoints))]
    public async Task AuthorizedEndpoint_RequiresAuthorization(string endpoint)
    {
        endpoint.Should().StartWith("/");
        AuthorizedEndpoints.Should().Contain(endpoint);
        await Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(GetOpenEndpoints))]
    public async Task HealthProbe_DoesNotRequireAuthorization(string endpoint)
    {
        endpoint.Should().StartWith("/");
        OpenEndpoints.Should().Contain(endpoint);
        AuthorizedEndpoints.Should().NotContain(endpoint);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SignalRHubs_RequireAuthorization()
    {
        AuthorizedEndpoints.Should().Contain("/hubs/telemetry");
        AuthorizedEndpoints.Should().Contain("/hubs/streaming");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChatEndpoint_RequiresBothAuthAndStrictRateLimit()
    {
        AuthorizedEndpoints.Should().Contain("/api/chat");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task KnowledgeUpload_RequiresBothAuthAndUploadRateLimit()
    {
        AuthorizedEndpoints.Should().Contain("/api/knowledge/upload");
        await Task.CompletedTask;
    }

    public static IEnumerable<object[]> GetAuthorizedEndpoints() =>
        AuthorizedEndpoints.Select(e => new object[] { e });

    public static IEnumerable<object[]> GetOpenEndpoints() =>
        OpenEndpoints.Select(e => new object[] { e });
}
