using System.Text.RegularExpressions;
using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Static-contract tests for <c>scripts/Verify-ProductionAuth.ps1</c> — the Sprint 4 read-only
/// production auth posture verifier (epic #27).
///
/// Pester is not available at a modern version on the build agents and CI does not run it, so
/// these tests read the script text and assert the security-critical invariants directly (no
/// PowerShell SDK dependency). They fail if anyone makes the verifier mutating, teaches it to
/// obtain or print a token/secret, drops a required posture check, or removes the fail-closed
/// exit / redaction behaviour. The invariants map 1:1 to the reviewer's required cases:
/// <list type="bullet">
///   <item>the script is strictly READ-ONLY (no mutating az verb, never signs in)</item>
///   <item>it never obtains, prints, or logs a token or secret; GUIDs are redacted</item>
///   <item>it verifies the Entra-only production pins (Mode/RequireAuth/env/tenant/client)</item>
///   <item>it rejects any Anonymous__*/GitHub__* env and confirms Easy Auth is disabled</item>
///   <item>it proves the anonymous 401 surface + health 200s via live probes</item>
///   <item>it proves the SWA serves Entra and hides GitHub/Anonymous sign-in</item>
///   <item>it supports -WhatIf (describe-only, no calls) and exits non-zero on any mismatch</item>
/// </list>
/// </summary>
public partial class VerifyProductionAuthScriptContractTests
{
    private static readonly string ScriptPath = Path.Combine(
        FindRepoRoot(), "scripts", "Verify-ProductionAuth.ps1");

    private static readonly string ScriptText = File.ReadAllText(ScriptPath);

    [GeneratedRegex(@"'(?:update|create|delete|deploy|restart|upsert|purge|patch)'")]
    private static partial Regex MutatingAzVerbRegex();

    [GeneratedRegex(@"SupportsShouldProcess\s*=\s*\$true")]
    private static partial Regex SupportsShouldProcessRegex();

    [Fact]
    public void Script_Exists()
    {
        File.Exists(ScriptPath).Should().BeTrue("the production verifier must be present");
        ScriptText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Script_IsReadOnly_NoMutatingAzVerbs()
    {
        // Every az invocation in this script must be a read (show/list/account/group). A quoted
        // mutating verb ('update'/'create'/'delete'/...) as an az argument would break the
        // read-only contract.
        MutatingAzVerbRegex().IsMatch(ScriptText)
            .Should().BeFalse("the production verifier must never issue a mutating az command");

        // Belt-and-braces: explicit mutating command shapes must be absent.
        ScriptText.Should().NotContain("containerapp update");
        ScriptText.Should().NotContain("auth update");
        ScriptText.Should().NotContain("az deployment");
    }

    [Fact]
    public void Script_NeverSignsIn()
    {
        // The verifier uses the caller's existing az context; it must never initiate a sign-in.
        // (The help text documents that it never calls `az login`, so assert on the actual az
        // subcommand token — az subcommands are passed as quoted array elements — not prose.)
        ScriptText.Should().NotContain("'login'");
        ScriptText.Should().NotContain("Connect-AzAccount");
    }

    [Fact]
    public void Script_NeverObtainsOrPrintsTokensOrSecrets()
    {
        // No token acquisition of any kind.
        ScriptText.Should().NotContain("get-access-token");
        ScriptText.Should().NotContain("get-token");
        ScriptText.Should().NotContain("-AsPlainText");
        ScriptText.Should().NotContain("ConvertFrom-SecureString");
        ScriptText.Should().NotContain("list-secrets");
        ScriptText.Should().NotContain("--query-token"); // never mint a real token for probes

        // The synthetic probe token must be an obvious non-secret, not a real credential.
        ScriptText.Should().Contain("access_token=not-a-real-token");
    }

    [Fact]
    public void Script_RedactsGuidsAndOnlyPrintsRedactedIdentifiers()
    {
        // A redaction helper exists and masks to ****last4.
        string redactor = FunctionBody("Format-Redacted");
        redactor.Should().Contain("'****'");

        // The expected tenant/client are printed ONLY through the redactor, never raw.
        ScriptText.Should().Contain("Format-Redacted $TenantId");
        ScriptText.Should().Contain("Format-Redacted $ClientId");
        ScriptText.Should().NotContain("Write-Host \"$TenantId\"");
        ScriptText.Should().NotContain("Write-Host \"$ClientId\"");
    }

    [Fact]
    public void Script_VerifiesEntraProductionPins()
    {
        ScriptText.Should().Contain("ASPNETCORE_ENVIRONMENT");
        ScriptText.Should().Contain("'Production'");
        ScriptText.Should().Contain("Authentication__Mode");
        ScriptText.Should().Contain("'Entra'");
        ScriptText.Should().Contain("Security__RequireAuth");
        ScriptText.Should().Contain("MicrosoftEntra__TenantId");
        ScriptText.Should().Contain("MicrosoftEntra__ClientId");
        ScriptText.Should().Contain("RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE");

        // Tenant/client are matched against the expected values (not merely present).
        ScriptText.Should().Contain("$appTenant -ieq $TenantId");
        ScriptText.Should().Contain("$appClient -ieq $ClientId");
    }

    [Fact]
    public void Script_RejectsOtherProviderEnvAndConfirmsEasyAuthDisabled()
    {
        ScriptText.Should().Contain("Anonymous__");
        ScriptText.Should().Contain("GitHub__");
        ScriptText.Should().Contain("Test-AnyEnvWithPrefix");

        // Easy Auth must be asserted DISABLED (in-process JWT boundary).
        ScriptText.Should().Contain("auth', 'show'");
        ScriptText.Should().Contain("Easy Auth) disabled");
    }

    [Fact]
    public void Script_ProvesAnonymous401SurfaceAndHealth200s()
    {
        ScriptText.Should().Contain("/api/chat");
        ScriptText.Should().Contain("/hubs/telemetry/negotiate");
        ScriptText.Should().Contain("/hubs/streaming/negotiate");
        ScriptText.Should().Contain("-eq 401");
        ScriptText.Should().Contain("/health");
        ScriptText.Should().Contain("/alive");
        ScriptText.Should().Contain("-eq 200");

        // The probe helper must never follow redirects into an authenticated surface or send a body.
        string probe = FunctionBody("Get-HttpStatus");
        probe.Should().Contain("MaximumRedirection 0");
    }

    [Fact]
    public void Script_ProvesSwaServesEntraAndHidesOtherModes()
    {
        ScriptText.Should().Contain("serves an Entra");
        ScriptText.Should().Contain("Continue with GitHub");
        ScriptText.Should().Contain("Continue in limited demo");
        ScriptText.Should().Contain("-not $exposesGitHub");
        ScriptText.Should().Contain("-not $exposesAnon");
    }

    [Fact]
    public void Script_DelegatesEntraAppRegistrationToReadOnlyVerifier()
    {
        ScriptText.Should().Contain("Verify-EntraAuth.ps1",
            "the app-registration posture (single-tenant, no secret, scope+role, SP assignmentRequired) is delegated to the dedicated read-only verifier");
    }

    [Fact]
    public void Script_SupportsWhatIf_DescribeOnly_NoCalls()
    {
        SupportsShouldProcessRegex().IsMatch(ScriptText)
            .Should().BeTrue("the verifier must advertise -WhatIf via SupportsShouldProcess");
        ScriptText.Should().Contain("$WhatIfPreference");
        ScriptText.Should().Contain("No Azure calls, HTTP requests, or writes were made.");
    }

    [Fact]
    public void Script_IsFailClosed_ExitsNonZeroOnAnyMismatch()
    {
        ScriptText.Should().Contain("$ErrorActionPreference = 'Stop'");
        ScriptText.Should().Contain("$script:failures");
        ScriptText.Should().Contain("exit 1");
        ScriptText.Should().Contain("exit 0");
    }

    // ── helpers (mirrors SetupEntraAuthScriptContractTests) ─────────────────────

    private static string FunctionBody(string name)
    {
        Match decl = Regex.Match(ScriptText, $@"function\s+{Regex.Escape(name)}\b");
        decl.Success.Should().BeTrue($"the script must declare function '{name}'");

        int open = ScriptText.IndexOf('{', decl.Index);
        open.Should().BeGreaterThan(-1, $"function '{name}' must have a body");

        int depth = 0;
        for (int i = open; i < ScriptText.Length; i++)
        {
            char c = ScriptText[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return ScriptText.Substring(open, i - open + 1);
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces while extracting function '{name}'.");
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

        throw new InvalidOperationException("Could not locate repo root (RetailPulse.slnx) from test base directory.");
    }
}
