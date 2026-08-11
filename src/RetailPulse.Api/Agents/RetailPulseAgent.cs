using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Legacy agent class retained for backward compatibility with tests and direct usage.
/// Delegates all chat logic to <see cref="GeneralAgent"/> via <see cref="IAgentExecutionPipeline"/>.
/// </summary>
public class RetailPulseAgent
{
    private readonly GeneralAgent _generalAgent;
    private readonly AgentExecutionPipeline _pipeline;
    private readonly AgentDefinition _agentDef;

    public RetailPulseAgent(
        IChatClient chatClient,
        AgentDefinition agentDef,
        IHubContext<TelemetryHub> hubContext,
        IEnumerable<AITool> tools,
        ILogger<RetailPulseAgent> logger,
        IConfiguration configuration,
        TenantConfiguration tenant,
        IHubContext<StreamingHub>? streamingHubContext = null,
        StreamingProgressFeature? streamingFeature = null)
    {
        _agentDef = agentDef;
        ILogger<AgentExecutionPipeline> pipelineLogger = LoggerFactory.Create(b => { }).CreateLogger<AgentExecutionPipeline>();
        _pipeline = new AgentExecutionPipeline(
            chatClient,
            hubContext,
            streamingHubContext,
            streamingFeature,
            configuration,
            pipelineLogger,
            metrics: null,
            anonymousChatPolicy: NoOpAnonymousChatPolicy.Instance,
            tenant: tenant);
        _generalAgent = new GeneralAgent(_pipeline, agentDef, tools);
    }

    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        => _generalAgent.HandleAsync(request, ct);

    internal TokenUsage BuildTokenUsage(int inputTokens, int outputTokens, int totalTokens)
        => _pipeline.BuildTokenUsage(inputTokens, outputTokens, totalTokens, _agentDef.Model);

    public static PromptConfiguration LoadPrompts(string yamlPath)
    {
        string yaml = File.ReadAllText(yamlPath);
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<PromptConfiguration>(yaml);
    }
}
