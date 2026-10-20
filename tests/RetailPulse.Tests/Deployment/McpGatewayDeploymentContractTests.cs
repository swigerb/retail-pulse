using System.Text.RegularExpressions;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// ADR 017 / issue #335 static contract for the governed APIM MCP runtime. Inspects repo files
/// only; never touches Azure. Pins: exactly four read-only tools, GET-only operations, no wildcard
/// operations, quotas, managed-identity backend auth, no static key, no payload logging.
/// </summary>
public sealed partial class McpGatewayDeploymentContractTests
{
    private static readonly string Root = FindRepoRoot();

    private static readonly string[] ApprovedTools =
        ["get_inventory", "get_stockout_risk", "get_supply_disruptions", "get_demand_forecast"];

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string McpBicep => Read("infra", "modules", "apim-mcp-api.bicep");
    private static string McpPolicy => Read("infra", "modules", "apim-mcp-policy.xml");
    private static string BackingPolicy => Read("infra", "modules", "apim-mcp-backing-policy.xml");

    [GeneratedRegex(@"resource\s+\w+\s+'Microsoft\.ApiManagement/service/apis/tools@[^']+'\s*=\s*\{\s*parent:\s*mcpServer\s*name:\s*'([^']+)'")]
    private static partial Regex ToolRegex();

    [GeneratedRegex(@"resource\s+\w+\s+'Microsoft\.ApiManagement/service/apis/operations@[^']+'\s*=\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline)]
    private static partial Regex OperationRegex();

    [Fact]
    public void McpServer_IsMcpTypeWithSubscriptionRequired()
    {
        McpBicep.Should().Contain("type: 'mcp'");
        McpBicep.Should().Contain("subscriptionRequired: true");
        McpBicep.Should().Contain("2025-09-01-preview", "MCP server resources require that API version or later");
    }

    [Fact]
    public void ToolsList_IsExactlyTheFourApprovedTools()
    {
        string[] tools = [.. ToolRegex().Matches(McpBicep).Select(m => m.Groups[1].Value)];
        tools.Should().BeEquivalentTo(ApprovedTools);
        tools.Should().HaveCount(4);
    }

    [Fact]
    public void Operations_AreGetOnly_Explicit_AndNotWildcard()
    {
        MatchCollection ops = OperationRegex().Matches(McpBicep);
        ops.Should().HaveCount(4);
        foreach (Match op in ops)
        {
            string body = op.Groups["body"].Value;
            body.Should().Contain("method: 'GET'");
            body.Should().NotMatchRegex("method: '(POST|PUT|PATCH|DELETE)'");
            body.Should().NotContain("*");
            ApprovedTools.Should().Contain(MyRegex().Match(body).Groups[1].Value);
        }

        McpBicep.Should().NotContainAny("method: 'POST'", "method: 'PUT'", "method: 'PATCH'", "method: 'DELETE'");
    }

    [Fact]
    public void BackingApi_IsNotBoundToAnyProduct_SoItIsNotCallableAsRest()
    {
        Regex.Matches(McpBicep, @"service/products/apis@").Should().HaveCount(1);
        Regex.IsMatch(McpBicep, @"service/products/apis@[^']+'\s*=\s*\{[^}]*name:\s*mcpServerId").Should().BeTrue();
        BackingPolicy.Should().Contain("405").And.Contain("context.Request.Method != &quot;GET&quot;");
    }

    [Fact]
    public void Policy_AuthenticatesCaller_ThenUsesManagedIdentityForBackend()
    {
        string policy = McpPolicy;
        policy.Should().Contain("<validate-jwt");
        policy.Should().Contain("{{mcpGatewayAudience}}").And.Contain("{{tenantId}}");
        policy.Should().Contain("RetailPulse.Mcp.Invoke");
        policy.Should().Contain("require-expiration-time=\"true\"").And.Contain("require-signed-tokens=\"true\"");
        policy.Should().Contain("<authentication-managed-identity resource=\"{{retailPulseApiAudience}}\"");
        policy.Should().Contain("base-url=\"https://{{retailPulseApiFqdn}}\"");
        policy.Should().Contain("<forward-request timeout=\"30\"");

        int validate = policy.IndexOf("<validate-jwt", StringComparison.Ordinal);
        int strip = policy.IndexOf("name=\"Authorization\" exists-action=\"delete\"", StringComparison.Ordinal);
        int mi = policy.IndexOf("<authentication-managed-identity", StringComparison.Ordinal);
        validate.Should().BeGreaterThan(-1);
        strip.Should().BeGreaterThan(validate, "the caller header is stripped only after it was validated");
        mi.Should().BeGreaterThan(strip, "the managed-identity token is set after the caller header is removed");
    }

    [Fact]
    public void Policy_HasQuotasAllowListAndNeverReadsResponseBody()
    {
        McpPolicy.Should().Contain("<rate-limit-by-key calls=\"30\" renewal-period=\"60\"");
        McpPolicy.Should().Contain("<quota-by-key calls=\"2000\" bandwidth=\"20480\" renewal-period=\"86400\"");
        McpPolicy.Should().Contain("context.Subscription.Id");
        foreach (string tool in ApprovedTools)
        {
            McpPolicy.Should().Contain($"&quot;{tool}&quot;");
        }

        McpPolicy.Should().NotContain("context.Response.Body", "MCP server policies must not buffer the response");
        BackingPolicy.Should().NotContain("context.Response.Body");
    }

    [Fact]
    public void Telemetry_IdentifiesServerToolScopeOutcomeLatency_WithoutPayloads()
    {
        McpPolicy.Should().Contain("<emit-metric");
        foreach (string dim in new[] { "mcp_server", "tool", "caller_scope", "outcome", "subscription" })
        {
            McpPolicy.Should().Contain($"name=\"{dim}\"");
        }

        McpPolicy.Should().Contain("context.Elapsed");
        Regex.Matches(McpBicep, @"body:\s*\{\s*bytes:\s*0\s*\}").Should().HaveCount(4, "no request/response bodies are logged");
        McpBicep.Should().Contain("headers: []");
        McpBicep.Should().Contain("service/apis/diagnostics@");
    }

    [Fact]
    public void NoStaticMcpKeyOrSecretAnywhereInTheGatewayPath()
    {
        string main = Read("infra", "main.bicep");
        string mcpModuleCall = main[main.IndexOf("module apimMcpApi", StringComparison.Ordinal)..];

        foreach (string text in new[] { McpBicep, McpPolicy, BackingPolicy, mcpModuleCall })
        {
            text.Should().NotContainEquivalentOf("x-api-key");
            text.Should().NotContainEquivalentOf("mcpApiKey");
            text.Should().NotContainEquivalentOf("McpServer__ApiKey");
            text.Should().NotContain("listSecrets");
        }

        Regex.Matches(McpBicep, @"namedValues@").Should().HaveCount(4);
        Regex.Matches(McpBicep, @"secret:\s*false").Should().HaveCount(4);
        McpBicep.Should().NotContain("secret: true");
        Regex.Matches(mcpModuleCall, @"^output\s+(\w+)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Should().OnlyContain(n => !n.Contains("KEY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InternalMcpServer_StaysInternalAndKeepsItsKey()
    {
        string apps = Read("infra", "modules", "container-apps.bicep");
        apps.Should().Contain("external: false");
        apps.Should().Contain("mcpApiKeySecretName");
        McpBicep.Should().NotContainEquivalentOf("mcpServerUrl").And.NotContainEquivalentOf("ca-retailpulse-mcp");
    }

    [Fact]
    public void ApiAppOnlyConfig_FailsClosed_UntilGatewayClientIdIsKnown()
    {
        string apps = Read("infra", "modules", "container-apps.bicep");
        apps.Should().Contain("param mcpGatewayClientId string = ''");
        apps.Should().Contain("MicrosoftEntra__AllowedAppClientIds__1");
        apps.Should().NotContain("McpGateway__AllowedOperations__", "the shared app-only list is replaced by per-client lists");
        apps.Should().Contain("McpGateway__Clients__0__ClientId").And.Contain("McpGateway__Clients__0__Operations__");
        apps.Should().Contain("McpGateway__Clients__1__ClientId").And.Contain("McpGateway__Clients__1__Operations__");
        apps.Should().Contain("mcpGatewayConfigured").And.Contain("mcpAllowedOperations[i]");
        apps.Should().Contain("'POST /api/chat'");
        foreach (string op in new[] { "GET /api/supply/inventory", "GET /api/stores/stockout-risks", "GET /api/supply/disruptions", "GET /api/demand/forecast" })
        {
            apps.Should().Contain($"'{op}'");
        }
    }

    [Theory]
    [InlineData("apim-mcp-policy.xml")]
    [InlineData("apim-mcp-backing-policy.xml")]
    public void PolicyXml_IsWellFormed_WithEscapedAttributeExpressions(string file)
    {
        string xml = Read("infra", "modules", file);
        Action parse = () => System.Xml.Linq.XDocument.Parse(xml);
        parse.Should().NotThrow();

        var doc = System.Xml.Linq.XDocument.Parse(xml);
        string[] conditions = [.. doc.Descendants("when").Select(w => (string)w.Attribute("condition")!)];
        conditions.Should().NotBeEmpty().And.OnlyContain(c => c.StartsWith("@(") && c.Contains('"'));
    }

    [Fact]
    public void McpPolicy_LogicalAndsAndOperationCheckSurviveParsing()
    {
        var doc = System.Xml.Linq.XDocument.Parse(McpPolicy);
        string[] conditions = [.. doc.Descendants("when").Select(w => (string)w.Attribute("condition")!)];
        conditions.Should().Contain(c => c.Contains("&&") && c.Contains("\"DELETE\""));
        string operationGuard = conditions.Single(c => c.Contains("context.Operation"));
        foreach (string tool in ApprovedTools)
        {
            operationGuard.Should().Contain($"\"{tool}\"");
        }
    }

    [Fact]
    public void IdentityScript_AppliesAndVerifiesContainerAppConfig_OnFirstProvision()
    {
        string script = Read("scripts", "Setup-McpGatewayIdentity.ps1");
        script.Should().Contain("az containerapp update").And.Contain("--set-env-vars");
        script.Should().Contain("McpGateway__Clients__0__ClientId").And.Contain("McpGateway__Clients__0__Operations__");
        script.Should().Contain("MicrosoftEntra__AllowedAppClientIds__1").And.Contain("MicrosoftEntra__AllowAppOnlyTokens");
        script.Should().Contain("az containerapp show").And.Contain("was not applied");
        script.IndexOf("az containerapp update", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("az containerapp show", StringComparison.Ordinal));
        foreach (string op in new[] { "GET /api/supply/inventory", "GET /api/stores/stockout-risks", "GET /api/supply/disruptions", "GET /api/demand/forecast" })
        {
            script.Should().Contain($"'{op}'");
        }

        script.Should().NotContain("POST /api/chat", "the APIM identity must never be granted the bot route");
    }

    [Fact]
    public void IdentityScript_RetriesFreshGatewayServicePrincipalFiveTimes()
    {
        string script = Read("scripts", "Setup-McpGatewayIdentity.ps1");
        int create = script.IndexOf("-> creating gateway service principal", StringComparison.Ordinal);
        create.Should().BeGreaterThan(0);
        string afterCreate = script[create..script.IndexOf("# 2. Retail Pulse API service principal", StringComparison.Ordinal)];
        afterCreate.Should().Contain("-RetryNotFound").And.Contain("servicePrincipals/$($created.id)");
        script.Should().Contain("$notFoundAttempts -lt 5").And.Contain("(attempt $notFoundAttempts/5)");
    }

    [Fact]
    public void OldUnsafeAttachOn_IsRetired() => File.Exists(Path.Combine(Root, "deploy", "apim-ai-gateway", "mcp-api.bicep")).Should().BeFalse();

    [Theory]
    [InlineData("azd-hooks", "postprovision.ps1")]
    [InlineData("azd-hooks", "postprovision.sh")]
    public void PostprovisionHooks_InvokeIdentitySetupFailClosed(string dir, string file)
    {
        string hook = Read(dir, file);
        hook.Should().Contain("Setup-McpGatewayIdentity.ps1");
        hook.LastIndexOf("Setup-McpGatewayIdentity.ps1", StringComparison.Ordinal)
            .Should().BeLessThan(hook.LastIndexOf("Verify-ApimAiGateway.ps1", StringComparison.Ordinal));
    }

    [Fact]
    public void IdentityScript_IsIdempotentSecretFreeAndNamesRequiredPermissions()
    {
        string script = Read("scripts", "Setup-McpGatewayIdentity.ps1");
        script.Should().Contain("Application.ReadWrite.All").And.Contain("AppRoleAssignment.ReadWrite.All");
        script.Should().Contain("RetailPulse.Mcp.Invoke").And.Contain("RetailPulse.User");
        script.Should().Contain("api://retailpulse-mcp-gateway");
        script.Should().Contain("AZURE_MCP_GATEWAY_APIM_CLIENT_ID");
        script.Should().Contain("allowedMemberTypes").And.Contain("Application");
        script.Should().Contain("already present");
        script.Should().NotContainEquivalentOf("addPassword").And.NotContainEquivalentOf("client_secret").And.NotContainEquivalentOf("passwordCredentials");
        script.Should().Contain("-lt 5").And.Contain("Start-Sleep -Seconds 10").And.Contain("-lt 3");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (RetailPulse.slnx).");
    }

    [GeneratedRegex(@"name: '([^']+)'")]
    private static partial Regex MyRegex();
}
