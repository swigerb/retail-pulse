using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Models;
using RetailPulse.Api.Packs;
using RetailPulse.Api.Tools;
using RetailPulse.McpServer.Data;
using RetailPulse.McpServer.Tools;
using RetailPulse.Tests.Fixtures;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Packs;

public sealed class LowesInventoryScenarioTests : IDisposable
{
    private readonly string _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_lowes_inventory");
    private readonly LoadedPack _pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load("lowes-retail");
    private readonly RetailPulseDb _db;

    public LowesInventoryScenarioTests()
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

    [Fact]
    public void PlywoodInventory_RequiresCanonicalBrandCategoryAndVariant()
    {
        Parse(_db.GetInventoryLevels("Building Materials", null, "Building Materials", null, "Plywood"))
            .GetProperty("total_items").GetInt32().Should().Be(0);

        JsonElement canonical = Parse(_db.GetInventoryLevels(
            "Building Materials", null, "Lumber & Building", null, "Plywood"));
        canonical.GetProperty("total_items").GetInt32().Should().Be(4);
        canonical.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("region").GetString())
            .Should().BeEquivalentTo(_pack.Tenant.Regions);
    }

    [Theory]
    [InlineData("Building Materials", "Lumber & Building", "Plywood", null, null,
        "Show days of supply for plywood across all regions")]
    [InlineData("Building Materials", "Lumber & Building", "Plywood", "Coastal South", "critical",
        "Show critical Plywood inventory for Building Materials in the Coastal South")]
    public async Task ScriptedConfiguredAgent_UsesSeededInventoryAndPreservesRequestedControls(
        string brand, string category, string variant, string? region, string? status, string userPrompt)
    {
        JsonElement result = await InvokeConfiguredAgent(brand, category, variant, region, status, userPrompt);
        JsonElement[] items = [.. result.GetProperty("items").EnumerateArray()];

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(item =>
            item.GetProperty("brand").GetString() == brand
            && item.GetProperty("category").GetString() == category
            && item.GetProperty("variant").GetString() == variant
            && (region == null || item.GetProperty("region").GetString() == region)
            && (status == null || item.GetProperty("status").GetString() == status));
        items.Select(item => item.GetProperty("region").GetString())
            .Should().BeEquivalentTo(region is null ? _pack.Tenant.Regions : [region]);
        result.GetProperty("total_items").GetInt32().Should().Be(region is null ? 4 : 1);
        items.Should().OnlyContain(item => item.GetProperty("days_of_supply").GetDouble() >= 0);
        if (region is null)
        {
            items.ToDictionary(
                    item => item.GetProperty("region").GetString()!,
                    item => item.GetProperty("days_of_supply").GetDouble())
                .Should().BeEquivalentTo(new Dictionary<string, double>
                {
                    ["Coastal South"] = 6.8,
                    ["Atlantic Seaboard"] = 32.7,
                    ["Central Plains"] = 38.2,
                    ["Mountain West"] = 31.2
                });
        }

        JsonElement filters = result.GetProperty("filters_applied");
        filters.GetProperty("brand").GetString().Should().Be(brand);
        filters.GetProperty("category").GetString().Should().Be(category);
        filters.GetProperty("variant").GetString().Should().Be(variant);
        filters.GetProperty("region").GetString().Should().Be(region);
        filters.GetProperty("status").GetString().Should().Be(status);
    }

    // Tool selection and prose are scripted; this verifies real configured-agent tool wiring and seed data.
    private async Task<JsonElement> InvokeConfiguredAgent(
        string brand, string category, string variant, string? region, string? status, string userPrompt)
    {
        var handler = new InventoryHandler(_db);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://retailpulse.test") };
        var inventoryTool = new InventoryLevelsTool(httpClient);
        AgentDefinition definition = _pack.Agents.Agents["fulfillment-continuity"];
        AgentToolRegistry registry = new AgentToolRegistry()
            .Register("GetInventoryLevels", _ => AIFunctionFactory.Create(inventoryTool.GetInventoryLevels));
        foreach (string name in definition.Tools.Where(name => name != "GetInventoryLevels"))
            registry.Register(name, _ => AIFunctionFactory.Create(UnexpectedTool, name));

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        int modelCalls = 0;
        JsonElement? received = null;
        Mock<IChatClient> model = new();
        model.Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            {
                if (++modelCalls == 1)
                {
                    messages.Should().Contain(message =>
                        message.Role == ChatRole.User && message.Text == userPrompt);
                    messages.Should().Contain(message =>
                        message.Role == ChatRole.System && message.Text.Contains(definition.SystemPrompt));
                    AIFunction function = options!.Tools!.OfType<AIFunction>()
                        .Single(tool => tool.Name == "GetInventoryLevels");
                    function.JsonSchema.GetProperty("properties").TryGetProperty("variant", out _)
                        .Should().BeTrue();

                    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [
                        new FunctionCallContent("inventory", "GetInventoryLevels", new Dictionary<string, object?>
                        {
                            ["brand"] = brand,
                            ["category"] = category,
                            ["variant"] = variant,
                            ["region"] = region,
                            ["status"] = status
                        })
                    ]));
                }

                FunctionResultContent toolResult = messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Should().ContainSingle().Subject;
                string json = toolResult.Result switch
                {
                    string text => text,
                    JsonElement value when value.ValueKind == JsonValueKind.String => value.GetString()!,
                    _ => JsonSerializer.Serialize(toolResult.Result)
                };
                received = JsonDocument.Parse(json).RootElement.Clone();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Seeded inventory received."));
            });

        using var functionClient = new FunctionInvokingChatClient(model.Object)
        {
            MaximumIterationsPerRequest = 3
        };
        var pipeline = new AgentExecutionPipeline(
            functionClient,
            AgentTestFixtures.CreateMockHubContext(),
            new ConfigurationBuilder().Build(),
            NullLogger<AgentExecutionPipeline>.Instance);
        var agent = new ConfiguredSpecialistAgent(pipeline, definition, registry.Resolve(definition.Tools, services));

        Contracts.ChatResponse response = await agent.HandleAsync(
            new Contracts.ChatRequest(userPrompt, SessionId: "lowes-inventory-regression"));

        response.Reply.Should().Be("Seeded inventory received.");
        modelCalls.Should().Be(2);
        handler.Requests.Should().ContainSingle("one scoped query should return all matching seeded regions");
        received.Should().NotBeNull("the configured specialist must receive the real seeded tool result");
        return received!.Value;
    }

    private static string UnexpectedTool() => throw new InvalidOperationException("Unexpected tool");

    private static JsonElement Parse(object result) => JsonSerializer.SerializeToElement(result);

    private sealed class InventoryHandler(RetailPulseDb db) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            uri.AbsolutePath.Should().Be("/api/supply/inventory");
            Requests.Add(uri);
            Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(uri.Query);
            string? Read(string name) => query.TryGetValue(name, out StringValues value) ? value.ToString() : null;
            object result = db.GetInventoryLevels(
                Read("brand"), Read("region"), Read("category"), Read("status"), Read("variant"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json")
            });
        }
    }
}
