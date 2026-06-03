using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Tracing;

/// <summary>
/// Guards the span.type contract end-to-end: production TraceSpan creation sites
/// must stamp a span.type tag, and the SignalR push service must project that
/// tag into the frontend-facing span payload.
/// </summary>
public class TraceSpanTypeContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Theory]
    [InlineData("src\\RetailPulse.Api\\Endpoints\\ChatEndpoints.cs", "OperationName:\\s*\"router\\.classify\"", "routing")]
    [InlineData("src\\RetailPulse.Api\\Endpoints\\ChatEndpoints.cs", "OperationName:\\s*\"router\\.select_agent\"", "routing")]
    [InlineData("src\\RetailPulse.Api\\Endpoints\\ChatEndpoints.cs", "OperationName:\\s*\"memory\\.recall\"", "memory")]
    [InlineData("src\\RetailPulse.Api\\Endpoints\\ChatEndpoints.cs", "OperationName:\\s*\\$\"agent\\.\\{specialist\\.Key\\}\\.process\"", "agent")]
    [InlineData("src\\RetailPulse.Api\\Endpoints\\ChatEndpoints.cs", "OperationName:\\s*\\$\"tool\\.\\{span\\.Name\\}\"", "tool")]
    [InlineData("src\\RetailPulse.Api\\Memory\\MemoryExtractionBackgroundService.cs", "OperationName:\\s*\"memory\\.store\"", "memory")]
    public void ProductionTraceSpanCreation_SetsExpectedSpanTypeTag(string relativePath, string operationPattern, string expectedType)
    {
        string fullPath = Path.Combine(RepoRoot, relativePath);
        File.Exists(fullPath).Should().BeTrue($"expected source file '{relativePath}' to exist");

        string source = File.ReadAllText(fullPath);
        var pattern = new Regex($@"{operationPattern}[\s\S]*?\[""span\.type""\]\s*=\s*""{Regex.Escape(expectedType)}""", RegexOptions.Multiline);

        pattern.IsMatch(source).Should().BeTrue(
            $"{relativePath} should tag {expectedType} spans with Tags[\"span.type\"] so telemetry can classify them correctly");
    }

    [Fact]
    public async Task TelemetryPushBackgroundService_UsesSpanTypeTagInSpanPayload()
    {
        var channel = new TelemetryPushChannel();
        object? payload = null;

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync("span_completed", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => payload = args.Single())
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(proxy.Object);

        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var service = new TelemetryPushBackgroundService(channel, hubContext.Object, NullLogger<TelemetryPushBackgroundService>.Instance);

        channel.TryWrite(new TelemetryPushItem(
            "span_completed",
            Span: new TraceSpan(
                SpanId: "span-1",
                TraceId: "trace-1",
                ParentSpanId: null,
                OperationName: "tool.GetDemand",
                StartTime: DateTimeOffset.UtcNow,
                EndTime: DateTimeOffset.UtcNow.AddMilliseconds(25),
                DurationMs: 25,
                Tags: new Dictionary<string, string> { ["span.type"] = "tool" }))).Should().BeTrue();

        channel.Complete();
        await RunServiceAsync(service, CancellationToken.None);

        payload.Should().NotBeNull();
        JsonSerializer.Serialize(payload).Should().Contain("\"type\":\"tool\"");
    }

    [Fact]
    public async Task TelemetryPushBackgroundService_FallsBackToGenericWhenSpanTypeMissing()
    {
        var channel = new TelemetryPushChannel();
        object? payload = null;

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync("span_completed", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => payload = args.Single())
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(proxy.Object);

        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var service = new TelemetryPushBackgroundService(channel, hubContext.Object, NullLogger<TelemetryPushBackgroundService>.Instance);

        channel.TryWrite(new TelemetryPushItem(
            "span_completed",
            Span: new TraceSpan(
                SpanId: "span-2",
                TraceId: "trace-2",
                ParentSpanId: null,
                OperationName: "custom.op",
                StartTime: DateTimeOffset.UtcNow,
                EndTime: DateTimeOffset.UtcNow.AddMilliseconds(10),
                DurationMs: 10))).Should().BeTrue();

        channel.Complete();
        await RunServiceAsync(service, CancellationToken.None);

        payload.Should().NotBeNull();
        JsonSerializer.Serialize(payload).Should().Contain("\"type\":\"generic\"");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from test base directory.");
    }

    private static async Task RunServiceAsync(TelemetryPushBackgroundService service, CancellationToken cancellationToken)
    {
        var method = typeof(TelemetryPushBackgroundService).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var task = method.Invoke(service, [cancellationToken]).Should().BeAssignableTo<Task>().Subject;
        await task;
    }
}
