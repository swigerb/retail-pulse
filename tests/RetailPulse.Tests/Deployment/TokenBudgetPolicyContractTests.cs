using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Offline contract for per-scope token budgets (#332): current llm-token-limit policy, counter keys derived only
/// from trusted APIM subscription/product context, a disabled-by-default tiny-TPM operator demo with a generation
/// based reset, and privacy-safe evidence that never fabricates consumed tokens on a pre-backend 429.
/// </summary>
public sealed partial class TokenBudgetPolicyContractTests
{
    private static readonly string Modules = Path.Combine(FindRepoRoot(), "infra", "modules");

    private static string Read(string name) => File.ReadAllText(Path.Combine(Modules, name));

    private static string Base => Read("apim-openai-policy.xml");

    private static string Normal => Read("apim-openai-budget-limit-normal.xml");

    private static string Demo => Read("apim-openai-budget-limit-demo.xml");

    private static string Models => Read("apim-openai-models-policy.xml");

    private static string Bicep => Read("apim-openai-api.bicep");

    private static string CounterKeys(string xml) =>
        string.Concat(CounterKeyRegex().Matches(xml).Select(m => m.Groups[1].Value));

    [GeneratedRegex("counter-key=\"([^\"]*)\"")]
    private static partial Regex CounterKeyRegex();

    [GeneratedRegex("estimate-prompt-tokens=\"true\"")]
    private static partial Regex EstimatePromptTokensRegex();

    [GeneratedRegex("rp-budget-demo-armed'[\\s\\S]*?value: '(\\w+)'")]
    private static partial Regex DemoArmedRegex();

    [GeneratedRegex("rp-budget-demo-generation'[\\s\\S]*?value: '(\\w+)'")]
    private static partial Regex DemoGenerationRegex();

    [GeneratedRegex("(primaryKey|subscriptionKey)\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveParameterRegex();

    [Fact]
    public void Fragments_AreWellFormedXml()
    {
        foreach (string fragment in new[] { Normal, Demo })
        {
            Action parse = () => XDocument.Parse("<r>" + fragment + "</r>");
            parse.Should().NotThrow();
        }
    }

    [Fact]
    public void NoInfraFile_UsesSupersededTokenLimitName()
    {
        foreach (string file in Directory.GetFiles(Modules).Where(f => f.EndsWith(".xml") || f.EndsWith(".bicep")))
        {
            File.ReadAllText(file).Should().NotContain("<azure-openai-token-limit", $"{Path.GetFileName(file)} must use llm-token-limit");
        }

        Normal.Should().Contain("<llm-token-limit");
        Demo.Should().Contain("<llm-token-limit");
    }

    [Fact]
    public void CounterKeys_DeriveOnlyFromTrustedSubscriptionContextAndNamedValues()
    {
        foreach (string fragment in new[] { Normal, Demo })
        {
            string keys = CounterKeys(fragment);
            keys.Should().Contain("context.Subscription.Id");
            keys.Should().NotContainEquivalentOf("Headers").And.NotContainEquivalentOf("Query").And.NotContainEquivalentOf("Body")
                .And.NotContainEquivalentOf("x-rp").And.NotContainEquivalentOf("Claims");
        }

        Base.Should().NotContain("GetValueOrDefault(&quot;x-rp-budget", "the scope is never read from a caller header");
    }

    [Fact]
    public void ScopeKind_ComesOnlyFromProductContext_AndSpoofHeadersAreStripped()
    {
        Base.Should().Contain("context.Product != null ? context.Product.Id");
        foreach (string kind in new[] { "store", "channel", "b2b" })
        {
            Base.Should().Contain($"rp-budget-{kind}-");
        }

        int inbound = Base.IndexOf("<inbound>", StringComparison.Ordinal);
        int limit = Base.IndexOf("{budget-limit}", StringComparison.Ordinal);
        foreach (string header in new[] { "x-rp-gw-budget-scope", "x-rp-gw-budget-mode", "x-rp-gw-retry-after", "x-rp-budget-scope", "x-rp-budget-demo" })
        {
            int strip = Base.IndexOf($"<set-header name=\"{header}\" exists-action=\"delete\"", inbound, StringComparison.Ordinal);
            strip.Should().BeGreaterThan(inbound).And.BeLessThan(limit, $"{header} must be stripped before the limiter");
        }
    }

    [Fact]
    public void SubscriptionKeys_AreNotForwardedToBackend_OrEmitted()
    {
        Base.Should().Contain("<set-header name=\"Ocp-Apim-Subscription-Key\" exists-action=\"delete\"");
        Base.Should().Contain("<set-header name=\"api-key\" exists-action=\"delete\"");
        Base.Should().Contain("<set-query-parameter name=\"api-key\" exists-action=\"delete\"");
        Models.Should().Contain("<set-header name=\"Ocp-Apim-Subscription-Key\" exists-action=\"delete\"");
        Models.Should().Contain("<set-header name=\"api-key\" exists-action=\"delete\"");
        Models.Should().Contain("<set-query-parameter name=\"api-key\" exists-action=\"delete\"");
        Base.Should().NotContain("context.Subscription.Key").And.NotContain("PrimaryKey");
        string outbound = Base[Base.IndexOf("<outbound>", StringComparison.Ordinal)..];
        outbound.Should().NotContain("Subscription.Id", "counter identifiers are never exposed in response headers");
    }

    [Fact]
    public void NormalLimit_IsProductionSafe_AndDemoLimitIsTinyStaticNamedValue()
    {
        Bicep.Should().Contain("param tokensPerMinute int = 80000");
        Normal.Should().Contain("tokens-per-minute=\"{tokens-per-minute}\"").And.Contain("estimate-prompt-tokens=\"true\"");
        Normal.Should().NotContain("{{");
        Demo.Should().Contain("tokens-per-minute=\"{{rp-budget-demo-tpm}}\"");
        Demo.Should().Contain("estimate-prompt-tokens=\"true\"");
        EstimatePromptTokensRegex().Count(Demo).Should().Be(2);
        Bicep.Should().Contain("@maxValue(500)").And.Contain("param tokenBudgetDemoTokensPerMinute int = 50");
        Demo.Should().NotContain("token-quota", "the TPM demo must not be confused with the long-period quota 403");
        Normal.Should().NotContain("token-quota");
    }

    [Fact]
    public void Demo_IsDisabledByDefault_ArmedByOperator_AndResetsThroughGeneration()
    {
        Bicep.Should().Contain("param tokenBudgetDemoEnabled bool = false");
        DemoArmedRegex().Match(Bicep).Groups[1].Value.Should().Be("false");
        DemoGenerationRegex().Match(Bicep).Groups[1].Value.Should().Be("1");
        Demo.Should().Contain("&quot;{{rp-budget-demo-armed}}&quot; == &quot;true&quot;");
        CounterKeys(Demo).Should().Contain("{{rp-budget-demo-generation}}");
        // Demo counter differs from the normal one so arming never drains the production budget.
        CounterKeys(Demo).Should().Contain("rp-budget-demo-");
        Demo.Should().Contain("<set-variable name=\"rp-budget-mode\" value=\"demo\"");
        Demo.Should().Contain("<set-variable name=\"rp-budget-mode\" value=\"normal\"");
    }

    [Fact]
    public void ThrottleEvidence_HasNumericRetryAndRemaining_AndNeverFabricatesConsumed()
    {
        string onError = Base[Base.IndexOf("<on-error>", StringComparison.Ordinal)..];
        onError.Should().Contain("context.LastError.Source == &quot;llm-token-limit&quot;");
        onError.Should().Contain("context.LastError.Reason == &quot;RateLimitExceeded&quot;")
            .And.Contain("context.Response.StatusCode == 429")
            .And.Contain("context.Response.Headers.GetValueOrDefault(&quot;Retry-After&quot;, &quot;&quot;)");
        onError.Should().Contain("x-rp-gw-retry-after").And.Contain("^[0-9]{1,6}$");
        onError.Should().Contain("x-rp-gw-tokens-remaining").And.Contain("^[0-9]{1,12}$")
            .And.Contain("<value>0</value>", "a verified pre-backend limiter rejection has no remaining admission budget");
        onError.Should().NotContain("x-rp-gw-tokens-consumed").And.NotContain("x-tokens-consumed");
        Normal.Should().Contain("retry-after-variable-name=\"rp-budget-retry-after\"");
        Normal.Should().Contain("remaining-tokens-variable-name=\"rp-tokens-remaining\"");
        string outbound = Base[Base.IndexOf("<outbound>", StringComparison.Ordinal)..Base.IndexOf("<on-error>", StringComparison.Ordinal)];
        outbound.Should().Contain("x-rp-gw-budget-scope").And.Contain("x-rp-gw-budget-mode");
    }

    [Fact]
    public void LiveProbe_UsesTheDeployedModelsTokenParameter()
    {
        string probe = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "probe-token-budget.cjs"));
        probe.Should().Contain("max_completion_tokens").And.NotContain("max_tokens:");
    }

    [Fact]
    public void Iac_ScopesAreIdempotent_AndKeysAreNeverInSourceOrOutputs()
    {
        Bicep.Should().Contain("Microsoft.ApiManagement/service/products@").And.Contain("Microsoft.ApiManagement/service/products/apis@");
        int start = Bicep.IndexOf("resource budgetSubscriptions", StringComparison.Ordinal);
        string block = Bicep[start..Bicep.IndexOf("output budgetScopeSubscriptionNames", StringComparison.Ordinal)];
        block.Should().NotContainEquivalentOf("primaryKey").And.NotContainEquivalentOf("secondaryKey").And.NotContain("listSecrets");
        Bicep.Split('\n').Count(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Contains("listSecrets(", StringComparison.Ordinal))
            .Should().Be(1, "the single pre-existing secure subscription key output is the only listSecrets use");
        SensitiveParameterRegex().Count(File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "main.bicepparam"))).Should().Be(0);
    }

    [Fact]
    public void CompiledArm_NormalDefaultsAndGatedDemo()
    {
        using var arm = new CompiledArmDeploymentGraphTests();
        JsonElement? compiled = arm.CompileForBudgetTests();
        if (compiled is null)
        {
            return;
        }

        JsonElement tpl = compiled.Value.GetProperty("resources").GetProperty("apimOpenAiApi").GetProperty("properties").GetProperty("template");
        tpl.GetProperty("parameters").GetProperty("tokensPerMinute").GetProperty("defaultValue").GetInt32().Should().Be(80000);
        tpl.GetProperty("parameters").GetProperty("tokenBudgetDemoEnabled").GetProperty("defaultValue").GetBoolean().Should().BeFalse();
        JsonElement demoTpm = tpl.GetProperty("parameters").GetProperty("tokenBudgetDemoTokensPerMinute");
        demoTpm.GetProperty("maxValue").GetInt32().Should().BeLessThanOrEqualTo(500);
        foreach (string nv in new[] { "budgetDemoArmed", "budgetDemoTpm", "budgetDemoGeneration" })
        {
            tpl.GetProperty("resources").GetProperty(nv).GetProperty("condition").GetString().Should().Contain("tokenBudgetDemoEnabled");
        }

        string raw = tpl.GetRawText();
        raw.Should().Contain("llm-token-limit").And.NotContain("<azure-openai-token-limit");
        tpl.GetProperty("resources").GetProperty("budgetSubscriptions").GetRawText().Should().NotContainEquivalentOf("primaryKey");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("RetailPulse.slnx not found");
    }
}
