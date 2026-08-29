using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.AI.ContentSafety;
using Azure.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

[Collection("AzureContentSafetyEvaluatorActivity")]
public sealed class ContentSafetyRetailPayloadTests : IDisposable
{
    private readonly string _dbPath = SqliteTestCleanup.NewDbPath("rp_contentsafety_retail_payloads");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    [Fact]
    public async Task GetStorePerformancePayload_PassesToolResultContentSafetyEvaluation()
    {
        string payload = BuildGetStorePerformancePayload();
        var handler = new RetailPayloadHandler();
        AzureContentSafetyEvaluator evaluator = BuildEvaluator(handler);

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            payload,
            ContentSafetyStage.ToolResult,
            new ContentSafetyEvaluationContext(UserId: "test-user", SourceId: "GetStorePerformance"),
            CancellationToken.None);

        result.Decision.Should().Be(ContentSafetyDecision.Passed);
        handler.AnalyzedTexts.Should().ContainSingle().Which.Should().Be(payload);
        payload.Should().NotContain("Strip Center", "the generated retail real-estate term triggers a sexual-content false positive");
        payload.Should().Contain("Shopping Center");
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"stores":[{"storeName":"Apex Retail Group Shopping Center #11","region":"Southwest","revenue":1835988.67,"target":1997181.15,"performanceIndex":0.919}],"count":1}""")]
    [InlineData(/*lang=json,strict*/ """{"stores":[{"storeName":"Apex Retail Group Outlet #1","region":"Northeast","revenue":1508629.63,"target":1799874.22,"performanceIndex":0.838,"issues":["Below target"]}],"count":1}""")]
    public async Task RepresentativeRetailToolPayloads_PassContentSafetyEvaluation(string payload)
    {
        var handler = new RetailPayloadHandler();
        AzureContentSafetyEvaluator evaluator = BuildEvaluator(handler);

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            payload,
            ContentSafetyStage.ToolResult,
            new ContentSafetyEvaluationContext(UserId: "test-user", SourceId: "GetStorePerformance"),
            CancellationToken.None);

        result.Decision.Should().Be(ContentSafetyDecision.Passed);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"categories":[{"name":"Intimate Apparel","sales":143200,"target":150000,"region":"Northeast"}]}""")]
    [InlineData(/*lang=json,strict*/ """{"categories":[{"name":"Adult Beverage","sales":219500,"target":205000,"region":"West Coast"}]}""")]
    [InlineData(/*lang=json,strict*/ """{"categories":[{"name":"Body Care","sales":98200,"target":96000,"region":"Southeast"}]}""")]
    [InlineData(/*lang=json,strict*/ """{"categories":[{"name":"Breast Pump Accessories","sales":65200,"target":64000,"region":"Midwest"}]}""")]
    public async Task AmbiguousRetailVocabulary_WithLowContentSafetyScores_DoesNotBlock(string payload)
    {
        var handler = new RetailPayloadHandler
        {
            DefaultCategories =
            [
                new { category = "Sexual", severity = 2 }
            ]
        };
        AzureContentSafetyEvaluator evaluator = BuildEvaluator(handler);

        ContentSafetyResult result = await evaluator.EvaluateAsync(
            payload,
            ContentSafetyStage.ToolResult,
            new ContentSafetyEvaluationContext(UserId: "test-user", SourceId: "GetRetailMetrics"),
            CancellationToken.None);

        result.Decision.Should().NotBe(ContentSafetyDecision.Blocked);
    }

    private string BuildGetStorePerformancePayload()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        var db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);

        return JsonSerializer.Serialize(db.GetStorePerformance());
    }

    private static AzureContentSafetyEvaluator BuildEvaluator(RetailPayloadHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://fake.cognitiveservices.azure.com")
        };
        var credential = new FakeTokenCredential();
        var sdk = new ContentSafetyClient(
            http.BaseAddress,
            credential,
            new ContentSafetyClientOptions { Transport = new HttpClientTransport(http) });
        var config = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                Endpoint = http.BaseAddress.ToString(),
                PromptShieldsEnabled = false
            }
        };

        return new AzureContentSafetyEvaluator(
            sdk,
            http,
            new ContentSafetyTokenProvider(credential),
            config,
            NullLoggerFactory.Instance.CreateLogger<AzureContentSafetyEvaluator>());
    }

    private sealed class RetailPayloadHandler : HttpMessageHandler
    {
        public List<string> AnalyzedTexts { get; } = [];
        public IReadOnlyList<object> DefaultCategories { get; init; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("text:analyze", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        userPromptAnalysis = new { attackDetected = false },
                        documentsAnalysis = Array.Empty<object>()
                    })
                };
            }

            string requestBody = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string text = JsonDocument.Parse(requestBody).RootElement.GetProperty("text").GetString() ?? "";
            AnalyzedTexts.Add(text);

            object[] categories = text.Contains("Strip Center", StringComparison.OrdinalIgnoreCase)
                ? [new { category = "Sexual", severity = 4 }]
                : [.. DefaultCategories];

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { categoriesAnalysis = categories })
            };
        }
    }
}
