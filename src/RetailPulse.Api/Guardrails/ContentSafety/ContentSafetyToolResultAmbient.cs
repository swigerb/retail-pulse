namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Ambient accessor for the tool-result Content Safety inspector. Set once
/// during application startup so the non-Agents seam
/// (<see cref="Budget.BudgetedAIFunction"/>) can consult the inspector without
/// any constructor-signature change under
/// <c>src/RetailPulse.Api/Agents/**</c>. When Content Safety is disabled the
/// inspector short-circuits internally, so it is safe to leave installed at all
/// times.
/// </summary>
public static class ContentSafetyToolResultAmbient
{
    /// <summary>The active inspector, or <c>null</c> when the layer is not wired up.</summary>
    public static ContentSafetyToolResultInspector? Current { get; private set; }

    /// <summary>Installs the inspector. Called once from <c>Program.cs</c>.</summary>
    public static void Install(ContentSafetyToolResultInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        Current = inspector;
    }

    /// <summary>Clears the ambient inspector — used by tests to keep isolation.</summary>
    internal static void Reset() => Current = null;
}
