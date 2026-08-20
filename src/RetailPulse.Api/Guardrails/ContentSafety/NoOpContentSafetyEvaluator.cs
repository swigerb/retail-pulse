namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Zero-allocation no-op evaluator returned by DI when
/// <see cref="Contracts.Guardrails.ContentSafetyConfig.Enabled"/>
/// is <c>false</c>. Every call returns <see cref="ContentSafetyResult.Passed"/>
/// so behaviour on the disabled path is byte-for-byte equal to the pattern-only
/// guardrails.
/// </summary>
public sealed class NoOpContentSafetyEvaluator : IContentSafetyEvaluator
{
    public static readonly NoOpContentSafetyEvaluator Instance = new();

    public Task<ContentSafetyResult> EvaluateAsync(
        string text,
        ContentSafetyStage stage,
        ContentSafetyEvaluationContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ContentSafetyResult.Passed);
}
