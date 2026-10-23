using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.AI.ContentSafety;
using Azure.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Packs;
using RetailPulse.Api.Prompts;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Tests.Guardrails.AgentDefinitions;
using RetailPulse.Tests.Packs;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

[Collection("AzureContentSafetyEvaluatorActivity")]
public sealed class ContentSafetyShieldSegmentationTests
{
    [Fact]
    public async Task ShippedGeneralPrompt_PassesEnforcedShieldCap_WithExactFullCoverage()
    {
        string text = GeneralPrompt();
        text.Length.Should().Be(11_581);
        Hash(text).Should().Be("091C0BE4FFF946A852DBB78DCCE729FDC9DCB2CB709BB2517AEB5643EDB3B1D6");
        using var rig = new Rig();

        ContentSafetyResult result = await rig.EvaluateAsync(text, ContentSafetyStage.AgentDefinition);

        result.Decision.Should().Be(ContentSafetyDecision.Passed);
        rig.Handler.Shields.Count.Should().Be(2);
        rig.Handler.Shields.Select(window => window.Length).Should().Equal(9_000, 3_081);
        AssertCoverage(text, rig.Handler.Shields);
        rig.Handler.Moderation.Select(Hash).Should().Equal(rig.Handler.Shields.Select(Hash));
        rig.Handler.CapViolations.Should().Be(0);
    }

    [Theory]
    [InlineData(ContentSafetyStage.Input)]
    [InlineData(ContentSafetyStage.Output)]
    [InlineData(ContentSafetyStage.AgentDefinition)]
    [InlineData(ContentSafetyStage.ToolResult)]
    [InlineData(ContentSafetyStage.RetrievedKnowledge)]
    public async Task StageSemanticsAndShortInput_ArePreserved(ContentSafetyStage stage)
    {
        using var rig = new Rig();
        ContentSafetyResult result = await rig.EvaluateAsync("synthetic short input", stage);

        result.Decision.Should().Be(ContentSafetyDecision.Passed);
        rig.Handler.Shields.Count.Should().Be(1);
        Hash(rig.Handler.Shields.Single()).Should().Be(Hash("synthetic short input"));
        rig.Handler.Moderation.Count.Should().Be(1);
        rig.Handler.DocumentRequests.Should().Be(IsDocument(stage) ? 1 : 0);
        rig.Handler.PromptRequests.Should().Be(IsDocument(stage) ? 0 : 1);
    }

    [Theory]
    [InlineData(ContentSafetyStage.Input)]
    [InlineData(ContentSafetyStage.AgentDefinition)]
    [InlineData(ContentSafetyStage.ToolResult)]
    [InlineData(ContentSafetyStage.RetrievedKnowledge)]
    public async Task LateAttackInSecondWindow_BlocksInCorrectSemanticLane(ContentSafetyStage stage)
    {
        using var rig = new Rig();
        rig.Handler.AttackAt = 2;

        ContentSafetyResult result = await rig.EvaluateAsync(GeneralPrompt(), stage);

        result.Decision.Should().Be(ContentSafetyDecision.Blocked);
        result.PromptShieldJailbreakDetected.Should().Be(!IsDocument(stage));
        result.PromptShieldIndirectInjectionDetected.Should().Be(IsDocument(stage));
        rig.Handler.Shields.Count.Should().Be(2);
        rig.Handler.DocumentRequests.Should().Be(IsDocument(stage) ? 2 : 0);
        rig.Handler.PromptRequests.Should().Be(IsDocument(stage) ? 0 : 2);
        rig.Handler.CapViolations.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmedAttack_CannotBeOverriddenByLaterUnavailability()
    {
        using var rig = new Rig();
        rig.Handler.AttackAt = 1;
        rig.Handler.FailAt = 2;

        ContentSafetyResult result = await rig.EvaluateAsync(GeneralPrompt(), ContentSafetyStage.AgentDefinition);

        result.Decision.Should().Be(ContentSafetyDecision.Blocked);
        rig.Handler.Shields.Count.Should().Be(2);
        rig.Handler.Moderation.Count.Should().Be(0);
    }

    [Fact]
    public async Task FirstWindowAttack_IsNotClearedByLaterCleanWindow()
    {
        using var rig = new Rig();
        rig.Handler.AttackAt = 1;

        ContentSafetyResult result = await rig.EvaluateAsync(GeneralPrompt(), ContentSafetyStage.AgentDefinition);

        result.Decision.Should().Be(ContentSafetyDecision.Blocked);
        result.PromptShieldJailbreakDetected.Should().BeTrue();
        rig.Handler.Shields.Count.Should().Be(2);
        rig.Handler.Moderation.Count.Should().Be(2);
    }

    [Fact]
    public async Task OverlapIncludesAttackThatStraddlesTheNominalCut()
    {
        const string marker = "synthetic-cross-boundary-attack-marker";
        string text = new string('a', 8_990) + marker + new string('b', 2_000);
        using var rig = new Rig();
        rig.Handler.AttackMarker = marker;

        ContentSafetyResult result = await rig.EvaluateAsync(text, ContentSafetyStage.Input);

        result.Decision.Should().Be(ContentSafetyDecision.Blocked);
        rig.Handler.Shields.Count.Should().Be(2);
        rig.Handler.Shields[0].Contains(marker, StringComparison.Ordinal).Should().BeFalse();
        rig.Handler.Shields[1].Contains(marker, StringComparison.Ordinal).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateUnavailable_AppliesUnchangedPolicy_WhileScanningEveryRealDefinition(bool regulated)
    {
        LoadedPack pack = HydratedPack();
        string text = pack.Agents.Agents.Values.Single(definition => definition.Key == "general").SystemPrompt;
        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig(onUnavailable: ContentSafetyFailPolicy.FailOpen);
        config.ContentSafety.ForceFailClosed = regulated;
        using var rig = new Rig(config);
        rig.Handler.FailTextHash = Hash(text[8_500..]);
        var audit = new InMemorySuspiciousRequestLog();
        var validator = new AgentDefinitionValidator(config, new JailbreakDetector(), rig.Evaluator, audit,
            AgentDefinitionValidatorToolCatalog.Build(), NullLogger<AgentDefinitionValidator>.Instance);

        if (regulated)
        {
            AgentDefinitionValidationException exception = (await FluentActions
                .Invoking(() => validator.ValidateAsync(pack.Agents))
                .Should().ThrowAsync<AgentDefinitionValidationException>()).Which;
            exception.Violations.Should().ContainSingle().Which.AgentKey.Should().Be("general");
        }
        else
        {
            (await validator.ValidateAsync(pack.Agents)).Violations.Should().BeEmpty();
        }

        pack.Agents.Agents.Should().HaveCount(16);
        rig.Handler.Shields.Count.Should().Be(72, "all 71 real fields are evaluated; general has two shield windows");
        rig.Handler.Moderation.Count.Should().Be(70, "the failed field cannot be treated as checked");
        rig.Handler.CapViolations.Should().Be(0);
        IReadOnlyList<SuspiciousRequest> rows = await audit.GetRecentAsync(100);
        rows.Should().ContainSingle().Which.Action.Should().Be(regulated ? "blocked" : "failopen-passed");
    }

    [Fact]
    public async Task WindowsShareOneScanBudget_AndCancellationReachesTransport()
    {
        var config = new GuardrailsConfig { ContentSafety = new ContentSafetyConfig { TimeoutMs = 200 } };
        using var rig = new Rig(config);
        rig.Handler.DelayMs = 125;

        ContentSafetyResult result = await rig.EvaluateAsync(GeneralPrompt(), ContentSafetyStage.AgentDefinition);

        result.Decision.Should().Be(ContentSafetyDecision.ServiceUnavailable);
        result.FailureReason.Should().Be(ContentSafetyFailureReason.Timeout);
        rig.Handler.CancellationObserved.Should().BeTrue();
        rig.Handler.Moderation.Count.Should().Be(0);
    }

    [Fact]
    public async Task CallerCancellationInLaterWindow_PropagatesWithoutRetry()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.Handler.CancelAt = 2;
        rig.Handler.Cancel = cts.Cancel;

        await FluentActions.Invoking(() => rig.EvaluateAsync(GeneralPrompt(), ContentSafetyStage.AgentDefinition, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        rig.Handler.Shields.Count.Should().Be(2);
        rig.Handler.Moderation.Count.Should().Be(0);
        rig.Handler.CancellationObserved.Should().BeTrue();
    }

    [Theory]
    [InlineData(9_000)]
    [InlineData(9_001)]
    [InlineData(10_000)]
    [InlineData(10_001)]
    [InlineData(30_001)]
    public async Task ArbitraryLengths_AreFullyCoveredWithinTheContract(int length)
    {
        string text = string.Concat(Enumerable.Range(0, (length + 9) / 10).Select(index => $"{index:D9}|"))[..length];
        using var rig = new Rig();

        (await rig.EvaluateAsync(text, ContentSafetyStage.Input)).Decision.Should().Be(ContentSafetyDecision.Passed);

        AssertCoverage(text, rig.Handler.Shields);
        rig.Handler.Shields.All(window => window.Length <= 9_000).Should().BeTrue();
        rig.Handler.CapViolations.Should().Be(0);
    }

    [Fact]
    public async Task UnicodeWindows_PreserveSurrogatePairs_AndExactInput()
    {
        // Pairs straddle the nominal end and overlap-start boundaries; combining marks also survive unchanged.
        string text = new string('a', 8_499) + char.ConvertFromUtf32(0x1F600)
            + new string('b', 498) + char.ConvertFromUtf32(0x1F642)
            + string.Concat(Enumerable.Repeat("e\u0301", 3_000));
        using var rig = new Rig();

        (await rig.EvaluateAsync(text, ContentSafetyStage.ToolResult)).Decision.Should().Be(ContentSafetyDecision.Passed);

        AssertCoverage(text, rig.Handler.Shields);
        var strictUtf8 = new UTF8Encoding(false, true);
        foreach (string window in rig.Handler.Shields)
        {
            strictUtf8.GetByteCount(window).Should().BeGreaterThan(0);
            char.IsLowSurrogate(window[0]).Should().BeFalse();
            char.IsHighSurrogate(window[^1]).Should().BeFalse();
        }
    }

    private static void AssertCoverage(string text, IReadOnlyList<string> windows)
    {
        windows.Count.Should().BeGreaterThan(0);
        string reassembled = windows[0];
        int previousStart = 0;
        foreach (string window in windows.Skip(1))
        {
            int start = text.IndexOf(window, previousStart + 1, StringComparison.Ordinal);
            start.Should().BeGreaterThan(previousStart);
            int overlap = reassembled.Length - start;
            overlap.Should().BeGreaterThanOrEqualTo(500);
            reassembled += window[overlap..];
            previousStart = start;
        }
        Hash(reassembled).Should().Be(Hash(text), "reassembly must cover every character, not just a prefix or hash inventory");
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool IsDocument(ContentSafetyStage stage) => stage is ContentSafetyStage.ToolResult or ContentSafetyStage.RetrievedKnowledge;
    private static string GeneralPrompt() => HydratedPack().Agents.Agents.Values.Single(definition => definition.Key == "general").SystemPrompt;
    private static LoadedPack HydratedPack()
    {
        LoadedPack pack = PackLoader.ForDirectory(PackTestPaths.PacksRoot).Load("default");
        var engine = new PromptTemplateEngine(new PackTenantProvider(pack).GetTenant());
        foreach ((string section, Api.Models.AgentDefinition definition) in pack.Agents.Agents)
        {
            if (string.IsNullOrWhiteSpace(definition.Key)) definition.Key = section;
            if (definition.Key != "router") engine.Hydrate(definition);
        }
        return pack;
    }

    private sealed class Rig : IDisposable
    {
        public CapHandler Handler { get; } = new();
        private readonly HttpClient _http;
        public AzureContentSafetyEvaluator Evaluator { get; }
        public Rig(GuardrailsConfig? config = null)
        {
            _http = new HttpClient(Handler) { BaseAddress = new Uri("https://cap-contract.example.test") };
            var credential = new FakeTokenCredential();
            var options = new ContentSafetyClientOptions { Transport = new HttpClientTransport(_http) };
            options.Retry.MaxRetries = 0;
            var sdk = new ContentSafetyClient(_http.BaseAddress, credential, options);
            Evaluator = new AzureContentSafetyEvaluator(sdk, _http, new ContentSafetyTokenProvider(credential),
                config ?? new GuardrailsConfig(), NullLogger<AzureContentSafetyEvaluator>.Instance);
        }
        public Task<ContentSafetyResult> EvaluateAsync(string text, ContentSafetyStage stage, CancellationToken ct = default) =>
            Evaluator.EvaluateAsync(text, stage, new ContentSafetyEvaluationContext(UserId: "synthetic", CheckPromptShield: true), ct);
        public void Dispose() => _http.Dispose();
    }

    private sealed class CapHandler : HttpMessageHandler
    {
        public List<string> Shields { get; } = [];
        public List<string> Moderation { get; } = [];
        public int DocumentRequests;
        public int PromptRequests;
        public int CapViolations;
        public int AttackAt;
        public string? AttackMarker;
        public int FailAt;
        public string? FailTextHash;
        public int DelayMs;
        public int CancelAt;
        public Action? Cancel;
        public bool CancellationObserved;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using JsonDocument body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (request.RequestUri!.AbsolutePath.EndsWith("text:analyze", StringComparison.Ordinal))
            {
                string text = body.RootElement.GetProperty("text").GetString()!;
                Moderation.Add(text);
                return text.Length > 10_000
                    ? Rejected()
                    : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { categoriesAnalysis = Array.Empty<object>() }) };
            }
            request.RequestUri.PathAndQuery.Should().Be("/contentsafety/text:shieldPrompt?api-version=2024-09-01");
            bool document = body.RootElement.TryGetProperty("documents", out JsonElement documents);
            string input;
            if (document)
            {
                DocumentRequests++;
                body.RootElement.TryGetProperty("userPrompt", out _).Should().BeFalse();
                documents.GetArrayLength().Should().Be(1, "documents are not concatenated or relabeled as user prompts");
                input = documents[0].GetString()!;
            }
            else
            {
                PromptRequests++;
                input = body.RootElement.GetProperty("userPrompt").GetString()!;
            }
            Shields.Add(input);
            if (input.Length > 10_000) return Rejected();
            if (Shields.Count == CancelAt) Cancel!();
            try
            {
                ct.ThrowIfCancellationRequested();
                if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
            if (Shields.Count == FailAt || Hash(input) == FailTextHash) return new(HttpStatusCode.ServiceUnavailable);
            bool attack = Shields.Count == AttackAt
                || (AttackMarker is not null && input.Contains(AttackMarker, StringComparison.Ordinal));
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    userPromptAnalysis = new { attackDetected = !document && attack },
                    documentsAnalysis = document ? new[] { new { attackDetected = attack } } : [],
                }),
            };
        }

        private HttpResponseMessage Rejected()
        {
            CapViolations++;
            return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = new { code = "LocalLengthLimit" } }) };
        }
    }
}
