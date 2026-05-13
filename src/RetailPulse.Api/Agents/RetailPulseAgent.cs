using System.ClientModel;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;
using AgentSpan = RetailPulse.Contracts.AgentSpan;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Legacy agent class retained for backward compatibility with tests and direct usage.
/// Delegates all chat logic to <see cref="GeneralAgent"/>.
/// </summary>
public class RetailPulseAgent
{
    private readonly GeneralAgent _generalAgent;

    public RetailPulseAgent(
        IChatClient chatClient,
        AgentDefinition agentDef,
        IHubContext<TelemetryHub> hubContext,
        IEnumerable<AITool> tools,
        ILogger<RetailPulseAgent> logger,
        IConfiguration configuration)
    {
        // Adapt the logger type — GeneralAgent uses its own logger category
        var loggerFactory = LoggerFactory.Create(b => { });
        var generalLogger = loggerFactory.CreateLogger<GeneralAgent>();

        _generalAgent = new GeneralAgent(chatClient, agentDef, hubContext, tools, generalLogger, configuration);
    }

    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        => _generalAgent.HandleAsync(request, ct);

    internal TokenUsage BuildTokenUsage(int inputTokens, int outputTokens, int totalTokens)
        => _generalAgent.BuildTokenUsage(inputTokens, outputTokens, totalTokens);

    public static PromptConfiguration LoadPrompts(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<PromptConfiguration>(yaml);
    }
}
