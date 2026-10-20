using System.ClientModel.Primitives;

namespace RetailPulse.Api.OpenAI.Gateway;

/// <summary>
/// Default retry policy, except that a gateway token-budget throttle is never retried. Waiting out a
/// Retry-After inside the request would hide the quota state the Control Room has to show.
/// </summary>
public sealed class BudgetAwareRetryPolicy(int maxRetries) : ClientRetryPolicy(maxRetries)
{
    protected override bool ShouldRetry(PipelineMessage message, Exception? exception) =>
        !IsBudgetThrottle(message) && base.ShouldRetry(message, exception);

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) =>
        !IsBudgetThrottle(message) && await base.ShouldRetryAsync(message, exception).ConfigureAwait(false);

    internal static bool IsBudgetThrottle(PipelineMessage message) =>
        message.Response is { Status: 429 } response
        && response.Headers.TryGetValue(GatewayEvidenceHeaders.Quota, out string? quota)
        && string.Equals(quota?.Trim(), "throttled", StringComparison.OrdinalIgnoreCase)
        && response.Headers.TryGetValue(GatewayEvidenceHeaders.BudgetScope, out _);
}
