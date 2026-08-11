using System.Text.RegularExpressions;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Static guardrails for the APIM AI Gateway module contract. These tests only
/// inspect repo files (the APIM Bicep modules, policy XML, and role-assignment
/// module) — they never invoke <c>az</c>, <c>azd</c>, or touch Azure.
///
/// The intent is to make the AI Gateway a first-class, non-optional invariant of
/// the deployment: identity, backend auth, subscription enforcement, policy
/// primitives (token-limit + emit-token-metric), diagnostics (GatewayLlmLogs,
/// API-level Application Insights with <c>metrics: true</c>, LLM diag), RBAC
/// (Cognitive Services OpenAI User), and the endpoint output contract (no doubled
/// <c>/openai</c> — the exact defect from incident #55) all live here as
/// regression tests.
/// </summary>
public sealed partial class ApimGatewayContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [GeneratedRegex(@"@description\('APIM inference endpoint[^']*'\)\s*param\s+apimInferenceEndpoint\s+(?<type>[A-Za-z]+)(?<rest>[^\r\n]*)")]
    private static partial Regex ApimInferenceEndpointParamRegex();

    private static string ApimBicep => File.ReadAllText(
        Path.Combine(RepoRoot, "infra", "modules", "apim.bicep"));

    private static string ApimOpenAiApiBicep => File.ReadAllText(
        Path.Combine(RepoRoot, "infra", "modules", "apim-openai-api.bicep"));

    private static string ApimPolicyXml => File.ReadAllText(
        Path.Combine(RepoRoot, "infra", "modules", "apim-openai-policy.xml"));

    private static string ApimRoleAssignmentBicep => File.ReadAllText(
        Path.Combine(RepoRoot, "infra", "modules", "apim-openai-role-assignment.bicep"));

    private static string ContainerAppsBicep => File.ReadAllText(
        Path.Combine(RepoRoot, "infra", "modules", "container-apps.bicep"));

    private static string MainBicep => File.ReadAllText(
        Path.Combine(RepoRoot, "infra", "main.bicep"));

    // ── APIM instance ────────────────────────────────────────────────────────

    [Fact]
    public void ApimInstance_UsesSystemAssignedIdentityForBackendManagedIdentityAuth()
    {
        // The APIM policy authenticates to AOAI using APIM's system-assigned
        // identity (managed-identity flow). Without SystemAssigned identity the
        // policy silently 401s at request time.
        ApimBicep.Should().MatchRegex(
            @"identity:\s*{\s*type:\s*'SystemAssigned'",
            "apim.bicep must declare a system-assigned managed identity so the AI Gateway can auth to AOAI via MI");
    }

    [Fact]
    public void ApimInstance_ExposesPrincipalIdOutput_ForRbacGrant()
    {
        ApimBicep.Should().MatchRegex(
            @"output\s+apimPrincipalId\s+string\s*=\s*apim\.identity\.principalId",
            "apim.bicep must output the APIM principal id so downstream modules can grant Cognitive Services OpenAI User");
    }

    [Fact]
    public void ApimInstance_WiresDiagnosticSettingsToLogAnalytics_IncludingGatewayLlmLogs()
    {
        // GatewayLlmLogs is the log category that carries prompt/completion payloads
        // captured by the API-level largeLanguageModel diagnostic; it must be
        // enabled or the AI Gateway telemetry pipeline goes dark for LLM traffic.
        ApimBicep.Should().Contain("workspaceId: logAnalyticsWorkspaceId",
            "apim.bicep must send APIM diagnostics to the shared Log Analytics workspace");
        ApimBicep.Should().Contain("'GatewayLogs'",
            "apim.bicep diagnostics must include the standard gateway request logs");
        ApimBicep.Should().Contain("'GatewayLlmLogs'",
            "apim.bicep diagnostics must include GatewayLlmLogs (LLM request/response payloads)");
    }

    [Fact]
    public void ApimInstance_DeclaresBothLoggersUsedByApiLevelDiagnostics()
    {
        // The API-level applicationinsights and azuremonitor diagnostics
        // (apim-openai-api.bicep) reference these two loggers as `existing`. If
        // apim.bicep drops them, the API-level diag deploy fails with
        // "resource not found" and telemetry silently goes dark.
        ApimBicep.Should().MatchRegex(
            @"resource\s+appInsightsLogger\s+'Microsoft\.ApiManagement/service/loggers[^']*'",
            "apim.bicep must declare the appinsights-logger resource");
        ApimBicep.Should().Contain("name: 'appinsights-logger'",
            "the applicationInsights logger must be named 'appinsights-logger'");
        ApimBicep.Should().MatchRegex(
            @"resource\s+azureMonitorLogger\s+'Microsoft\.ApiManagement/service/loggers[^']*'",
            "apim.bicep must declare the azuremonitor logger resource");
        ApimBicep.Should().Contain("name: 'azuremonitor'",
            "the Azure Monitor logger must be named 'azuremonitor'");
    }

    [Fact]
    public void ApimInstance_UsesConnectionStringForAppInsightsLogger_Secretless()
    {
        // The instrumentationKey path pulls from a hidden NamedValue; using the
        // connection string keeps the wiring resource-scoped and secretless
        // (§decision 2026-08-05 / MERGED APIM decision).
        ApimBicep.Should().Contain("connectionString: appInsightsConnectionString",
            "the appinsights-logger must use the App Insights connection string (secretless), not instrumentationKey");
    }

    [Fact]
    public void ApimInstance_WiresInstanceLevelApplicationInsightsDiagnostic_ForTokenMetrics()
    {
        // Instance-level applicationinsights diagnostic is what actually routes
        // `azure-openai-emit-token-metric` custom metrics into App Insights
        // customMetrics. API-level alone is not enough.
        ApimBicep.Should().Contain(
            "name: 'applicationinsights'",
            "apim.bicep must declare an instance-level 'applicationinsights' diagnostic for token metrics");
    }

    // ── APIM OpenAI API module ───────────────────────────────────────────────

    [Fact]
    public void ApimOpenAiApi_HasSubscriptionRequired_AndApiKeyHeader()
    {
        ApimOpenAiApiBicep.Should().Contain("subscriptionRequired: true",
            "the inference API must require an APIM subscription — never publicly callable");
        ApimOpenAiApiBicep.Should().MatchRegex(
            @"subscriptionKeyParameterNames:\s*{[^}]*header:\s*'api-key'",
            "the inference API must accept the subscription key via the 'api-key' header (AOAI SDK compatible)");
    }

    [Fact]
    public void ApimOpenAiApi_DeclaresBackendWithManagedIdentityCredentialsToCognitiveServices()
    {
        ApimOpenAiApiBicep.Should().Contain("Microsoft.ApiManagement/service/backends",
            "apim-openai-api.bicep must declare the AOAI backend");
        ApimOpenAiApiBicep.Should().MatchRegex(
            @"managedIdentity:\s*{\s*resource:\s*'https://cognitiveservices\.azure\.com'",
            "the backend must authenticate to AOAI via managed identity (resource=cognitiveservices.azure.com), never a key");
    }

    [Fact]
    public void ApimOpenAiApi_ExposesApimSubscriptionKey_AsSecureOutput_ViaListSecrets()
    {
        // The primary key must flow through as a @secure() output resolved at
        // Bicep-time via listSecrets() — that is what makes `azd provision`
        // idempotently re-assert the ACA secret + secretRef binding (see PR #52).
        ApimOpenAiApiBicep.Should().Contain("subscription.listSecrets()",
            "the APIM subscription primary key must be resolved via listSecrets()");
        ApimOpenAiApiBicep.Should().MatchRegex(
            @"@secure\(\)\s*output\s+subscriptionKey\s+string",
            "the APIM subscription key must be exposed as a @secure() output so ARM masks it in deployment history");
    }

    [Fact]
    public void ApimOpenAiApi_InferenceEndpointOutput_DoesNotDoubleAppendOpenAiSegment()
    {
        // The exact regression from incident #55: `AzureOpenAIClient` appends
        // `/openai/deployments/...` itself, so `inferenceEndpoint` must NOT end
        // in `/openai` — derive from the base path param instead of the
        // API's registered `path` (which includes `/openai` for the OpenAPI import).
        ApimOpenAiApiBicep.Should().Contain(
            "output inferenceEndpoint string = '${apim.properties.gatewayUrl}/${inferenceApiPath}'",
            "inferenceEndpoint must derive from the base 'inferenceApiPath' param, NOT from api.properties.path (regression #55)");

        // Belt-and-braces: reject the literal doubled-suffix expression too.
        ApimOpenAiApiBicep.Should().NotContain("api.properties.path}'",
            "inferenceEndpoint must not interpolate api.properties.path (which contains a trailing /openai) into the output");
    }

    [Fact]
    public void ApimOpenAiApi_WiresApiPolicyWithTokenLimitAndTokenEmitMetric()
    {
        // Policy is loaded via loadTextContent and templated with backend id +
        // tokens-per-minute — assert the API's policy resource wires it.
        ApimOpenAiApiBicep.Should().MatchRegex(
            @"resource\s+apiPolicy\s+'Microsoft\.ApiManagement/service/apis/policies",
            "apim-openai-api.bicep must declare an API policy resource");
        ApimOpenAiApiBicep.Should().Contain("loadTextContent('apim-openai-policy.xml')",
            "the API policy must load its content from apim-openai-policy.xml");
        ApimOpenAiApiBicep.Should().Contain("'{backend-id}'",
            "the API policy must substitute the AOAI backend id at deploy time");
        ApimOpenAiApiBicep.Should().Contain("'{tokens-per-minute}'",
            "the API policy must substitute the tokens-per-minute cap at deploy time");
    }

    [Fact]
    public void ApimOpenAiApi_ApiLevelAppInsightsDiag_EnablesMetricsRouting()
    {
        // API-level applicationinsights diagnostic with metrics=true is what
        // routes emit-token-metric custom metrics into App Insights AppMetrics.
        ApimOpenAiApiBicep.Should().MatchRegex(
            @"parent:\s*api\s*\r?\n\s*name:\s*'applicationinsights'",
            "apim-openai-api.bicep must declare an API-level 'applicationinsights' diagnostic");
        ApimOpenAiApiBicep.Should().Contain("metrics: true",
            "the API-level applicationinsights diagnostic must enable metrics routing for emit-token-metric");
    }

    [Fact]
    public void ApimOpenAiApi_ApiLevelAzureMonitorDiag_EnablesLargeLanguageModelLogs()
    {
        ApimOpenAiApiBicep.Should().MatchRegex(
            @"parent:\s*api\s*\r?\n\s*name:\s*'azuremonitor'",
            "apim-openai-api.bicep must declare an API-level 'azuremonitor' diagnostic");
        ApimOpenAiApiBicep.Should().Contain("largeLanguageModel:",
            "the API-level azuremonitor diagnostic must declare the largeLanguageModel section for LLM request/response capture");
        ApimOpenAiApiBicep.Should().Contain("logs: 'enabled'",
            "the largeLanguageModel section must enable log capture");
    }

    [Fact]
    public void ApimOpenAiApi_GrantsCognitiveServicesOpenAiUserRole_ToApimIdentity()
    {
        // 5e0bd9bd-7b93-4f28-af87-19fc36ad61bd = Cognitive Services OpenAI User.
        // Without this role the MI backend policy 403s at request time.
        ApimOpenAiApiBicep.Should().Contain("5e0bd9bd-7b93-4f28-af87-19fc36ad61bd",
            "apim-openai-api.bicep must reference the 'Cognitive Services OpenAI User' role definition id");
        ApimOpenAiApiBicep.Should().Contain("./apim-openai-role-assignment.bicep",
            "apim-openai-api.bicep must invoke the role-assignment sub-module");
        ApimOpenAiApiBicep.Should().Contain("principalId: apimPrincipalId",
            "the role must be granted to the APIM principal id");
    }

    [Fact]
    public void ApimRoleAssignmentModule_ScopesToAiServicesAccount_WithServicePrincipalType()
    {
        ApimRoleAssignmentBicep.Should().Contain("scope: aiServices",
            "the role assignment must be scoped to the AOAI account, not the resource group");
        ApimRoleAssignmentBicep.Should().Contain("principalType: 'ServicePrincipal'",
            "the role assignment must set principalType=ServicePrincipal for the APIM MI");
        ApimRoleAssignmentBicep.Should().Contain(
            "subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionId)",
            "the role assignment must build the role definition id via subscriptionResourceId");
    }

    // ── APIM policy XML ──────────────────────────────────────────────────────

    [Fact]
    public void ApimPolicyXml_UsesManagedIdentityAuthentication_ToCognitiveServices()
    {
        ApimPolicyXml.Should().MatchRegex(
            @"<authentication-managed-identity\s+resource=""https://cognitiveservices\.azure\.com""",
            "the APIM policy must authenticate to AOAI via managed identity — never a key or shared secret");
    }

    [Fact]
    public void ApimPolicyXml_SetsBackendServiceToTemplatedBackendId()
    {
        ApimPolicyXml.Should().Contain("<set-backend-service backend-id=\"{backend-id}\"",
            "the APIM policy must route to the templated backend-id (substituted at deploy time)");
    }

    [Fact]
    public void ApimPolicyXml_EnforcesTokensPerMinuteLimit_PerSubscription()
    {
        // Subscription-scoped counter (not caller IP or global) — required for
        // the multi-tenant cost/quota story.
        ApimPolicyXml.Should().MatchRegex(
            @"<azure-openai-token-limit\s+counter-key=""@\(context\.Subscription\.Id\)""",
            "token-limit must key on the APIM subscription id (per-subscription quota)");
        ApimPolicyXml.Should().Contain("tokens-per-minute=\"{tokens-per-minute}\"",
            "token-limit must reference the templated tokens-per-minute cap");
        ApimPolicyXml.Should().Contain("retry-after-header-name=\"Retry-After\"",
            "token-limit must emit the standard Retry-After header on throttle");
    }

    [Fact]
    public void ApimPolicyXml_EmitsTokenMetricInRetailPulseNamespace_WithApiOperationSubscriptionDimensions()
    {
        ApimPolicyXml.Should().Contain("<azure-openai-emit-token-metric namespace=\"RetailPulse\">",
            "emit-token-metric must publish under the 'RetailPulse' custom-metric namespace");
        ApimPolicyXml.Should().Contain("<dimension name=\"API ID\"",
            "emit-token-metric must dimension by API ID");
        ApimPolicyXml.Should().Contain("<dimension name=\"Operation ID\"",
            "emit-token-metric must dimension by Operation ID");
        ApimPolicyXml.Should().Contain("<dimension name=\"Subscription ID\"",
            "emit-token-metric must dimension by Subscription ID (per-tenant cost attribution)");
    }

    // ── Container-apps wiring: no direct AOAI endpoint on the API ────────────

    [Fact]
    public void ContainerApps_DeclaresApimSubscriptionKey_AsAcaSecret_WithSecureAnnotation()
    {
        ContainerAppsBicep.Should().Contain("@secure()",
            "the APIM subscription key parameter into container-apps.bicep must be marked @secure() so ARM masks it");
        ContainerAppsBicep.Should().Contain("apimSubscriptionKey",
            "container-apps.bicep must consume the APIM subscription key parameter");
        ContainerAppsBicep.Should().MatchRegex(
            @"secrets:\s*\[\s*{\s*name:\s*apimSubscriptionKeySecretName\s*value:\s*apimSubscriptionKey",
            "the APIM subscription key must be declared as an ACA secret, never inlined as an env-var value");
    }

    [Fact]
    public void ContainerApps_ApimInferenceEndpointParameter_IsRequired_NoDefault()
    {
        // A default endpoint would let a mis-provision silently boot the API
        // against something other than the APIM inference URL. Force operators
        // to plumb it through main.bicep.
        Match match = ApimInferenceEndpointParamRegex().Match(ContainerAppsBicep);
        match.Success.Should().BeTrue(
            "container-apps.bicep must declare 'apimInferenceEndpoint' as a param with a description");
        match.Groups["type"].Value.Should().Be("string");
        match.Groups["rest"].Value.Should().NotContain("=",
            "apimInferenceEndpoint must not carry a default — main.bicep must pipe the APIM endpoint through explicitly");
    }

    // ── main.bicep aliases the APIM inference endpoint as a first-class output

    [Theory]
    [InlineData("AZURE_APIM_NAME")]
    [InlineData("AZURE_APIM_GATEWAY_URL")]
    [InlineData("AZURE_APIM_INFERENCE_ENDPOINT")]
    [InlineData("AZURE_APIM_INFERENCE_API_NAME")]
    [InlineData("AZURE_APIM_INFERENCE_SUBSCRIPTION_NAME")]
    public void MainBicep_EmitsApimGatewayOutput_ForOperatorAndVerificationTooling(string outputName)
    {
        var pattern = new Regex(
            $@"output\s+{Regex.Escape(outputName)}\s+string\s*=",
            RegexOptions.Multiline);
        pattern.IsMatch(MainBicep).Should().BeTrue(
            $"main.bicep must emit '{outputName}' so the postprovision hook + Verify-ApimAiGateway.ps1 can locate the deployed AI Gateway");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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

        throw new InvalidOperationException(
            "Could not locate repo root (RetailPulse.slnx) walking up from " + AppContext.BaseDirectory);
    }
}
