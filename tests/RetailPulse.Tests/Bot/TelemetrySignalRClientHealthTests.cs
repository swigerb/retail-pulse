using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Contracts;
using RetailPulse.TeamsBot.Services;

namespace RetailPulse.Tests.Bot;

/// <summary>
/// Tests for TelemetrySignalRClient backoff reconnection and health mode properties.
/// </summary>
public class TelemetrySignalRClientHealthTests
{
    [Fact]
    public async Task IsConnected_DefaultsFalse_BeforeConnect()
    {
        (TelemetrySignalRClient? client, HubConnection _) = CreateClient("degraded");
        try
        {
            client.IsConnected.Should().BeFalse();
        }
        finally { await client.DisposeAsync(); }
    }

    [Fact]
    public async Task IsDegraded_TrueWhenDisconnected_InDegradedMode()
    {
        (TelemetrySignalRClient? client, HubConnection _) = CreateClient("degraded");
        try
        {
            client.IsDegraded.Should().BeTrue();
        }
        finally { await client.DisposeAsync(); }
    }

    [Fact]
    public async Task IsDegraded_FalseWhenDisconnected_InFailFastMode()
    {
        (TelemetrySignalRClient? client, HubConnection _) = CreateClient("fail-fast");
        try
        {
            client.IsDegraded.Should().BeFalse();
        }
        finally { await client.DisposeAsync(); }
    }

    [Fact]
    public Task BackoffConstants_AreReasonable()
    {
        TelemetrySignalRClient.InitialReconnectDelay.Should().Be(TimeSpan.FromSeconds(1));
        TelemetrySignalRClient.MaxReconnectDelay.Should().Be(TimeSpan.FromSeconds(30));
        TelemetrySignalRClient.ReconnectBackoffMultiplier.Should().Be(2.0);
        TelemetrySignalRClient.MaxReconnectAttempts.Should().Be(10);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetSpans_ReturnsEmptyList_WhenNoSpans()
    {
        (TelemetrySignalRClient? client, HubConnection _) = CreateClient("degraded");
        try
        {
            List<AgentSpan> spans = client.GetSpans("nonexistent-session");
            spans.Should().BeEmpty();
        }
        finally { await client.DisposeAsync(); }
    }

    [Fact]
    public async Task StartCollecting_DoesNotThrow_WhenDisconnected()
    {
        (TelemetrySignalRClient? client, HubConnection _) = CreateClient("degraded");
        try
        {
            Func<Task> act = () => client.StartCollectingAsync("session-1");
            await act.Should().NotThrowAsync();
        }
        finally { await client.DisposeAsync(); }
    }

    [Fact]
    public async Task ConnectAsync_InDegradedMode_DoesNotThrow_OnCancellation()
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:1/hubs/nonexistent")
            .Build();

        ILogger<TelemetrySignalRClient> logger = Mock.Of<ILogger<TelemetrySignalRClient>>();
        var client = new TelemetrySignalRClient(connection, logger, "degraded");

        // Very short timeout so the backoff loop hits cancellation quickly
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // In degraded mode, ConnectAsync should not throw even when cancelled
        Func<Task> act = () => client.ConnectAsync(cts.Token);
        await act.Should().NotThrowAsync();

        client.IsConnected.Should().BeFalse();
        client.IsDegraded.Should().BeTrue();

        await client.DisposeAsync();
    }

    private static (TelemetrySignalRClient client, HubConnection connection) CreateClient(string healthMode)
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:1/hubs/test")
            .Build();

        ILogger<TelemetrySignalRClient> logger = Mock.Of<ILogger<TelemetrySignalRClient>>();
        var client = new TelemetrySignalRClient(connection, logger, healthMode);

        return (client, connection);
    }
}
