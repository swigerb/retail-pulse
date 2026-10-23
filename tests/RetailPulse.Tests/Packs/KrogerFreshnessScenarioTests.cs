using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Models;
using RetailPulse.Api.Packs;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.Fixtures;
using RetailPulse.Tests.TestInfrastructure;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Packs;

public sealed class KrogerFreshnessScenarioTests : IDisposable
{
    private readonly string _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_kroger_freshness_test");
    private readonly LoadedPack _pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load("kroger-retail");
    private readonly RetailPulseDb _db;

    public KrogerFreshnessScenarioTests()
    {
        _db = new RetailPulseDb(
            new PackTenantProvider(_pack),
            _pack.Seed,
            _dbPath,
            Path.Combine(_pack.RootPath, "pack.yaml"),
            Path.Combine(_pack.RootPath, "seed"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    [Theory]
    [InlineData("Fresh Produce", "Heartland", "Produce", "YTD", 2,
        "Which Fresh Produce items in the Heartland have fewer than two days of supply, and how is sell-through trending?")]
    [InlineData("Dairy & Eggs", "Desert Basin", "Dairy", "Q1", 7,
        "Which Dairy & Eggs items in the Desert Basin, category Dairy, have fewer than seven days of supply, and how is sell-through trending for Q1?")]
    public async Task ScriptedToolCalls_PreserveFiltersThroughConfiguredAgentPipeline(
        string brand, string region, string category, string period, double threshold, string userPrompt)
    {
        const string expectedReply = "Both seeded tool results were returned.";
        var handler = new KrogerApiHandler(_db);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://retailpulse.test") };

#pragma warning disable CS0618
        var historicalDemandTool = new HistoricalDemandTool(httpClient);
#pragma warning restore CS0618
        var inventoryTool = new InventoryLevelsTool(httpClient);
        var depletionStatsTool = new DepletionStatsTool(httpClient);
        AgentDefinition definition = _pack.Agents.Agents["freshness-substitutions"];

        definition.Tools.Should().Contain(["GetHistoricalDemand", "GetInventoryLevels", "GetDepletionStats"]);
        definition.SystemPrompt.Should().Contain("canonical brand name");
        definition.SystemPrompt.Should().Contain("Berries, Leafy Greens");
        definition.SystemPrompt.Should().Contain("GetHistoricalDemand returns weekly units and volume, not");
        _pack.StartingTasks.SelectMany(category => category.Tasks)
            .Should().ContainSingle(task => task.Name == "Produce days of supply")
            .Which.Prompt.Should().Be(
                "Which Fresh Produce items in the Heartland have fewer than two days of supply, and how is sell-through trending?");

        AgentToolRegistry toolRegistry = new AgentToolRegistry()
            .Register("GetHistoricalDemand", _ => AIFunctionFactory.Create(historicalDemandTool.GetHistoricalDemand))
            .Register("GetInventoryLevels", _ => AIFunctionFactory.Create(inventoryTool.GetInventoryLevels))
            .Register("GetDepletionStats", _ => AIFunctionFactory.Create(depletionStatsTool.GetDepletionStats));
        foreach (string toolName in definition.Tools.Except(
                     ["GetHistoricalDemand", "GetInventoryLevels", "GetDepletionStats"],
                     StringComparer.OrdinalIgnoreCase))
        {
            toolRegistry.Register(toolName, _ => AIFunctionFactory.Create(
                () => "Not used by the scripted freshness test.", toolName));
        }

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        IReadOnlyList<AITool> tools = toolRegistry.Resolve(definition.Tools, services);
        IReadOnlyList<string> modelToolResults = [];
        IChatClient model = CreateScriptedToolCallingClient(
            brand, region, category, period, userPrompt, definition.SystemPrompt,
            expectedReply, results => modelToolResults = results);
        using var functionClient = new FunctionInvokingChatClient(model)
        {
            MaximumIterationsPerRequest = 3
        };
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var pipeline = new AgentExecutionPipeline(
            functionClient,
            AgentTestFixtures.CreateMockHubContext(),
            configuration,
            NullLogger<AgentExecutionPipeline>.Instance);
        var agent = new ConfiguredSpecialistAgent(pipeline, definition, tools);

        ChatResponse response = await agent.HandleAsync(new ChatRequest(
            userPrompt,
            SessionId: "kroger-freshness-seeded-regression"));

        response.Reply.Should().Be(expectedReply);
        handler.Requests.Should().HaveCount(2);
        modelToolResults.Should().HaveCount(2, "the specialist must receive both real tool results before synthesizing its reply");

        JsonElement inventory = handler.Results["/api/supply/inventory"];
        inventory.GetProperty("filters_applied").GetProperty("brand").GetString().Should().Be(brand);
        inventory.GetProperty("filters_applied").GetProperty("region").GetString().Should().Be(region);
        inventory.GetProperty("filters_applied").GetProperty("category").GetString().Should().Be(category);
        inventory.GetProperty("total_items").GetInt32().Should().BeGreaterThan(0);
        inventory.GetProperty("items").EnumerateArray().Should().OnlyContain(item =>
            item.GetProperty("brand").GetString() == brand
            && item.GetProperty("region").GetString() == region
            && item.GetProperty("category").GetString() == category);
        inventory.GetProperty("items").EnumerateArray()
            .Should().Contain(item => item.GetProperty("days_of_supply").GetDouble() < threshold,
                "the requested threshold must match at least one seeded item in this fixture");
        modelToolResults.Any(result => ContainsInventoryBelowThreshold(result, threshold)).Should().BeTrue();

        JsonElement depletion = handler.Results["/api/depletion-stats"];
        depletion.GetProperty("brand").GetString().Should().Be(brand);
        depletion.GetProperty("region").GetString().Should().Be(region);
        depletion.GetProperty("period").GetString().Should().Be(period);
        depletion.TryGetProperty("error", out _).Should().BeFalse();
        depletion.GetProperty("metrics").GetProperty("sell_through_yoy").GetString()
            .Should().MatchRegex(@"^[+-]\d+\.\d+%$");
        modelToolResults.Any(ContainsSellThroughMetric).Should().BeTrue();
    }

    [Fact]
    public void VariantNames_DoNotAliasCanonicalBrandData()
    {
        using var inventory = JsonDocument.Parse(JsonSerializer.Serialize(
            _db.GetInventoryLevels("Berries", "Heartland", "Produce", null)));
        using var demand = JsonDocument.Parse(JsonSerializer.Serialize(
            _db.GetHistoricalDemand("Berries", "Heartland", period: "current-quarter")));
        using var depletion = JsonDocument.Parse(JsonSerializer.Serialize(
            _db.GetDepletionStats("Berries", "Heartland", "YTD")));

        inventory.RootElement.GetProperty("total_items").GetInt32().Should().Be(0);
        demand.RootElement.GetProperty("summary").GetProperty("total_volume").GetDouble().Should().Be(0);
        demand.RootElement.GetProperty("summary").GetProperty("weeks_of_data").GetInt32().Should().Be(0);
        depletion.RootElement.GetProperty("error").GetString().Should().Contain("No data found");
        depletion.RootElement.TryGetProperty("metrics", out _).Should().BeFalse();
    }

    // Calls and final prose are scripted: this proves wiring and seed availability, not model adherence.
    private static IChatClient CreateScriptedToolCallingClient(
        string brand,
        string region,
        string category,
        string period,
        string userPrompt,
        string systemPrompt,
        string finalReply,
        Action<IReadOnlyList<string>> onSynthesis)
    {
        int requestCount = 0;
        Mock<IChatClient> client = new();
        client.Setup(chatClient => chatClient.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessage> messages, ChatOptions? _, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref requestCount) == 1)
                {
                    messages.Should().Contain(message => message.Role == ChatRole.User && message.Text == userPrompt);
                    messages.Should().Contain(message => message.Role == ChatRole.System && message.Text.Contains(systemPrompt));
                    return new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "inventory-call",
                            "GetInventoryLevels",
                            new Dictionary<string, object?>
                            {
                                ["brand"] = brand,
                                ["region"] = region,
                                ["category"] = category
                            }),
                        new FunctionCallContent(
                            "demand-call",
                            "GetDepletionStats",
                            new Dictionary<string, object?>
                            {
                                ["brand"] = brand,
                                ["region"] = region,
                                ["period"] = period
                            })
                    ]));
                }

                var results = messages
                    .SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Select(result => result.Result switch
                    {
                        string text => text,
                        JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? string.Empty,
                        JsonElement json => json.GetRawText(),
                        _ => JsonSerializer.Serialize(result.Result)
                    })
                    .ToList();
                onSynthesis(results);
                return new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, finalReply));
            });
        return client.Object;
    }

    private static bool ContainsInventoryBelowThreshold(string result, double threshold)
    {
        using var document = JsonDocument.Parse(result);
        return document.RootElement.TryGetProperty("items", out JsonElement items)
            && items.EnumerateArray().Any(item => item.GetProperty("days_of_supply").GetDouble() < threshold);
    }

    private static bool ContainsSellThroughMetric(string result)
    {
        using var document = JsonDocument.Parse(result);
        return document.RootElement.TryGetProperty("metrics", out JsonElement metrics)
            && metrics.TryGetProperty("sell_through_yoy", out _);
    }

    private sealed class KrogerApiHandler(RetailPulseDb db) : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public ConcurrentDictionary<string, JsonElement> Results { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri
                ?? throw new InvalidOperationException("The test API request did not include a URI.");
            Requests.Enqueue(uri);
            Dictionary<string, StringValues> query =
                QueryHelpers.ParseQuery(uri.Query);

            object result = uri.AbsolutePath switch
            {
                "/api/supply/inventory" => db.GetInventoryLevels(
                    query.GetValueOrDefault("brand").ToString(),
                    query.GetValueOrDefault("region").ToString(),
                    query.GetValueOrDefault("category").ToString(),
                    query.GetValueOrDefault("status").ToString()),
                "/api/depletion-stats" => db.GetDepletionStats(
                    query.GetValueOrDefault("brand").ToString(),
                    query.GetValueOrDefault("region").ToString(),
                    query.GetValueOrDefault("period").ToString()),
                _ => throw new InvalidOperationException($"Unexpected API path: {uri.AbsolutePath}")
            };

            string json = JsonSerializer.Serialize(result);
            Results[uri.AbsolutePath] = JsonDocument.Parse(json).RootElement.Clone();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
