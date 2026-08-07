using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Static deployment-contract guardrails for the provider-neutral authentication foundation.
/// These only read repo files — they never invoke azd or touch Azure.
///
/// They prove that the LIVE deployment path is explicitly pinned to the Entra authentication
/// mode and can never silently select GitHub or Anonymous:
/// <list type="bullet">
///   <item>Both azd postprovision hooks set <c>Authentication__Mode=Entra</c> on the API.</item>
///   <item>Neither hook ever sets the API mode to GitHub or Anonymous.</item>
///   <item>The committed <c>appsettings.Production.json</c> pins <c>Authentication:Mode = Entra</c>.</item>
/// </list>
/// </summary>
public sealed class ProviderNeutralDeploymentContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_PinsApiAuthenticationModeToEntra(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().Contain("Authentication__Mode=Entra",
            $"{hookFile} must explicitly pin the API to the Entra authentication mode (not merely default to it)");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_NeverSelectsGitHubOrAnonymousMode(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().NotContain("Authentication__Mode=Anonymous",
            $"{hookFile} must never deploy the API in Anonymous authentication mode");
        script.Should().NotContain("Authentication__Mode=GitHub",
            $"{hookFile} must never deploy the API in GitHub authentication mode");
    }

    [Fact]
    public void ProductionAppSettings_PinsAuthenticationModeToEntra()
    {
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.Production.json"));

        json.Should().MatchRegex(
            "\"Authentication\"\\s*:\\s*\\{[^}]*\"Mode\"\\s*:\\s*\"Entra\"",
            "appsettings.Production.json must explicitly pin Authentication:Mode to Entra");
        json.Should().NotContain("\"Mode\": \"Anonymous\"");
        json.Should().NotContain("\"Mode\": \"GitHub\"");
    }

    [Fact]
    public void BaseAppSettings_DeclareEntraAuthenticationMode()
    {
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.json"));

        json.Should().MatchRegex(
            "\"Authentication\"\\s*:\\s*\\{[^}]*\"Mode\"\\s*:\\s*\"Entra\"",
            "the base appsettings.json must declare a deterministic Entra authentication mode");
    }

    [Fact]
    public void BaseAppSettings_NeverSelectsAnonymousOrGitHubMode()
    {
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.json"));

        json.Should().NotContain("\"Mode\": \"Anonymous\"",
            "the live base appsettings.json must never select Anonymous mode");
        json.Should().NotContain("\"Mode\": \"GitHub\"",
            "the live base appsettings.json must never select GitHub mode");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_NeverConfiguresAnonymousGuardrails(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        // The live deployment is Entra-only, so it must never carry the Anonymous hosted
        // opt-in or a signing key — configuring these would imply an Anonymous deployment.
        script.Should().NotContain("Anonymous__AllowHosted",
            $"{hookFile} must never enable hosted Anonymous mode");
        script.Should().NotContain("Anonymous__SigningKey",
            $"{hookFile} must never carry an Anonymous signing key");
    }

    [Fact]
    public void ContainerAppsBicep_PinsApiToSingleReplica()
    {
        // maxReplicas: 1 is required for the single SQLite writer, the (non-Production)
        // Anonymous mode's replica-local billable-use circuit breaker, AND the Sprint 2
        // GitHub BFF mode's replica-local OAuth state/redemption stores + login limiters.
        // This proves the live API artifact cannot scale out.
        string bicep = File.ReadAllText(Path.Combine(
            RepoRoot, "infra", "modules", "container-apps.bicep"));

        // Scope the assertion to the API container app's own scale block, so a maxReplicas on
        // some OTHER container app (mcp/teamsbot) can never satisfy this contract by accident.
        int apiIndex = bicep.IndexOf("'ca-retailpulse-api'", StringComparison.Ordinal);
        apiIndex.Should().BeGreaterThan(-1, "the API container app must exist");
        int nextResourceIndex = bicep.IndexOf("resource ", apiIndex, StringComparison.Ordinal);
        string apiBlock = nextResourceIndex > apiIndex
            ? bicep[apiIndex..nextResourceIndex]
            : bicep[apiIndex..];

        apiBlock.Should().MatchRegex(
            "maxReplicas\\s*:\\s*1",
            "the API container app must pin maxReplicas to 1");
        apiBlock.Should().NotMatchRegex(
            "maxReplicas\\s*:\\s*([2-9]|\\d{2,})",
            "the API container app must never allow more than one replica");
    }

    [Fact]
    public void ExampleAnonymousConfig_IsNotLoadedByAnyEnvironment()
    {
        // The Anonymous example/template MUST NOT be a real environment overlay. Only
        // appsettings.json and appsettings.{Environment}.json are auto-loaded; the example
        // is deliberately named appsettings.Anonymous.example.json (note ".example.") so it
        // can never be picked up by ASPNETCORE_ENVIRONMENT=Anonymous.
        string apiDir = Path.Combine(RepoRoot, "src", "RetailPulse.Api");

        File.Exists(Path.Combine(apiDir, "appsettings.Anonymous.json"))
            .Should().BeFalse("a live appsettings.Anonymous.json overlay must not exist");
        File.Exists(Path.Combine(apiDir, "appsettings.Anonymous.example.json"))
            .Should().BeTrue("the non-live Anonymous template should be committed for documentation");
    }

    [Fact]
    public void ExampleGitHubConfig_IsNotLoadedByAnyEnvironment()
    {
        // The GitHub example/template MUST NOT be a real environment overlay. Only
        // appsettings.json and appsettings.{Environment}.json are auto-loaded; the example
        // is deliberately named appsettings.GitHub.example.json (note ".example.") so it
        // can never be picked up by ASPNETCORE_ENVIRONMENT=GitHub.
        string apiDir = Path.Combine(RepoRoot, "src", "RetailPulse.Api");

        File.Exists(Path.Combine(apiDir, "appsettings.GitHub.json"))
            .Should().BeFalse("a live appsettings.GitHub.json overlay must not exist");
        File.Exists(Path.Combine(apiDir, "appsettings.GitHub.example.json"))
            .Should().BeTrue("the non-live GitHub template should be committed for documentation");
    }

    [Fact]
    public void ExampleGitHubConfig_ContainsNoRealSecrets()
    {
        // The committed GitHub example must only ever carry angle-bracket placeholders for the
        // secret values — never a real client secret or signing key.
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.GitHub.example.json"));

        json.Should().MatchRegex("\"ClientSecret\"\\s*:\\s*\"<[^\"]*>\"",
            "the GitHub example client secret must be an angle-bracket placeholder");
        json.Should().MatchRegex("\"SigningKey\"\\s*:\\s*\"<[^\"]*>\"",
            "the GitHub example signing key must be an angle-bracket placeholder");
    }

    [Fact]
    public void ExampleGitHubConfig_HasImmutableAllowlistContract_NoLoginAllowlist()
    {
        // The example must teach the hardened contract: immutable allowlist + secure cookies +
        // single-replica acknowledgement, and must NOT reintroduce a mutable login allowlist.
        string json = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.GitHub.example.json"));

        json.Should().NotContain("\"AllowedLogins\"",
            "the mutable login allowlist was removed and must never return to the example");
        json.Should().Contain("\"AllowedUserIds\"",
            "the example must show the immutable numeric-id allowlist");
        json.Should().Contain("\"RequireSecureCookies\"",
            "the example must document the secure-cookie enforcement flag");
        json.Should().Contain("\"AcknowledgeSingleReplica\"",
            "the example must document the single-replica acknowledgement flag");
        json.Should().Contain("\"AdditionalValidationKeys\"",
            "the example must document signing-key rotation via additional validation keys");
    }

    [Fact]
    public void InfraBicep_PinsFrontendAuthModeToEntra()
    {
        // The SPA build reads VITE_AUTH_MODE from the azd env (an infra output). The live
        // deployment must emit it pinned to Entra so the frontend renders ONLY the Microsoft
        // sign-in UX — matching the API's Entra Authentication__Mode (proven above/below).
        string bicep = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicep"));

        bicep.Should().MatchRegex(
            "output\\s+VITE_AUTH_MODE\\s+string\\s*=\\s*'Entra'",
            "infra/main.bicep must emit VITE_AUTH_MODE pinned to Entra for the live SPA build");
        bicep.Should().NotMatchRegex(
            "output\\s+VITE_AUTH_MODE\\s+string\\s*=\\s*'(GitHub|Anonymous)'",
            "the live SPA build must never be pinned to GitHub or Anonymous");
    }

    [Fact]
    public void DeploymentContract_FrontendAndApiAuthModesAreInParity()
    {
        // End-to-end parity for the LIVE path: the frontend VITE_AUTH_MODE (infra output) and the
        // API Authentication__Mode (both postprovision hooks + committed Production settings) must
        // all resolve to the SAME provider — Entra. This is the single guardrail that proves the
        // two halves of a deployment can never diverge into a mixed/misconfigured provider state.
        string bicep = File.ReadAllText(Path.Combine(RepoRoot, "infra", "main.bicep"));
        string ps1 = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", "postprovision.ps1"));
        string sh = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", "postprovision.sh"));
        string prod = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Api", "appsettings.Production.json"));

        const string frontendMode = "Entra";

        bicep.Should().MatchRegex(
            "output\\s+VITE_AUTH_MODE\\s+string\\s*=\\s*'" + frontendMode + "'",
            "frontend VITE_AUTH_MODE must be " + frontendMode);
        ps1.Should().Contain($"Authentication__Mode={frontendMode}",
            "the pwsh hook API mode must match the frontend mode");
        sh.Should().Contain($"Authentication__Mode={frontendMode}",
            "the sh hook API mode must match the frontend mode");
        prod.Should().MatchRegex(
            "\"Authentication\"\\s*:\\s*\\{[^}]*\"Mode\"\\s*:\\s*\"" + frontendMode + "\"",
            "the committed Production API mode must match the frontend mode");
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.github.example")]
    [InlineData(".env.anonymous.example")]
    public void WebEnvTemplates_DocumentAuthMode(string envFile)
    {
        // Every committed web env template must document VITE_AUTH_MODE so an operator building a
        // non-default (GitHub/Anonymous) deployment has a safe, secret-free starting point and the
        // fail-closed contract is discoverable. These are templates only — never auto-loaded.
        string path = Path.Combine(RepoRoot, "src", "RetailPulse.Web", envFile);

        File.Exists(path).Should().BeTrue($"{envFile} template should be committed");
        File.ReadAllText(path).Should().Contain("VITE_AUTH_MODE",
            $"{envFile} must document the VITE_AUTH_MODE build-time selector");
    }

    [Theory]
    [InlineData(".env.github.example")]
    [InlineData(".env.anonymous.example")]
    public void WebModeTemplates_CarryNoSecrets(string envFile)
    {
        // The non-production SPA templates are build-time CONFIG only. They must never carry a
        // provider secret or signing key — those live on the backend, never in the browser bundle.
        string content = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Web", envFile));

        content.Should().NotContain("ClientSecret",
            $"{envFile} must never carry a client secret");
        content.Should().NotContain("SigningKey",
            $"{envFile} must never carry a signing key");
    }

    [Theory]
    [InlineData("preprovision.ps1")]
    [InlineData("preprovision.sh")]
    public void PreprovisionHook_FailsClosedWhenEntraIdsMissing(string hookFile)
    {
        // The live default auth mode is Entra. The preprovision hook must fail the whole provision
        // BEFORE any resource is created when the Entra tenant/client IDs are empty/placeholders, so a
        // deployment can never silently ship an unauthenticated shell.
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().Contain("RETAIL_PULSE_ENTRA_TENANT_ID",
            $"{hookFile} must validate the Entra tenant id before provisioning");
        script.Should().Contain("RETAIL_PULSE_ENTRA_CLIENT_ID",
            $"{hookFile} must validate the Entra client id before provisioning");
        script.Should().Contain("RETAIL_PULSE_AUTH_MODE",
            $"{hookFile} must read the auth mode so non-Entra deployments can skip the Entra-specific check");
        // The hook must actually abort (throw/exit) on the misconfiguration, not merely warn.
        script.Should().MatchRegex(hookFile.EndsWith(".ps1") ? "throw " : "exit 1",
            $"{hookFile} must abort provisioning when Entra IDs are missing");
    }

    [Theory]
    [InlineData("preprovision.ps1")]
    [InlineData("preprovision.sh")]
    public void PreprovisionHook_DefaultsAuthModeToEntra(string hookFile)
    {
        // When RETAIL_PULSE_AUTH_MODE is unset the hook must treat the deployment as Entra (the live
        // default), so an operator cannot bypass the Entra ID check simply by not setting the mode.
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

        script.Should().Contain("Entra",
            $"{hookFile} must default the auth mode to Entra when RETAIL_PULSE_AUTH_MODE is unset");
    }

    [Fact]
    public void WebPackage_WiresPrebuildAuthConfigValidator()
    {
        // A frontend-only deploy (e.g. Static Web Apps) builds without azd outputs. The npm `prebuild`
        // guard must run the validator so `npm run build` fails fast on an Entra build with empty config.
        string pkg = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Web", "package.json"));

        pkg.Should().MatchRegex(
            "\"prebuild\"\\s*:\\s*\"[^\"]*validate-auth-config\\.mjs[^\"]*\"",
            "package.json must wire a prebuild step that runs the auth-config validator");

        File.Exists(Path.Combine(RepoRoot, "src", "RetailPulse.Web", "scripts", "validate-auth-config.mjs"))
            .Should().BeTrue("the auth-config validator script must be committed");
    }

    [Fact]
    public void WebAuthConfigValidator_FailsClosedForEntra_ButPassesWhenModeUnset()
    {
        // Static guardrails on the validator's contract so it stays green for CI's plain `npm run build`
        // (no VITE_AUTH_MODE) yet fails an explicit Entra build with empty ids.
        string script = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "RetailPulse.Web", "scripts", "validate-auth-config.mjs"));

        script.Should().Contain("VITE_AUTH_MODE",
            "the validator must key off the build-time auth-mode selector");
        script.Should().Contain("VITE_ENTRA_TENANT_ID",
            "the validator must require the Entra tenant id for an Entra build");
        script.Should().Contain("VITE_ENTRA_CLIENT_ID",
            "the validator must require the Entra client id for an Entra build");
        script.Should().Contain("process.exit(1)",
            "the validator CLI must fail the build (non-zero exit) on invalid config");
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

        throw new InvalidOperationException(
            "Could not locate repo root (RetailPulse.slnx) walking up from " + AppContext.BaseDirectory);
    }
}
