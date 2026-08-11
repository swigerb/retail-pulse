using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using ProdChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Composition-root regression coverage for issue #76 Group A: the live production
/// sweep found the LLM emitting ChartSpecs on PROSE prompts (prompts that contain
/// trigger nouns like "Compare X vs Y" or "Show me ... trends" but NO explicit chart
/// noun). The <see cref="Api.Charts.ChartRequestDetector"/> unit test
/// classifies these correctly — but the pipeline's chart-fulfillment invariant only
/// enforced "chart must exist when requested" and never the inverse. Every
/// specialist has CreateChart wired into its toolkit for the legitimate chart
/// prompts, so nothing stopped the model from calling it on a prose prompt and
/// shipping an unrequested visualization.
///
/// This is the same class of test/production divergence that shipped as #74's
/// Publix failure #2 (roster-coverage code correct, wiring silently skipped it).
/// A detector unit test alone CANNOT catch it. This test exercises the full
/// pipeline through the same DI graph the app uses, so a future regression that
/// re-introduces the drop-on-prose gap in the pipeline is caught at build time.
/// </summary>
public sealed class ChartOnProsePromptCompositionTests
{
    private static readonly string[] TenantBrands =
    [
        "FreshMart", "Harvest Table", "ClearDesk", "Urban Living", "Foundry Home", "Coastline Tacos",
    ];

    // The five prose prompts flagged as Group A in the #76 sweep failure taxonomy.
    // They contain trigger nouns but NO explicit chart noun — so the detector
    // classifies them as prose and the pipeline must not surface a chart even if
    // the LLM emits one via CreateChart.
    public static IEnumerable<object[]> ProsePromptsFromSweep =>
    [
        ["Compare depletion trends across all regions for this quarter"],
        ["Compare Harvest Table vs FreshMart sell-through rates by region"],
        ["Compare ClearDesk Technology vs Paper Products sell-through by region"],
        ["Show me Urban Living depletion trends across all regions this quarter"],
        ["Compare Foundry Home vs Urban Living performance in the West Coast"],
    ];

    [Theory]
    [MemberData(nameof(ProsePromptsFromSweep))]
    public async Task Pipeline_ProsePrompt_DoesNotSurfaceModelEmittedChart(string prosePrompt)
    {
        // Given a full DI graph like production, but with a fake chat client that
        // simulates the exact production defect: the LLM calls CreateChart and
        // emits a ChartSpec in the response tool results even though the prompt
        // is prose.
        var fakeChat = new ChartEmittingChatClient();
        ServiceProvider provider = BuildProvider(fakeChat);
        using IServiceScope scope = provider.CreateScope();
        IAgentExecutionPipeline pipeline = scope.ServiceProvider.GetRequiredService<IAgentExecutionPipeline>();

        var ctx = new AgentExecutionContext
        {
            AgentName = "General",
            SystemPrompt = "test",
            Temperature = 0.0f,
            ModelName = "test-model",
            Request = new ChatRequest(prosePrompt) { SessionId = "sess" },
            Tools = [],
        };

        ProdChatResponse response = await pipeline.ExecuteAsync(ctx);

        response.Charts.Should().BeNull(
            "the pipeline must strip any chart the model emits on a prose prompt — the " +
            "detector classifies these as prose and the composition-root chart-fulfillment " +
            "invariant is the ONLY guard against LLM non-determinism (issue #76 Group A)");
    }

    [Fact]
    public async Task Pipeline_ProsePrompt_IsDeterministicAcrossRepeatedInvocations()
    {
        // Stability regression: production sweep observed the same prompt sometimes
        // charting and sometimes not across reruns. The composition-level decision
        // must be deterministic for a given prompt.
        var fakeChat = new ChartEmittingChatClient();
        ServiceProvider provider = BuildProvider(fakeChat);

        var chartCounts = new HashSet<int>();
        for (int i = 0; i < 10; i++)
        {
            using IServiceScope scope = provider.CreateScope();
            IAgentExecutionPipeline pipeline = scope.ServiceProvider.GetRequiredService<IAgentExecutionPipeline>();
            var ctx = new AgentExecutionContext
            {
                AgentName = "General",
                SystemPrompt = "test",
                Temperature = 0.0f,
                ModelName = "test-model",
                Request = new ChatRequest("Compare Harvest Table vs FreshMart sell-through rates by region") { SessionId = $"s{i}" },
                Tools = [],
            };
            ProdChatResponse r = await pipeline.ExecuteAsync(ctx);
            chartCounts.Add(r.Charts?.Count ?? 0);
        }

        chartCounts.Should().ContainSingle().Which.Should().Be(0,
            "identical prose prompts must yield identical (zero-chart) decisions on every rerun (issue #76 stability)");
    }

    // ── DI graph (mirrors TenantRosterPipelineCompositionTests) ─────────────

    private static ServiceProvider BuildProvider(IChatClient chatClient)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddHttpContextAccessor();

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Anonymous:Enabled"] = "false",
            ["Anonymous:SigningKey"] = "chart-on-prose-composition-signing-key-0123456789",
        }).Build();
        services.AddSingleton(config);

        services.AddSingleton(chatClient);

        var tenant = new TenantConfiguration
        {
            Company = "TestCo",
            BrandsList = [.. TenantBrands.Select(b => new BrandConfig { Name = b, Category = "grocery" })],
        };
        services.AddSingleton(tenant);

        services.AddSingleton<Api.Auth.IAnonymousChatPolicy>(Api.Auth.NoOpAnonymousChatPolicy.Instance);

        services.AddAgentRouting(
            promptConfig: new PromptConfiguration
            {
                Agents = new Dictionary<string, AgentDefinition>
                {
                    ["router"] = new AgentDefinition { Name = "router", SystemPrompt = "route" },
                },
            },
            generalAgentDef: new AgentDefinition { Name = "General", SystemPrompt = "gen" },
            foundryEnabled: false,
            toolsFactory: _ => []);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Simulates the exact production failure: the model calls CreateChart and the
    /// tool result carries a serialized ChartSpec, even though the user prompt is
    /// prose. The pipeline's chart-extraction path then produces a ChartSpec in
    /// <see cref="ProdChatResponse.Charts"/> unless the
    /// fulfillment invariant strips it (issue #76 Group A).
    /// </summary>
    private sealed class ChartEmittingChatClient : IChatClient
    {
        private static readonly ChartSpec _bogusChart = new()
        {
            Type = "bar",
            Title = "Model-emitted (unrequested) chart",
            Data =
            [
                new ChartSeries
                {
                    Legend = "Bogus",
                    Values = [new ChartDataPoint { X = "A", Y = 42 }],
                }
            ],
        };

        public Task<MeaiChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            string toolPayload = JsonSerializer.Serialize(new
            {
                status = "success",
                chart = _bogusChart,
                recovered = false,
            });
            var content = new List<AIContent>
            {
                new TextContent("Here is a comparison of the requested metrics."),
                new FunctionResultContent("call-createchart", toolPayload),
            };
            var message = new ChatMessage(ChatRole.Assistant, content);
            return Task.FromResult(new MeaiChatResponse(message));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
