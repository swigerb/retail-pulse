using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using RetailPulse.Api.OpenAI.Gateway;
using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Issue #339 — APIM semantic-cache policy path. The fragments are rendered exactly as the Bicep module renders them
/// (same placeholders), the classifier patterns, similarity threshold, TTL and partition keys are READ FROM THE
/// RENDERED XML, and a small pipeline harness runs request → inbound decision → lookup → backend → outbound safety →
/// store against a fake external cache and a fake backend that meters tokens. Drift guards pin the policy expressions
/// the harness mirrors. No Azure, Redis or key material is involved.
/// </summary>
public sealed partial class SemanticCachePolicyRuntimeTests
{
    private static readonly string Modules = Path.Combine(FindRepoRoot(), "infra", "modules");

    private static string Read(string name) => File.ReadAllText(Path.Combine(Modules, name));

    private const string Backend = "embeddings-backend";

    private static RenderedPolicy Render(bool regulated = false, string threshold = "0.05", int ttl = 300) => new(
        Read("apim-openai-semantic-cache-inbound.xml")
            .Replace("{cache-regulated}", regulated ? "true" : "false")
            .Replace("{cache-score-threshold}", threshold)
            .Replace("{cache-embeddings-backend-id}", Backend),
        Read("apim-openai-semantic-cache-outbound.xml").Replace("{cache-ttl}", ttl.ToString()),
        Read("apim-openai-semantic-cache-error.xml"));

    private static JsonObject Chat(string user, bool stream = false, bool tools = false, params (string Role, string Content)[] extra)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = "You are a retail assistant." } };
        foreach ((string role, string content) in extra)
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = user });
        var body = new JsonObject { ["messages"] = messages };
        if (stream) body["stream"] = true;
        if (tools) body["tools"] = new JsonArray(new JsonObject { ["type"] = "function" });
        return body;
    }

    private static GatewayRequest Req(string prompt, string tenant = "acme", string subscription = "sub-1", JsonObject? body = null) =>
        new(subscription, tenant, body ?? Chat(prompt), "/inference/chat/completions");

    // ---- hit / miss / token reduction ---------------------------------------------------------------------

    [Fact]
    public void EquivalentSafePrompts_MissThenHit_AndBackendTokensAreNotSpentAgain()
    {
        var gw = new GatewayHarness(Render());
        var first = gw.Handle(Req("What is your return policy?"));
        var second = gw.Handle(Req("what's the return policy"));
        var third = gw.Handle(Req("Tell me about the return policy, please"));

        first.CacheEvidence.Should().Be("miss");
        second.CacheEvidence.Should().Be("hit");
        third.CacheEvidence.Should().Be("hit");
        second.Body.Should().Be(first.Body);
        gw.BackendCalls.Should().Be(1);
        gw.BackendTokens.Should().Be(GatewayHarness.TokensPerCall, "three equivalent prompts cost one backend call");
        second.BackendTokens.Should().Be(0);
    }

    [Fact]
    public void DifferentTopic_IsAMissEvenWhenTheCacheIsWarm()
    {
        var gw = new GatewayHarness(Render());
        gw.Handle(Req("What is your return policy?"));
        gw.Handle(Req("Explain the warranty coverage terms")).CacheEvidence.Should().Be("miss");
        gw.BackendCalls.Should().Be(2);
    }

    [Fact]
    public void SimilarityThreshold_IsReadFromThePolicy_AndAStricterOneRejectsAFuzzyMatch()
    {
        // "return policy" vs "return policy for shoes": distance ≈ 0.18.
        var loose = new GatewayHarness(Render(threshold: "0.20"));
        loose.Handle(Req("What is your return policy?"));
        loose.Handle(Req("What is your return policy for shoes?")).CacheEvidence.Should().Be("hit");

        var strict = new GatewayHarness(Render(threshold: "0.05"));
        strict.Handle(Req("What is your return policy?"));
        strict.Handle(Req("What is your return policy for shoes?")).CacheEvidence.Should().Be("miss");
    }

    [Fact]
    public void Ttl_IsReadFromThePolicy_AndExpiredEntriesMissAgain()
    {
        var gw = new GatewayHarness(Render(ttl: 120));
        gw.Handle(Req("What is your return policy?"));
        gw.Advance(TimeSpan.FromSeconds(119));
        gw.Handle(Req("What is your return policy?")).CacheEvidence.Should().Be("hit");
        gw.Advance(TimeSpan.FromSeconds(2));
        gw.Handle(Req("What is your return policy?")).CacheEvidence.Should().Be("miss");
    }

    // ---- bypass ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("How much does the Acme blender cost?", "dynamic-intent")]
    [InlineData("What is the price of the return shipping label?", "dynamic-intent")]
    [InlineData("Is the shipping box in stock at my store?", "dynamic-intent")]
    [InlineData("Show inventory for the return desk", "dynamic-intent")]
    [InlineData("Which items had a stockout this week with returns?", "dynamic-intent")]
    [InlineData("Was the product I returned part of a recall?", "sensitive-intent")]
    [InlineData("What is the pharmacy return policy?", "sensitive-intent")]
    [InlineData("Can I return my prescription medication?", "sensitive-intent")]
    [InlineData("Explain the return policy for my health order", "sensitive-intent")]
    [InlineData("Can patient records be part of a refund request?", "sensitive-intent")]
    [InlineData("What is the clinical warranty policy?", "sensitive-intent")]
    [InlineData("Email me the return policy at jo@example.com", "sensitive-intent")]
    [InlineData("Return policy for order 1234567 please", "sensitive-intent")]
    [InlineData("Cancel my return and update the refund", "mutation")]
    [InlineData("Please cancel the return and explain the policy", "mutation")]
    [InlineData("Create a new return for the warranty claim", "mutation")]
    [InlineData("What is the weather like in Seattle today?", "dynamic-intent")]
    [InlineData("Tell me a joke about shopping carts", "unknown-intent")]
    [InlineData("hi", "low-confidence")]
    [InlineData("   ", "blank")]
    public void SensitiveDynamicMutationUnknownAndBlankPrompts_AlwaysBypass_EvenWhenWarm(string prompt, string reason)
    {
        var gw = new GatewayHarness(Render());
        gw.Handle(Req("What is your return policy?")); // warm cache must never serve these
        int callsBefore = gw.BackendCalls;

        var r1 = gw.Handle(Req(prompt));
        var r2 = gw.Handle(Req(prompt));

        r1.CacheEvidence.Should().Be("bypass");
        r1.CacheReason.Should().Be(reason);
        r2.CacheEvidence.Should().Be("bypass");
        gw.BackendCalls.Should().Be(callsBefore + 2, "bypassed requests always reach the backend and are never stored");
        gw.CacheEntries.Should().Be(1);
    }

    [Fact]
    public void StreamingToolsMultiTurnAndRegulatedRequests_Bypass()
    {
        string safe = "What is your return policy?";
        var gw = new GatewayHarness(Render());
        gw.Handle(Req(safe, body: Chat(safe, stream: true))).CacheReason.Should().Be("streaming");
        gw.Handle(Req(safe, body: Chat(safe, tools: true))).CacheReason.Should().Be("tools");
        gw.Handle(Req(safe, body: Chat(safe, extra: [("user", "hello there friend"), ("assistant", "Hi!")]))).CacheReason.Should().Be("low-confidence");
        gw.Handle(Req(safe, body: Chat(safe, extra: [("assistant", "Earlier answer")]))).CacheReason.Should().Be("low-confidence");
        gw.Handle(Req(safe, body: new JsonObject())).CacheReason.Should().Be("unknown-intent");
        gw.CacheEntries.Should().Be(0);

        var regulated = new GatewayHarness(Render(regulated: true));
        regulated.Handle(Req(safe)).CacheReason.Should().Be("regulated");
        regulated.Handle(Req(safe)).CacheEvidence.Should().Be("bypass");
        regulated.CacheEntries.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Acme Corp")]
    [InlineData("acme_corp")]
    [InlineData("-acme")]
    public void MissingOrMalformedTenant_Bypasses_SoNothingIsSharedAcrossAnonymousCallers(string tenant)
    {
        var gw = new GatewayHarness(Render());
        var r = gw.Handle(Req("What is your return policy?", tenant: tenant));
        r.CacheEvidence.Should().Be("bypass");
        r.CacheReason.Should().Be("no-tenant");
        gw.CacheEntries.Should().Be(0);
    }

    // ---- partitioning ---------------------------------------------------------------------------------------

    [Fact]
    public void Cache_IsPartitionedByTenantAndSubscription()
    {
        var gw = new GatewayHarness(Render());
        gw.Handle(Req("What is your return policy?", tenant: "acme", subscription: "sub-1")).CacheEvidence.Should().Be("miss");
        gw.Handle(Req("What is your return policy?", tenant: "acme", subscription: "sub-1")).CacheEvidence.Should().Be("hit");
        gw.Handle(Req("What is your return policy?", tenant: "globex", subscription: "sub-1")).CacheEvidence.Should().Be("miss",
            "another retailer must never read acme's entry");
        gw.Handle(Req("What is your return policy?", tenant: "acme", subscription: "sub-2")).CacheEvidence.Should().Be("miss",
            "a different APIM subscription is a different partition");
        gw.Handle(Req("What is your return policy?", tenant: "globex", subscription: "sub-1")).CacheEvidence.Should().Be("hit");
        gw.BackendCalls.Should().Be(3);
    }

    [Fact]
    public void RenderedLookup_VariesByTrustedSubscriptionTenantAndPath_AndNeverByPromptOrBody()
    {
        string[] varyBy = [.. Render().Inbound.Descendants("vary-by").Select(v => Decode(v.Value))];
        varyBy.Should().Equal("@(context.Subscription.Id)", "@((string)context.Variables[\"rp-cache-tenant\"])", "@(context.Request.Url.Path)");
        string inbound = Decode(Render().InboundText);
        inbound.Should().Contain("x-rp-tenant-id\" exists-action=\"delete\"", "the caller-supplied tenant header is consumed, not forwarded to the model backend");
        TenantRegexRegex().IsMatch(inbound).Should().BeTrue();
    }

    // ---- blocked / failed responses -------------------------------------------------------------------------

    [Fact]
    public void BlockedOutput_IsNeverStored_AndIsNotServedLater()
    {
        var gw = new GatewayHarness(Render());
        gw.NextBackend = new BackendResult(200, "UNSAFE", OutputUnsafe: true);
        var blocked = gw.Handle(Req("What is your return policy?"));
        blocked.Status.Should().Be(403, "the safety outbound replaces the response before cache-store runs");
        blocked.CacheEvidence.Should().BeNull("return-response ends the outbound pipeline before cache evidence is emitted");
        gw.CacheEntries.Should().Be(0);

        gw.NextBackend = null;
        gw.Handle(Req("What is your return policy?")).CacheEvidence.Should().Be("miss");
        gw.Handle(Req("What is your return policy?")).CacheEvidence.Should().Be("hit");
        gw.LastStoredBody.Should().NotContain("UNSAFE");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void FailedBackendResponses_AreNeverStored(int status)
    {
        var gw = new GatewayHarness(Render());
        gw.NextBackend = new BackendResult(status, "error");
        var r = gw.Handle(Req("What is your return policy?"));
        r.CacheEvidence.Should().Be("bypass");
        r.CacheReason.Should().Be("failed");
        gw.CacheEntries.Should().Be(0);
    }

    [Fact]
    public void ResponseWhoseOutputWasNotSafetyChecked_IsNeverStored()
    {
        var gw = new GatewayHarness(Render());
        gw.NextBackend = new BackendResult(200, "unchecked", OutputChecked: false);
        var r = gw.Handle(Req("What is your return policy?"));
        r.CacheEvidence.Should().Be("bypass");
        r.CacheReason.Should().Be("unsafe");
        gw.CacheEntries.Should().Be(0);
    }

    // ---- cache unavailable ----------------------------------------------------------------------------------

    [Fact]
    public void CacheUnavailable_ServesFromBackendWithUnavailableEvidence_AndStoresNothing()
    {
        var gw = new GatewayHarness(Render()) { CacheAvailable = false };
        var r = gw.Handle(Req("What is your return policy?"));
        r.Status.Should().Be(200);
        r.CacheEvidence.Should().Be("unavailable");
        r.CacheReason.Should().Be("cache-error");
        r.Body.Should().NotBeNullOrEmpty();
        gw.BackendCalls.Should().Be(1);
        gw.CacheEntries.Should().Be(0);

        gw.CacheAvailable = true;
        gw.Handle(Req("What is your return policy?")).CacheEvidence.Should().Be("miss", "recovery needs no manual step");
    }

    [Fact]
    public void RenderedErrorFragment_ReportsOnlyAFixedUnavailableMarker_ForSemanticCachePolicyErrors()
    {
        string error = Decode(Render().ErrorText);
        error.Should().Contain("context.LastError.Source.Contains(\"semantic-cache\")");
        error.Should().Contain("<value>unavailable</value>").And.Contain("<value>cache-error</value>");
        error.Should().NotContain("LastError.Message").And.NotContain("LastError.Reason", "error detail may carry endpoint or key text");
    }

    // ---- rendered policy contract / drift guards -----------------------------------------------------------

    [Fact]
    public void RenderedFragments_AreWellFormed_WithSupportedPolicies_AndBoundedAttributes()
    {
        RenderedPolicy p = Render(threshold: "0.12", ttl: 600);
        XElement lookup = p.Inbound.Descendants("azure-openai-semantic-cache-lookup").Single();
        lookup.Attribute("score-threshold")!.Value.Should().Be("0.12");
        lookup.Attribute("embeddings-backend-id")!.Value.Should().Be(Backend);
        lookup.Attribute("embeddings-backend-auth")!.Value.Should().Be("system-assigned");
        lookup.Attribute("ignore-system-messages")!.Value.Should().Be("true");
        lookup.Attribute("max-message-count")!.Value.Should().Be("3");
        p.Outbound.Descendants("azure-openai-semantic-cache-store").Single().Attribute("duration")!.Value.Should().Be("600");
        p.InboundText.Should().NotContain("{cache-").And.NotContain("{semantic");
        p.OutboundText.Should().NotContain("{cache-");

        string all = p.InboundText + p.OutboundText + p.ErrorText;
        all.Should().NotContainEquivalentOf("connectionstring").And.NotContainEquivalentOf("accesskey")
            .And.NotContainEquivalentOf("primarykey").And.NotContainEquivalentOf("redis")
            .And.NotContain("<trace").And.NotContain("log-to-eventhub");
    }

    [Fact]
    public void DecisionExpression_MatchesTheHarnessMirror_InOrderOfReasons()
    {
        string expr = Decode(Render().InboundText);
        string decision = expr[expr.IndexOf("name=\"rp-cache-decision\"", StringComparison.Ordinal)..];
        string[] reasons = [.. ReturnLiteralRegex().Matches(decision).Select(m => m.Groups[1].Value).Distinct()];
        reasons.Should().Equal(GatewayHarness.ReasonOrder);
        decision.Should().Contain("catch (Exception) { return \"unknown-intent\"; }", "a malformed body must bypass, never throw");
    }

    [Fact]
    public void OutboundStore_RequiresBackend200_SafetyCheckedOutput_AndNeverHitsBlockedFlow()
    {
        string outbound = Decode(Render().OutboundText);
        string store = outbound[..outbound.IndexOf("<azure-openai-semantic-cache-store", StringComparison.Ordinal)];
        store.Should().Contain("== \"eligible\"").And.Contain("context.Response.StatusCode == 200")
            .And.Contain("context.Variables.ContainsKey(\"rp-backend-start\")")
            .And.Contain("((string)context.Variables[\"rp-safety-output-checked\"]) == \"true\"");
        outbound.Split("<azure-openai-semantic-cache-store").Length.Should().Be(2, "exactly one store, in one guarded branch");
    }

    [Fact]
    public void BasePolicy_OrdersCacheAfterSafetyAndBeforeBackend_AndStoreAfterOutboundSafety()
    {
        string b = Read("apim-openai-policy.xml");
        int Idx(string s) => b.IndexOf(s, StringComparison.Ordinal);
        Idx("{safety-inbound}").Should().BeLessThan(Idx("{semantic-cache-inbound}"));
        Idx("{semantic-cache-inbound}").Should().BeLessThan(Idx("<set-backend-service"));
        Idx("{semantic-cache-inbound}").Should().BeLessThan(Idx("{budget-limit}"), "a hit must consume no token budget");
        Idx("{safety-outbound}").Should().BeLessThan(Idx("{semantic-cache-outbound}"));
        Idx("{semantic-cache-outbound}").Should().BeLessThan(Idx("x-rp-gw-request-id\" exists-action=\"override"));
        b.IndexOf("{semantic-cache-error}", Idx("<on-error>"), StringComparison.Ordinal).Should().BeGreaterThan(0);
        b.Should().Contain("<set-header name=\"x-rp-gw-cache\" exists-action=\"delete\" />")
            .And.Contain("<set-header name=\"x-rp-gw-cache-reason\" exists-action=\"delete\" />");
        b.Should().NotContain("azure-openai-semantic-cache", "the base policy renders no cache policy unless Bicep opts in");
    }

    [Fact]
    public void Bicep_DefaultsDisabled_RequiresInfraBackendAndSafety_AndBoundsThresholdAndTtl()
    {
        string api = Read("apim-openai-api.bicep");
        api.Should().Contain("param semanticCachePolicyEnabled bool = false");
        api.Should().Contain("var semanticCachePolicyActive = semanticCachePolicyEnabled && gatewaySafetyEnabled && !empty(semanticCacheEmbeddingsBackendId)");
        Regex.IsMatch(api, @"@minValue\(1\)\s*@maxValue\(20\)[^\n]*\n[^\n]*\nparam semanticCacheScoreThresholdPercent int = 5").Should().BeTrue();
        Regex.IsMatch(api, @"@minValue\(60\)\s*@maxValue\(3600\)[^\n]*\n[^\n]*\nparam semanticCacheTtlSeconds int = 300|@minValue\(60\)\s*@maxValue\(3600\)\s*@description\([^\n]*\)\s*param semanticCacheTtlSeconds int = 300").Should().BeTrue();
        api.Should().Contain("string(regulatedRetailMode)");

        string main = File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "main.bicep"));
        main.Should().Contain("param apimSemanticCachePolicyEnabled bool = false");
        main.Should().Contain("semanticCachePolicyEnabled: semanticCacheEnabled && apimSemanticCachePolicyEnabled");
        main.Should().Contain("semanticCacheTtlSeconds: min(semanticCacheTtlSeconds, 3600)");
        string param = File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "main.bicepparam"));
        param.Should().Contain("AZURE_APIM_SEMANTIC_CACHE_POLICY_ENABLED', 'false'");

        string everything = api + main + param;
        everything.Should().NotContainEquivalentOf("redisenterprise/databases/listKeys");
        api.Should().NotContain("registerExternalCache").And.NotContain("Microsoft.ApiManagement/service/caches",
            "the external-cache registration stays out of band (#338)");
    }

    [Fact]
    public void Evidence_ParsesHitMissBypassUnavailable_AndRejectsAnythingElse()
    {
        GatewayCallEvidence Parse(string? v) => GatewayEvidenceHeaders.Parse(
            n => n == GatewayEvidenceHeaders.Cache ? v : n == GatewayEvidenceHeaders.RequestId ? "req" : null, 200);

        Parse("hit").Cache.Should().Be(GatewayOutcome.Hit);
        Parse("miss").Cache.Should().Be(GatewayOutcome.Miss);
        Parse("bypass").Cache.Should().Be(GatewayOutcome.Bypass);
        Parse("unavailable").Cache.Should().Be(GatewayOutcome.Unavailable);
        Parse("passed").Cache.Should().Be(GatewayOutcome.Unknown);
        Parse("<script>").Cache.Should().Be(GatewayOutcome.Unknown);
        Parse(null).Cache.Should().Be(GatewayOutcome.Unknown);
        GatewayEvidenceHeaders.AllNames.Should().Contain(GatewayEvidenceHeaders.CacheReason);
    }

    private static string Decode(string xml) => System.Net.WebUtility.HtmlDecode(xml);

    [GeneratedRegex("return \"([a-z-]+)\";")]
    private static partial Regex ReturnLiteralRegex();

    [GeneratedRegex(@"\^\[a-z0-9\]\[a-z0-9-\]\{0,62\}\$")]
    private static partial Regex TenantRegexRegex();

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("RetailPulse.slnx not found.");
    }

    private sealed class RenderedPolicy(string inbound, string outbound, string error)
    {
        public string InboundText { get; } = inbound;
        public string OutboundText { get; } = outbound;
        public string ErrorText { get; } = error;
        public XElement Inbound { get; } = XDocument.Parse("<r>" + inbound + "</r>").Root!;
        public XElement Outbound { get; } = XDocument.Parse("<r>" + outbound + "</r>").Root!;

        public string Constant(string name) =>
            Inbound.Descendants("set-variable").Single(v => (string?)v.Attribute("name") == name).Attribute("value")!.Value;

        public bool Regulated => Constant("rp-cache-regulated") == "true";

        public double Threshold => double.Parse(Inbound.Descendants("azure-openai-semantic-cache-lookup").Single().Attribute("score-threshold")!.Value,
            System.Globalization.CultureInfo.InvariantCulture);

        public int Ttl => int.Parse(Outbound.Descendants("azure-openai-semantic-cache-store").Single().Attribute("duration")!.Value);
    }

    private sealed record GatewayRequest(string Subscription, string TenantHeader, JsonObject Body, string Path);

    private sealed record BackendResult(int Status, string Text, bool OutputUnsafe = false, bool OutputChecked = true);

    private sealed record GatewayResponse(int Status, string Body, string? CacheEvidence, string? CacheReason, int BackendTokens);

    /// <summary>Mirrors the rendered policy pipeline; all tunables are read from the rendered XML.</summary>
    private sealed partial class GatewayHarness(RenderedPolicy policy)
    {
        public const int TokensPerCall = 120;

        public static readonly string[] ReasonOrder =
        [
            "regulated", "unknown-intent", "streaming", "tools", "low-confidence", "blank",
            "sensitive-intent", "mutation", "dynamic-intent", "no-tenant", "eligible",
        ];

        private static readonly HashSet<string> Stop =
            ["what", "is", "the", "your", "a", "an", "do", "does", "how", "can", "i", "you", "s", "are", "of", "to", "for", "about", "tell", "me", "please", "explain"];

        private readonly List<Entry> _entries = [];
        private DateTime _now = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

        public int BackendCalls { get; private set; }
        public int BackendTokens { get; private set; }
        public bool CacheAvailable { get; set; } = true;
        public BackendResult? NextBackend { get; set; }
        public int CacheEntries => _entries.Count;
        public string LastStoredBody => _entries.Count == 0 ? "" : _entries[^1].Body;

        public void Advance(TimeSpan by) => _now += by;

        public GatewayResponse Handle(GatewayRequest req)
        {
            string tenant = TenantRegex().IsMatch(req.TenantHeader.Trim().ToLowerInvariant()) ? req.TenantHeader.Trim().ToLowerInvariant() : "";
            string decision = Decide(req.Body, tenant);
            string? lookupFailure = null;

            if (decision == "eligible")
            {
                string partition = string.Join("\u0001", req.Subscription, tenant, req.Path);
                string prompt = LastUserText(req.Body);
                try
                {
                    Entry? hit = Lookup(partition, prompt);
                    if (hit is not null) return new GatewayResponse(200, hit.Body, "hit", null, 0);
                }
                catch (InvalidOperationException)
                {
                    lookupFailure = "cache-error";
                }

                BackendResult result = NextBackend ?? new BackendResult(200, $"ANSWER:{prompt.Trim().ToLowerInvariant()}");
                BackendCalls++;
                BackendTokens += TokensPerCall;

                if (result.Status == 200 && result.OutputUnsafe)
                    return new GatewayResponse(403, "blocked", null, null, TokensPerCall);
                if (lookupFailure is not null)
                    return new GatewayResponse(result.Status, result.Text, "unavailable", lookupFailure, TokensPerCall);
                if (result.Status == 200 && result.OutputChecked)
                {
                    _entries.Add(new Entry(partition, Embed(prompt), result.Text, _now.AddSeconds(policy.Ttl)));
                    return new GatewayResponse(200, result.Text, "miss", null, TokensPerCall);
                }

                return new GatewayResponse(result.Status, result.Text, "bypass", result.Status == 200 ? "unsafe" : "failed", TokensPerCall);
            }

            BackendResult passthrough = NextBackend ?? new BackendResult(200, "ANSWER");
            BackendCalls++;
            BackendTokens += TokensPerCall;
            return new GatewayResponse(passthrough.Status, passthrough.Text, "bypass", decision, TokensPerCall);
        }

        private Entry? Lookup(string partition, string prompt)
        {
            if (!CacheAvailable) throw new InvalidOperationException("cache down");
            Dictionary<string, double> q = Embed(prompt);
            return _entries.Where(e => e.Partition == partition && e.Expires > _now)
                .Select(e => (Entry: e, Distance: 1 - Cosine(q, e.Vector)))
                .Where(x => x.Distance <= policy.Threshold)
                .OrderBy(x => x.Distance).Select(x => x.Entry).FirstOrDefault();
        }

        private string Decide(JsonObject body, string tenant)
        {
            if (policy.Regulated) return "regulated";
            if (body["messages"] is not JsonArray msgs) return body["stream"]?.GetValue<bool>() == true ? "streaming" : "unknown-intent";
            if (body["stream"]?.GetValue<bool>() == true) return "streaming";
            if (body["tools"] is not null || body["functions"] is not null || body["tool_choice"] is not null || body["function_call"] is not null) return "tools";

            int users = 0;
            string text = "";
            foreach (JsonNode? m in msgs)
            {
                string role = m!["role"]!.GetValue<string>();
                if (role is "system" or "developer") continue;
                if (role != "user") return "low-confidence";
                users++;
                text = m["content"]!.GetValue<string>();
            }

            text = text.Trim();
            if (text.Length == 0) return "blank";
            if (users != 1 || text.Length < 12 || text.Length > 400) return "low-confidence";
            if (Regex.IsMatch(text, policy.Constant("rp-cache-deny-sensitive"))) return "sensitive-intent";
            if (Regex.IsMatch(text, policy.Constant("rp-cache-deny-mutation"))) return "mutation";
            if (Regex.IsMatch(text, policy.Constant("rp-cache-deny-dynamic"))) return "dynamic-intent";
            if (!Regex.IsMatch(text, policy.Constant("rp-cache-allow"))) return "unknown-intent";
            return tenant.Length == 0 ? "no-tenant" : "eligible";
        }

        private static string LastUserText(JsonObject body) =>
            body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();

        private static Dictionary<string, double> Embed(string text) =>
            WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => !Stop.Contains(w))
                .GroupBy(w => w).ToDictionary(g => g.Key, g => (double)g.Count());

        private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
        {
            double dot = a.Sum(kv => kv.Value * b.GetValueOrDefault(kv.Key));
            double na = Math.Sqrt(a.Values.Sum(v => v * v)), nb = Math.Sqrt(b.Values.Sum(v => v * v));
            return na == 0 || nb == 0 ? 0 : dot / (na * nb);
        }

        [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
        private static partial Regex TenantRegex();

        [GeneratedRegex("[a-z0-9]+")]
        private static partial Regex WordRegex();

        private sealed record Entry(string Partition, Dictionary<string, double> Vector, string Body, DateTime Expires);
    }
}
