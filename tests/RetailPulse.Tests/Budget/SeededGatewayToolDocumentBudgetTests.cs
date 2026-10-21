using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Budget;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace RetailPulse.Tests.Budget;

public sealed class SeededGatewayToolDocumentBudgetTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;
    private readonly ITestOutputHelper _output;
    private readonly ToolResultBudgetOptions _options = new()
    {
        Enabled = true,
        MaxResultChars = 6_000,
        MaxCumulativeChars = 9_500,
        MaxArrayItems = 24,
        PerToolMaxResultChars = new(StringComparer.OrdinalIgnoreCase)
        {
            ["GetPortfolioDepletionStats"] = 9_500
        }
    };
    private readonly ToolResultBudget _budget =
        new([new HistoricalDemandCompactor(), new PortfolioDepletionCompactor()]);

    public SeededGatewayToolDocumentBudgetTests(ITestOutputHelper output)
    {
        _output = output;
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");
        _dbPath = SqliteTestCleanup.NewDbPath("gateway_tool_docs");
        _db = new RetailPulseDb(
            new FileTenantProvider(tenantConfigPath),
            _dbPath,
            tenantConfigPath);
    }

    [Fact]
    public void PortfolioFastest_PathPreservesCompleteTwelveBrandAggregate()
    {
        string portfolio = Compact(
            "GetPortfolioDepletionStats",
            _db.GetPortfolioDepletionStats("National", "YTD"));

        IReadOnlyList<string> outgoing = Prepare("portfolio-fastest", [portfolio]);

        outgoing.Should().ContainSingle();
        outgoing.Single().Should().NotContain("budget_notice");
        using var document = JsonDocument.Parse(outgoing.Single());
        document.RootElement.GetProperty("brands").GetArrayLength().Should().Be(12,
            "the fastest-growing portfolio answer requires the complete seeded brand roster");
    }

    [Fact]
    public void UrbanLivingAllRegions_PathPreservesNationalTrendPayload()
    {
        string depletion = Compact(
            "GetDepletionStats",
            _db.GetDepletionStats("Urban Living", "National", "Q1"));

        IReadOnlyList<string> outgoing = Prepare("urban-living-all-regions", [depletion]);

        outgoing.Should().ContainSingle();
        outgoing.Single().Should().Contain("Urban Living");
        outgoing.Single().Should().NotContain("budget_notice");
    }

    [Fact]
    public void FoundryHomeVsUrbanLiving_PathPreservesBothCompactedHistories()
    {
        string foundry = Compact(
            "GetHistoricalDemand",
            _db.GetHistoricalDemand("Foundry Home", "West Coast", null, 12));
        string urban = Compact(
            "GetHistoricalDemand",
            _db.GetHistoricalDemand("Urban Living", "West Coast", null, 12));

        IReadOnlyList<string> outgoing = Prepare("foundry-vs-urban", [foundry, urban]);

        outgoing.Should().HaveCount(2);
        outgoing.Should().OnlyContain(result => !result.Contains("budget_notice", StringComparison.Ordinal));
        outgoing.Should().Contain(result => result.Contains("Foundry Home", StringComparison.Ordinal));
        outgoing.Should().Contain(result => result.Contains("Urban Living", StringComparison.Ordinal));
        outgoing.Sum(result => result.Length).Should().BeLessThanOrEqualTo(9_500);
    }

    private string Compact(string toolName, object payload)
    {
        string raw = JsonSerializer.Serialize(payload);
        return _budget.Apply(toolName, raw, _options).Json;
    }

    private IReadOnlyList<string> Prepare(string path, IReadOnlyList<string> results)
    {
        var messages = results
            .Select((result, index) => new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent($"call-{index}", result)]))
            .ToList();
        var boundary = new GatewayToolDocumentBudgetChatClient(
            Mock.Of<IChatClient>(),
            _options,
            NullLogger<GatewayToolDocumentBudgetChatClient>.Instance);

        IReadOnlyList<ChatMessage> outgoing = boundary.PrepareMessages(messages, out GatewayToolDocumentBudgetChatClient.BoundaryMetrics? metrics);
        _output.WriteLine(
            $"{path}: original_docs={metrics.OriginalDocuments} original_chars={metrics.OriginalChars} "
            + $"outgoing_docs={metrics.ReturnedDocuments} outgoing_chars={metrics.ReturnedChars} "
            + $"omitted_docs={metrics.OmittedDocuments}");
        metrics.ReturnedDocuments.Should().BeLessThanOrEqualTo(GatewayToolDocumentBudgetChatClient.MaxToolDocuments);
        metrics.ReturnedChars.Should().BeLessThanOrEqualTo(_options.MaxCumulativeChars);

        return [.. outgoing
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(result => GatewayToolDocumentBudgetChatClient.ResultText(result.Result))
            .Where(result => !string.IsNullOrWhiteSpace(result))];
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dbPath);
}
