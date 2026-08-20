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
/// <remarks>
/// <para>
/// The ambient accessor exists only because issue #89 owns
/// <c>src/RetailPulse.Api/Agents/**</c> and this issue must not modify the
/// <c>AgentExecutionPipeline</c> constructor that builds every
/// <see cref="Budget.BudgetedAIFunction"/>. Once #89 lands and takes ownership
/// of the tool-result seam, this static should be deleted in favour of a
/// constructor-injected <see cref="ContentSafetyToolResultInspector"/>. See
/// ADR-010 § "Tool-result seam".
/// </para>
/// <para>
/// <see cref="Install"/> is idempotent for the same instance and rejects an
/// attempt to install a different instance — this prevents test suites from
/// silently racing on a global mutable slot while still allowing the
/// production single-install path to run without ceremony.
/// </para>
/// </remarks>
public static class ContentSafetyToolResultAmbient
{
    private static ContentSafetyToolResultInspector? _current;

    /// <summary>The active inspector, or <c>null</c> when the layer is not wired up.</summary>
    public static ContentSafetyToolResultInspector? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Installs the inspector. Called once from <c>Program.cs</c>. Idempotent
    /// for the same instance; throws when a different inspector is already
    /// installed so a startup race is loud rather than silent.
    /// </summary>
    public static void Install(ContentSafetyToolResultInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ContentSafetyToolResultInspector? existing =
            Interlocked.CompareExchange(ref _current, inspector, null);
        if (existing is not null && !ReferenceEquals(existing, inspector))
        {
            throw new InvalidOperationException(
                "ContentSafetyToolResultAmbient is already installed with a different inspector instance.");
        }
    }

    /// <summary>Clears the ambient inspector — used by tests to keep isolation.</summary>
    internal static void Reset() => Interlocked.Exchange(ref _current, null);
}
