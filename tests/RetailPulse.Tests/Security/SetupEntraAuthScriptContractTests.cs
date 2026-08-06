using System.Text.RegularExpressions;
using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Static-contract tests for <c>scripts/Setup-EntraAuth.ps1</c> that lock in the safe
/// reconciliation behaviour required by the security review (Finding #1 — app hijack).
///
/// Pester is not available at a modern version on the build agents and CI does not run it, so
/// these tests read the script text and assert the security-critical invariants directly
/// (no PowerShell SDK dependency). They fail if anyone reintroduces name-based adoption, drops
/// the managed-marker / ownership / identifier-URI verification, or removes the preview
/// write-gate. The scenarios map 1:1 to the reviewer's required cases:
/// <list type="bullet">
///   <item>pre-created same-name attacker app is rejected (create-only never adopts by name)</item>
///   <item>multiple matches hard-fail rather than picking the first</item>
///   <item>an explicit client id (or app object id) is the ONLY way to adopt</item>
///   <item>a marker mismatch blocks the PATCH</item>
///   <item>preview makes no writes</item>
///   <item>a partial failure surfaces as a thrown error (no silent success)</item>
/// </list>
/// </summary>
public partial class SetupEntraAuthScriptContractTests
{
    private static readonly string ScriptPath = Path.Combine(
        FindRepoRoot(), "scripts", "Setup-EntraAuth.ps1");

    private static readonly string ScriptText = File.ReadAllText(ScriptPath);

    [GeneratedRegex(@"\[string\]\$ClientId\b")]
    private static partial Regex ClientIdParamRegex();

    [GeneratedRegex(@"\[string\]\$AppObjectId\b")]
    private static partial Regex AppObjectIdParamRegex();

    [GeneratedRegex(@"\$app\s*=\s*\$\w+\.value\[0\]")]
    private static partial Regex FirstMatchAdoptionRegex();

    [GeneratedRegex(@"tags\s*=\s*@\(\$script:ManagedTag\)")]
    private static partial Regex ManagedTagStampRegex();

    [GeneratedRegex(@"POST[\s\S]*PATCH[\s\S]*DELETE[\s\S]*-not\s+\$script:ApplyWrites[\s\S]*throw")]
    private static partial Regex PreviewWriteGateRegex();

    [GeneratedRegex(@"\$ErrorActionPreference\s*=\s*'Stop'")]
    private static partial Regex ErrorActionStopRegex();

    [GeneratedRegex(@"\bthrow\b")]
    private static partial Regex ThrowRegex();

    [Fact]
    public void Script_Exists()
    {
        File.Exists(ScriptPath).Should().BeTrue("the setup script must be present for provisioning");
        ScriptText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ExplicitIdentifiers_AreTheOnlyWayToAdoptAnExistingApp()
    {
        // Adoption requires an operator-supplied appId or object id — declared as parameters.
        ClientIdParamRegex().IsMatch(ScriptText)
            .Should().BeTrue("-ClientId must be an explicit parameter used to adopt an existing app");
        AppObjectIdParamRegex().IsMatch(ScriptText)
            .Should().BeTrue("-AppObjectId must be an explicit parameter used to adopt an existing app");
    }

    [Fact]
    public void NameBasedFirstMatchAdoption_IsRemoved()
    {
        // The vulnerable pattern selected the first application returned by a displayName
        // filter and then mutated it. That must be gone entirely.
        ScriptText.Should().NotContain("$existing.value[0]",
            "adopting the first app returned by a displayName lookup is the hijack vector");

        // Guard against any variant that assigns the reconciliation target ($app) straight
        // from the first element of a query result.
        FirstMatchAdoptionRegex().IsMatch(ScriptText)
            .Should().BeFalse("the reconciliation target must never be a name-lookup first match");
    }

    [Fact]
    public void CreateOnlyMode_HardFailsWhenAnAppWithTheSameNameAlreadyExists()
    {
        // The display-name filter must exist ONLY to detect collisions and be followed by a
        // throw (never an adoption). This is the pre-created attacker-app case.
        string resolve = FunctionBody("Resolve-TargetApplication");

        resolve.Should().Contain("displayName eq",
            "create-only mode looks up the name to detect a collision");
        resolve.Should().Contain("NOT adopt an app by display name",
            "a same-name application must cause a hard failure, not adoption");
    }

    [Fact]
    public void AmbiguousMatches_HardFail_RatherThanGuessing()
    {
        string resolve = FunctionBody("Resolve-TargetApplication");
        resolve.Should().Contain("Count -gt 1", "more than one match must hard-fail");
        ThrowCount(resolve).Should().BeGreaterThanOrEqualTo(3,
            "no-match, ambiguous-match, and same-name collisions must each throw");
    }

    [Fact]
    public void Adoption_VerifiesOwnershipMarkerAndIdentifierUri_BeforeAnyMutation()
    {
        string guard = FunctionBody("Assert-SafeToAdopt");

        // (1) ownership — caller must be a registered owner.
        guard.Should().Contain("Test-AppOwnedByCaller", "adoption must confirm caller ownership");
        guard.Should().Contain("not a registered owner");

        // (2) identifier URI — never repoint another app's api:// URI.
        guard.Should().Contain("identifierUris", "adoption must verify the identifier URI matches");

        // (3) marker tag — must carry the managed marker unless explicitly overridden.
        guard.Should().Contain("ManagedTag", "adoption must verify the managed marker tag");
        guard.Should().Contain("AllowUnmarkedAdoption",
            "adopting an unmarked app must require an explicit override switch");

        // The safety gate runs before the resolver returns any explicitly-targeted app.
        string resolve = FunctionBody("Resolve-TargetApplication");
        resolve.Should().Contain("Assert-SafeToAdopt",
            "every explicit-adoption path must run the safety gate before returning the app");

        // Ownership helper actually queries the owners collection.
        FunctionBody("Test-AppOwnedByCaller").Should().Contain("/owners");
    }

    [Fact]
    public void NewlyCreatedApps_AreStampedWithTheManagedMarkerTag()
    {
        // The create body must set tags containing the managed marker so future reconciles can
        // recognise (and safely adopt) the tool's own app.
        ManagedTagStampRegex().IsMatch(ScriptText)
            .Should().BeTrue("created apps must be tagged with the managed marker");
    }

    [Fact]
    public void PreviewMode_MakesNoWrites_ViaCentralGate()
    {
        // A single choke point rejects every mutating verb unless -Apply was supplied, so
        // preview can never write even if a future guard is forgotten.
        string invokeGraph = FunctionBody("Invoke-Graph");
        invokeGraph.Should().Contain("script:ApplyWrites", "the write gate must consult the apply flag");
        PreviewWriteGateRegex().IsMatch(invokeGraph)
            .Should().BeTrue("every write verb must be blocked in preview mode");
    }

    [Fact]
    public void PreviewPlaceholders_DoNotAssignToValidatedParameterVariables()
    {
        ScriptText.Should().NotContain("$appObjectId = if",
            "PowerShell variables are case-insensitive, so assigning a preview placeholder to the validated -AppObjectId parameter fails before preview completes");
        ScriptText.Should().NotContain("$clientId = if",
            "PowerShell variables are case-insensitive, so assigning a preview placeholder to the validated -ClientId parameter fails before preview completes");
        ScriptText.Should().Contain("$resolvedAppObjectId");
        ScriptText.Should().Contain("$resolvedClientId");
    }

    [Fact]
    public void PartialFailure_SurfacesAsAThrownError_NotSilentSuccess()
    {
        // Stop-on-error plus an explicit non-zero-exit throw means a failed Graph call aborts
        // the run instead of continuing to mutate or reporting success.
        ErrorActionStopRegex().IsMatch(ScriptText).Should().BeTrue();
        string invokeGraph = FunctionBody("Invoke-Graph");
        invokeGraph.Should().Contain("LASTEXITCODE -ne 0");
        invokeGraph.Should().Contain("throw");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the full text of a PowerShell <c>function &lt;name&gt; { ... }</c> body by
    /// brace-matching from its declaration. Keeps the contract tests self-contained without a
    /// PowerShell parser dependency.
    /// </summary>
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

    private static int ThrowCount(string body) => ThrowRegex().Count(body);

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
