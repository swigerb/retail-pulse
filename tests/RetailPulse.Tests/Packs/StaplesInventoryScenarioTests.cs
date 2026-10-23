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

public sealed class StaplesInventoryScenarioTests : IDisposable
{
    private readonly string _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_staples_inventory");
    private readonly LoadedPack _pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load("staples-retail");
    private readonly RetailPulseDb _db;

    public StaplesInventoryScenarioTests()
    {
        _db = CreateDb();
    }

    private RetailPulseDb CreateDb() => new(
        new PackTenantProvider(_pack), _pack.Seed, _dbPath,
        Path.Combine(_pack.RootPath, "pack.yaml"), Path.Combine(_pack.RootPath, "seed"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    [Fact]
    public void OmittedVariant_PreservesInventoryScope_AndVariantFilterIsCaseInsensitive()
    {
        JsonElement all = Parse(_db.GetInventoryLevels("Contract Catalog", null, "Office Essentials", null));
        all.GetProperty("total_items").GetInt32().Should().Be(12);
        all.GetProperty("filters_applied").GetProperty("variant").ValueKind.Should().Be(JsonValueKind.Null);
        JsonElement filtered = Parse(_db.GetInventoryLevels("Contract Catalog", null, "Office Essentials", null, " paper & toner "));
        filtered.GetProperty("items").EnumerateArray().Select(item => item.GetRawText()).Should().BeEquivalentTo(
            all.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("variant").GetString() == "Paper & Toner")
                .Select(item => item.GetRawText()));
        filtered.GetProperty("total_items").GetInt32().Should().Be(4);
    }

    [Fact]
    public void ProductionFailureFilters_RemainEmpty_NotBroadenedOrAliased()
    {
        foreach (string? region in _pack.Tenant.Regions.Prepend(null))
        {
            Parse(_db.GetInventoryLevels("Contract Catalog", region, "Paper & Toner", null))
                .GetProperty("total_items").GetInt32().Should().Be(0);
        }

        Parse(_db.GetInventoryLevels("Workspace Furniture", null, "Office Essentials", null, "Paper & Toner"))
            .GetProperty("total_items").GetInt32().Should().Be(0);
        Parse(_db.GetInventoryLevels("Contract Catalog", null, "Office Essentials", null, "Unknown variant"))
            .GetProperty("total_items").GetInt32().Should().Be(0);
        Parse(_db.GetInventoryLevels("Contract Catalog", null, "Office Essentials", null, "Paper"))
            .GetProperty("total_items").GetInt32().Should().Be(0, "variant matching is exact, not a substring alias");
    }

    [Theory]
    [InlineData("Contract Catalog", "Office Essentials", "Paper & Toner", null, null,
        "Show days of supply for Paper & Toner by region")]
    [InlineData("Workspace Furniture", "Furniture & Technology", "Task Seating", "Pacific Rim", null,
        "Show current days of supply for Workspace Furniture, category Furniture & Technology, variant Task Seating in the Pacific Rim")]
    [InlineData("Contract Catalog", "Office Essentials", "Breakroom", "Atlantic Corridor", null,
        "Show Breakroom inventory for Contract Catalog, category Office Essentials, in the Atlantic Corridor")]
    public async Task ScriptedConfiguredAgent_ReturnsOnlyRequestedSeededVariant(
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
            .Should().BeEquivalentTo(region == null ? _pack.Tenant.Regions : [region]);
        result.GetProperty("total_items").GetInt32().Should().Be(region == null ? _pack.Tenant.Regions.Count : 1);
        items.Should().OnlyContain(item => item.GetProperty("days_of_supply").GetDouble() >= 0);
        JsonElement filters = result.GetProperty("filters_applied");
        filters.GetProperty("brand").GetString().Should().Be(brand);
        filters.GetProperty("category").GetString().Should().Be(category);
        filters.GetProperty("variant").GetString().Should().Be(variant);
        filters.GetProperty("region").GetString().Should().Be(region);
        filters.GetProperty("status").GetString().Should().Be(status);

        // The MCP tool and the API AIFunction must expose the same data and filter semantics.
        Parse(SupplyTools.GetInventoryLevels(_db, brand, region, category, status, variant))
            .GetRawText().Should().Be(result.GetRawText());
    }

    [Fact]
    public async Task ExplicitStatus_IsPreservedWithVariant()
    {
        JsonElement seed = Parse(_db.GetInventoryLevels("Contract Catalog", "Atlantic Corridor", "Office Essentials", null, "Breakroom"));
        string status = seed.GetProperty("items")[0].GetProperty("status").GetString()!;
        JsonElement result = await InvokeConfiguredAgent("Contract Catalog", "Office Essentials", "Breakroom", "Atlantic Corridor", status,
            $"Show {status} Breakroom inventory for Contract Catalog in the Atlantic Corridor");
        result.GetProperty("total_items").GetInt32().Should().Be(1);
        result.GetProperty("filters_applied").GetProperty("status").GetString().Should().Be(status);
        result.GetProperty("items")[0].GetProperty("status").GetString().Should().Be(status);
    }

    [Fact]
    public async Task SeedMutation_ReachesConfiguredAgent_AndSurvivesRestart()
    {
        const string prompt = "Show days of supply for Paper & Toner by region";
        JsonElement before = await InvokeConfiguredAgent("Contract Catalog", "Office Essentials", "Paper & Toner", null, null, prompt);
        ExecuteSql("""
            UPDATE InventoryLevels SET DaysOfSupply = 123.4
            WHERE Brand = 'Contract Catalog' AND Variant = 'Paper & Toner' AND Region = 'Pacific Rim'
            """).Should().Be(1);
        _ = CreateDb();
        JsonElement after = await InvokeConfiguredAgent("Contract Catalog", "Office Essentials", "Paper & Toner", null, null, prompt);
        static double ReadPacific(JsonElement result) => result.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("region").GetString() == "Pacific Rim")
            .GetProperty("days_of_supply").GetDouble();
        ReadPacific(before).Should().NotBe(123.4);
        ReadPacific(after).Should().Be(123.4, "the configured tool result must reflect the database, not a canned regional answer");
        after.GetProperty("total_items").GetInt32().Should().Be(4);
    }

    [Fact]
    public void LegacySchema_MigratesAndReseedsVariantIdentity_Once()
    {
        ExecuteSql("""
            ALTER TABLE InventoryLevels DROP COLUMN Variant;
            UPDATE SeedMetadata SET Value = 'v9:legacy-test' WHERE Key = 'tenant_hash';
            """);
        RetailPulseDb upgraded = CreateDb();
        JsonElement items = Parse(upgraded.GetInventoryLevels("Contract Catalog", null, "Office Essentials", null, "Paper & Toner"));
        items.GetProperty("total_items").GetInt32().Should().Be(4);
        items.GetProperty("items").EnumerateArray().Should().OnlyContain(item =>
            item.GetProperty("variant").GetString() == "Paper & Toner");
        ExecuteSql("UPDATE InventoryLevels SET DaysOfSupply = 77.7 WHERE Variant = 'Paper & Toner'").Should().Be(4);
        RetailPulseDb restarted = CreateDb();
        Parse(restarted.GetInventoryLevels("Contract Catalog", null, "Office Essentials", null, "Paper & Toner"))
            .GetProperty("items").EnumerateArray().Should().OnlyContain(item =>
                item.GetProperty("days_of_supply").GetDouble() == 77.7);
    }

    private int ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    // Tool selection and prose are scripted; this exercises real invocation, not external model judgment.
    private async Task<JsonElement> InvokeConfiguredAgent(
        string brand, string category, string variant, string? region, string? status, string userPrompt)
    {
        var handler = new InventoryHandler(_db);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://retailpulse.test") };
        var inventoryTool = new InventoryLevelsTool(httpClient);
        AgentDefinition definition = _pack.Agents.Agents["fulfillment-ops"];
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
                    messages.Should().Contain(m => m.Role == ChatRole.User && m.Text == userPrompt);
                    messages.Should().Contain(m => m.Role == ChatRole.System && m.Text.Contains(definition.SystemPrompt));
                    AIFunction function = options!.Tools!.OfType<AIFunction>().Single(f => f.Name == "GetInventoryLevels");
                    function.JsonSchema.GetProperty("properties").TryGetProperty("variant", out _).Should().BeTrue();
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [
                        new FunctionCallContent("inventory", "GetInventoryLevels", new Dictionary<string, object?>
                        {
                            ["brand"] = brand, ["category"] = category, ["variant"] = variant,
                            ["region"] = region, ["status"] = status
                        })
                    ]));
                }

                FunctionResultContent toolResult = messages.SelectMany(m => m.Contents)
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
        using var functionClient = new FunctionInvokingChatClient(model.Object) { MaximumIterationsPerRequest = 3 };
        var pipeline = new AgentExecutionPipeline(functionClient, AgentTestFixtures.CreateMockHubContext(),
            new ConfigurationBuilder().Build(), NullLogger<AgentExecutionPipeline>.Instance);
        var agent = new ConfiguredSpecialistAgent(pipeline, definition, registry.Resolve(definition.Tools, services));
        Contracts.ChatResponse response = await agent.HandleAsync(new Contracts.ChatRequest(userPrompt, SessionId: "staples-inventory-regression"));
        response.Reply.Should().Be("Seeded inventory received.");
        modelCalls.Should().Be(2);
        handler.Requests.Should().ContainSingle("one all-region query replaces five empty regional retries");
        received.Should().NotBeNull("the configured specialist must receive the real seeded tool result");
        return received!.Value;
    }

    private static string UnexpectedTool() => throw new InvalidOperationException("Unexpected tool");

    private static JsonElement Parse(object result) => JsonSerializer.SerializeToElement(result);

    private sealed class InventoryHandler(RetailPulseDb db) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
