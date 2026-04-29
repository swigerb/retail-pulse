using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Azure.AI.Agents.Persistent;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Tools;

/// <summary>
/// Foundry Shipment Analysis Agent — a real Azure AI Foundry-hosted agent
/// that specializes in Three-Tier Distribution analysis. Uses the
/// <see cref="IAgentProvider{TAgent}"/> pattern (MAF 1.0 best practice)
/// to resolve the agent identity at runtime without leaking asst_* IDs
/// into business logic.
/// </summary>
public class FoundryShipmentAgent
{
    /// <summary>
    /// Maximum time to wait for the Foundry agent run to finish before
    /// abandoning the call. Polling does not have a built-in timeout, so
    /// this guards against indefinite hangs when the agent gets stuck.
    /// </summary>
    private static readonly TimeSpan MaxRunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Polling interval while waiting for the Foundry run to complete.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAgentProvider<IDistributionAnalysisAgent> _provider;
    private readonly ShipmentStatsTool _shipmentTool;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly ILogger<FoundryShipmentAgent> _logger;

    public FoundryShipmentAgent(
        IAgentProvider<IDistributionAnalysisAgent> provider,
        ShipmentStatsTool shipmentTool,
        IHubContext<TelemetryHub> hubContext,
        ILogger<FoundryShipmentAgent> logger)
    {
        _provider = provider;
        _shipmentTool = shipmentTool;
        _hubContext = hubContext;
        _logger = logger;
    }

    [Description("Delegate shipment and Three-Tier distribution analysis to the Foundry Shipment Specialist agent. This agent analyzes pipeline dynamics, detects anomalies like pipeline clogs (shipments up but sell-through down), and provides actionable recommendations. Use this when the user asks about shipments, pipeline health, sell-in vs sell-through, or Three-Tier distribution issues.")]
    public async Task<string> AnalyzeShipments(
        [Description("The brand to analyze, e.g. 'brand name'")] string brand,
        [Description("The region to analyze, e.g. 'Northeast', 'West Coast', 'National'")] string region,
        [Description("The time period, e.g. 'YTD', 'Q1'")] string period = "YTD",
        CancellationToken cancellationToken = default)
    {
        // Bound the entire operation by MaxRunTimeout so a stuck Foundry run
        // can never hang an HTTP request indefinitely.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(MaxRunTimeout);
        var ct = timeoutCts.Token;

        var collector = new TelemetryCollector(_hubContext);
        var agentInfo = await _provider.GetAgentInfoAsync(ct);
        var agentsClient = _provider.GetAgentsClient();

        using var delegationActivity = AgentTelemetry.Source.StartActivity("agent.delegation", ActivityKind.Client);
        delegationActivity?.SetTag("agent.source", "Retail Pulse Agent");
        delegationActivity?.SetTag("agent.target", "Foundry Shipment Specialist");
        delegationActivity?.SetTag("agent.target_name", agentInfo.FriendlyName);
        delegationActivity?.SetTag("agent.target_id", agentInfo.Id);
        delegationActivity?.SetTag("agent.brand", brand);
        delegationActivity?.SetTag("agent.region", region);

        var sw = Stopwatch.StartNew();

        await collector.RecordSpanAsync(
            "Foundry Shipment Specialist", "agent_delegation",
            $"Delegating shipment analysis for {brand} in {region} to Foundry agent ({agentInfo.FriendlyName})",
            sw.ElapsedMilliseconds);

        // Step 1: Fetch shipment data from MCP
        var stepStart = sw.ElapsedMilliseconds;
        using var fetchActivity = AgentTelemetry.Source.StartActivity("tool.GetShipmentStats", ActivityKind.Client);
        fetchActivity?.SetTag("tool.name", "GetShipmentStats");
        var shipmentData = await _shipmentTool.GetShipmentStats(brand, region, period, ct);
        fetchActivity?.SetTag("tool.result_length", shipmentData.Length);

        await collector.RecordSpanAsync(
            "GetShipmentStats", "tool_call",
            $"Fetched shipment data: {shipmentData[..Math.Min(200, shipmentData.Length)]}",
            sw.ElapsedMilliseconds - stepStart);

        // Step 2: Send to real Foundry agent for analysis
        var foundryPrompt = $"""
            Analyze the following shipment data for {brand} in {region} ({period}):

            {shipmentData}

            Provide a Three-Tier Distribution analysis with:
            1. Pipeline health assessment
            2. Anomaly identification (pipeline clog, supply constraint, etc.)
            3. Specific recommendations for the brand team
            """;

        using var foundryActivity = AgentTelemetry.Source.StartActivity("agent.foundry_call", ActivityKind.Client);
        foundryActivity?.SetTag("agent.name", agentInfo.FriendlyName);
        foundryActivity?.SetTag("agent.id", agentInfo.Id);
        foundryActivity?.SetTag("agent.runtime", agentInfo.Runtime);

        stepStart = sw.ElapsedMilliseconds;

        // Create a thread, post the message, run the agent, poll for completion,
        // and ALWAYS clean up the thread (try/finally) even if the run fails or
        // the request is cancelled mid-flight.
        var thread = await agentsClient.Threads.CreateThreadAsync(cancellationToken: ct);
        var threadId = thread.Value.Id;

        string analysis;
        RunStatus finalStatus;
        try
        {
            await agentsClient.Messages.CreateMessageAsync(
                threadId,
                MessageRole.User,
                foundryPrompt,
                cancellationToken: ct);

            var run = await agentsClient.Runs.CreateRunAsync(
                threadId,
                agentInfo.Id,
                cancellationToken: ct);

            // Poll until the run completes, the timeout fires, or the caller cancels.
            var runResult = run.Value;
            while (runResult.Status == RunStatus.Queued || runResult.Status == RunStatus.InProgress)
            {
                await Task.Delay(PollInterval, ct);
                runResult = (await agentsClient.Runs.GetRunAsync(threadId, runResult.Id, cancellationToken: ct)).Value;
            }

            finalStatus = runResult.Status;
            var foundryCallDurationMs = sw.ElapsedMilliseconds - stepStart;
            foundryActivity?.SetTag("agent.duration_ms", foundryCallDurationMs);
            foundryActivity?.SetTag("agent.run_status", runResult.Status.ToString());

            await collector.RecordSpanAsync(
                "Foundry Shipment Specialist", "agent_call",
                $"Calling Foundry-hosted agent ({agentInfo.FriendlyName}) for {brand}/{region} analysis",
                foundryCallDurationMs);

            analysis = "Foundry agent could not generate analysis.";
            if (runResult.Status == RunStatus.Completed)
            {
                var messages = agentsClient.Messages.GetMessagesAsync(
                    threadId: threadId, order: ListSortOrder.Descending, cancellationToken: ct);

                await foreach (var msg in messages.WithCancellation(ct))
                {
                    if (msg.Role == MessageRole.Agent)
                    {
                        foreach (var content in msg.ContentItems)
                        {
                            if (content is MessageTextContent textContent)
                            {
                                analysis = textContent.Text;
                                break;
                            }
                        }
                        break;
                    }
                }
            }
            else
            {
                _logger.LogWarning(
                    "Foundry agent run did not complete. Status: {Status}, Error: {Error}",
                    runResult.Status, runResult.LastError?.Message);
                analysis = $"Foundry agent run ended with status: {runResult.Status}. {runResult.LastError?.Message}";
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Foundry agent run timed out after {Timeout} for {Brand}/{Region}",
                MaxRunTimeout, brand, region);
            finalStatus = RunStatus.Expired;
            analysis = $"Foundry agent timed out after {MaxRunTimeout.TotalSeconds:N0}s. Try again or fall back to local analysis.";
        }
        finally
        {
            // Always delete the thread — leaking threads on every call would
            // pile up quotas in the Foundry project. Use a fresh CT so cleanup
            // still runs even if the original token was cancelled.
            try
            {
                await agentsClient.Threads.DeleteThreadAsync(threadId, cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete Foundry thread {ThreadId}", threadId);
            }
        }

        var responseDurationMs = 0L; // post-processing already accounted for above
        sw.Stop();

        await collector.RecordSpanAsync(
            "Foundry Shipment Specialist", "agent_response",
            $"Foundry analysis complete: {analysis[..Math.Min(200, analysis.Length)]}",
            responseDurationMs);

        _logger.LogInformation(
            "Foundry Shipment Specialist ({AgentName}) analyzed {Brand}/{Region} in {DurationMs}ms (status: {Status})",
            agentInfo.FriendlyName, brand, region, sw.ElapsedMilliseconds, finalStatus);

        // Return structured result combining raw data + agent analysis
        var result = new
        {
            source = $"Foundry Shipment Specialist ({agentInfo.Runtime})",
            agent_name = agentInfo.FriendlyName,
            agent_id = agentInfo.Id,
            brand,
            region,
            period,
            raw_shipment_data = JsonSerializer.Deserialize<JsonElement>(shipmentData),
            agent_analysis = analysis
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }
}
