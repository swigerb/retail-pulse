using System.Diagnostics;
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
    public void McpApiChildren_AreSerialized_ToAvoidApimEtagWriteRaces()
    {
        McpBicep.Should().Contain("resource toolInventory");
        McpBicep.Should().Contain("dependsOn: [toolInventory]");
        McpBicep.Should().Contain("dependsOn: [toolStockout]");
        McpBicep.Should().Contain("dependsOn: [toolDisruptions]");
        McpBicep.Should().Contain("dependsOn: [toolForecast, nvApiFqdn");
        McpBicep.Should().Contain("dependsOn: [mcpPolicy]");
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
        ProductApisRegex().Matches(McpBicep).Should().HaveCount(1);
        ProductApiBindingRegex().IsMatch(McpBicep).Should().BeTrue();
        BackingPolicy.Should().Contain("405").And.Contain("context.Request.Method != &quot;GET&quot;");
    }

    [Fact]
    public void Policy_AuthenticatesCaller_ThenUsesManagedIdentityForBackend()
    {
        string policy = McpPolicy;
        string main = Read("infra", "main.bicep");
        string module = McpBicep;
        string parameters = Read("infra", "main.bicepparam");
        policy.Should().Contain("<validate-jwt");
        policy.Should().Contain("{{mcpGatewayAudience}}").And.Contain("{{tenantId}}");
        main.Should().Contain("param mcpGatewayAudience string").And.Contain("@minLength(36)").And.Contain("@maxLength(36)");
        module.Should().Contain("Exact application (client) ID GUID in MCP caller v2 access-token aud claims");
        parameters.Should().Contain("AZURE_MCP_GATEWAY_AUDIENCE', ''");
        parameters.Should().NotContain("AZURE_MCP_GATEWAY_AUDIENCE', 'api://",
            "the identifier URI must never fall back into the v2 token audience parameter");
        policy.Should().Contain("RetailPulse.Mcp.Invoke");
        policy.Should().Contain("require-expiration-time=\"true\"").And.Contain("require-signed-tokens=\"true\"");
        policy.Should().Contain("<authentication-managed-identity resource=\"{{retailPulseApiAudience}}\"");
        policy.Should().Contain("base-url=\"https://{{retailPulseApiFqdn}}\"");
        policy.Should().Contain("<forward-request timeout=\"30\"");

        int validate = policy.IndexOf("<validate-jwt", StringComparison.Ordinal);
        int audiences = policy.IndexOf("<audiences>", validate, StringComparison.Ordinal);
        int issuers = policy.IndexOf("<issuers>", validate, StringComparison.Ordinal);
        int requiredClaims = policy.IndexOf("<required-claims>", validate, StringComparison.Ordinal);
        int strip = policy.IndexOf("name=\"Authorization\" exists-action=\"delete\"", StringComparison.Ordinal);
        int mi = policy.IndexOf("<authentication-managed-identity", StringComparison.Ordinal);
        validate.Should().BeGreaterThan(-1);
        audiences.Should().BeGreaterThan(validate);
        issuers.Should().BeGreaterThan(audiences,
            "APIM validate-jwt requires audiences before issuers");
        requiredClaims.Should().BeGreaterThan(issuers,
            "APIM validate-jwt requires required-claims after issuers");
        strip.Should().BeGreaterThan(validate, "the caller header is stripped only after it was validated");
        mi.Should().BeGreaterThan(strip, "the managed-identity token is set after the caller header is removed");
    }

    [Fact]
    public void Policy_HasQuotasAndNeverReadsResponseBody()
    {
        McpPolicy.Should().Contain("<rate-limit-by-key calls=\"30\" renewal-period=\"60\"");
        McpPolicy.Should().Contain("<quota-by-key calls=\"2000\" bandwidth=\"20480\" renewal-period=\"86400\"");
        McpPolicy.Should().Contain("context.Subscription.Id");

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
        ZeroBodyBytesRegex().Matches(McpBicep).Should().HaveCount(4, "no request/response bodies are logged");
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

        NamedValuesRegex().Matches(McpBicep).Should().HaveCount(4);
        NonSecretRegex().Matches(McpBicep).Should().HaveCount(4);
        McpBicep.Should().NotContain("secret: true");
        OutputRegex().Matches(mcpModuleCall)
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
    public void McpPolicy_AllowsNativeTransportRequestsBeforeToolDispatch()
    {
        var doc = System.Xml.Linq.XDocument.Parse(McpPolicy);
        string[] conditions = [.. doc.Descendants("when").Select(w => (string)w.Attribute("condition")!)];
        conditions.Should().Contain(c => c.Contains("&&") && c.Contains("\"DELETE\""));
        conditions.Should().NotContain(c => c.Contains("context.Operation"),
            "the MCP-scope policy also evaluates initialize and other native transport requests, whose operation id is not a backing REST operation");
        McpPolicy.Should().Contain("native MCP").And.Contain("app-only route allow-list");
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
    [InlineData("azd-hooks", "preprovision.ps1")]
    [InlineData("azd-hooks", "preprovision.sh")]
    public void PreprovisionHooks_BootstrapExactGatewayAudienceBeforeArm(string dir, string file)
    {
        string hook = Read(dir, file);
        hook.Should().Contain("Setup-McpGatewayIdentity.ps1").And.Contain("-BootstrapOnly");
        hook.Should().Contain("client-id", "the hook must document why the resource URI cannot be used as the v2 aud");
        hook.Should().Contain("azd env get-value AZURE_MCP_GATEWAY_AUDIENCE");
        hook.Should().Contain("does not hot-reload",
            "the first bootstrap run must stop before ARM instead of deploying with a stale URI/empty audience");
    }

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
        script.Should().Contain("requestedAccessTokenVersion").And.Contain("= 2");
        script.Should().Contain("AZURE_MCP_GATEWAY_RESOURCE_IDENTIFIER");
        script.Should().Contain("AZURE_MCP_GATEWAY_AUDIENCE $gatewayAudience");
        script.Should().Contain("AZURE_MCP_GATEWAY_APIM_CLIENT_ID");
        script.Should().Contain("allowedMemberTypes").And.Contain("Application");
        script.Should().Contain("already present");
        script.Should().NotContainEquivalentOf("addPassword").And.NotContainEquivalentOf("client_secret").And.NotContainEquivalentOf("passwordCredentials");
        script.Should().Contain("-lt 5").And.Contain("Start-Sleep -Seconds 10").And.Contain("-lt 3");
    }

    [Fact]
    public void IdentityScript_CreatePath_RequestsV2AndEmitsDistinctResourceAndAudience()
    {
        if (!CommandExists("pwsh")) return;
        string output = RunBootstrapHarness(existingApp: false);

        output.Should().Contain("APPLICATION_POST=");
        output.Should().Contain("\"identifierUris\":[\"api://retailpulse-mcp-gateway\"]");
        output.Should().Contain("\"requestedAccessTokenVersion\":2");
        output.Should().Contain("AZD env set AZURE_MCP_GATEWAY_RESOURCE_IDENTIFIER api://retailpulse-mcp-gateway");
        output.Should().Contain("AZD env set AZURE_MCP_GATEWAY_AUDIENCE 11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public void IdentityScript_ExistingPath_RepairsV1WithoutDuplicatingAppOrRole()
    {
        if (!CommandExists("pwsh")) return;
        string output = RunBootstrapHarness(existingApp: true);

        output.Should().NotContain("APPLICATION_POST=");
        output.Should().Contain("APPLICATION_PATCH=");
        output.Should().Contain("\"requestedAccessTokenVersion\":2");
        output.Should().NotContain("\"appRoles\"", "the existing application-only invoke role must not be duplicated");
        output.Should().Contain("AZD env set AZURE_MCP_GATEWAY_AUDIENCE 11111111-1111-1111-1111-111111111111");
    }

    private static string RunBootstrapHarness(bool existingApp)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"retail-pulse-mcp-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        string log = Path.Combine(temp, "calls.log");
        string harness = Path.Combine(temp, "harness.ps1");
        string setup = Path.Combine(Root, "scripts", "Setup-McpGatewayIdentity.ps1");

        string content = $$"""
            $ErrorActionPreference = 'Stop'
            $log = '{{log.Replace("'", "''")}}'
            $existingApp = {{(existingApp ? "$true" : "$false")}}
            if ($existingApp) {
                $existingResponse = @{
                    value = @(@{
                        id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
                        appId = '11111111-1111-1111-1111-111111111111'
                        api = @{ requestedAccessTokenVersion = 1 }
                        appRoles = @(@{
                            id = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
                            allowedMemberTypes = @('Application')
                            value = 'RetailPulse.Mcp.Invoke'
                            isEnabled = $true
                        })
                    })
                } | ConvertTo-Json -Depth 10 -Compress
                $spResponse = @{
                    value = @(@{ id = 'cccccccc-cccc-cccc-cccc-cccccccccccc' })
                } | ConvertTo-Json -Depth 10 -Compress
            }
            else {
                $existingResponse = @{ value = @() } | ConvertTo-Json -Compress
                $spResponse = @{ value = @() } | ConvertTo-Json -Compress
            }
            function global:az {
                $methodIndex = [Array]::IndexOf($args, '--method')
                $urlIndex = [Array]::IndexOf($args, '--url')
                $method = $args[$methodIndex + 1]
                $url = $args[$urlIndex + 1]
                $bodyIndex = [Array]::IndexOf($args, '--body')
                if ($bodyIndex -ge 0) {
                    $bodyPath = $args[$bodyIndex + 1].Substring(1)
                    $body = Get-Content -Raw $bodyPath
                }
                if ($url -match '/applications\?') {
                    $global:LASTEXITCODE = 0
                    $existingResponse
                    return
                }
                if ($method -eq 'POST' -and $url -match '/applications$') {
                    Add-Content -Path $log -Value "APPLICATION_POST=$body"
                    $global:LASTEXITCODE = 0
                    @{
                        id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
                        appId = '11111111-1111-1111-1111-111111111111'
                    } | ConvertTo-Json -Compress
                    return
                }
                if ($method -eq 'PATCH' -and $url -match '/applications/') {
                    Add-Content -Path $log -Value "APPLICATION_PATCH=$body"
                    $global:LASTEXITCODE = 0
                    return
                }
                if ($method -eq 'GET' -and $url -match '/servicePrincipals\?') {
                    $global:LASTEXITCODE = 0
                    $spResponse
                    return
                }
                if ($method -eq 'POST' -and $url -match '/servicePrincipals$') {
                    $global:LASTEXITCODE = 0
                    @{ id = 'cccccccc-cccc-cccc-cccc-cccccccccccc' } | ConvertTo-Json -Compress
                    return
                }
                if ($method -eq 'GET' -and $url -match '/servicePrincipals/cccccccc-cccc-cccc-cccc-cccccccccccc') {
                    $global:LASTEXITCODE = 0
                    @{ id = 'cccccccc-cccc-cccc-cccc-cccccccccccc' } | ConvertTo-Json -Compress
                    return
                }
                throw "Unexpected az call: $method $url"
            }
            function global:azd {
                Add-Content -Path $log -Value "AZD $($args -join ' ')"
                $global:LASTEXITCODE = 0
            }
            . '{{setup.Replace("'", "''")}}' -BootstrapOnly
            Get-Content -Raw $log
            """;

        try
        {
            File.WriteAllText(harness, content);
            using Process process = Process.Start(new ProcessStartInfo("pwsh")
            {
                ArgumentList = { "-NoProfile", "-File", harness },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, $"bootstrap harness failed: {stderr}\n{stdout}");
            return stdout;
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static bool CommandExists(string command)
    {
        string executable = OperatingSystem.IsWindows() ? $"{command}.exe" : command;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, executable)));
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

    [GeneratedRegex(@"service/products/apis@")]
    private static partial Regex ProductApisRegex();

    [GeneratedRegex(@"service/products/apis@[^']+'\s*=\s*\{[^}]*name:\s*mcpServerId")]
    private static partial Regex ProductApiBindingRegex();

    [GeneratedRegex(@"body:\s*\{\s*bytes:\s*0\s*\}")]
    private static partial Regex ZeroBodyBytesRegex();

    [GeneratedRegex(@"namedValues@")]
    private static partial Regex NamedValuesRegex();

    [GeneratedRegex(@"secret:\s*false")]
    private static partial Regex NonSecretRegex();

    [GeneratedRegex(@"^output\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex OutputRegex();

    [GeneratedRegex(@"name: '([^']+)'")]
    private static partial Regex MyRegex();
}
