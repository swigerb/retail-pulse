using System.Collections.Concurrent;
using RetailPulse.Api.Guardrails.ContentSafety;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Scriptable evaluator used across the Content Safety tests. Callers push
/// per-stage results and inspect the ordered call log; concurrent
/// <see cref="EvaluateAsync"/> calls are safe because both structures are
/// concurrent-safe.
/// </summary>
internal sealed class FakeContentSafetyEvaluator : IContentSafetyEvaluator
{
    public ConcurrentDictionary<ContentSafetyStage, Queue<ContentSafetyResult>> Scripts { get; } = new();
    public ConcurrentQueue<(string Text, ContentSafetyStage Stage, ContentSafetyEvaluationContext Context)> Calls { get; } = new();
    public ContentSafetyResult DefaultResult { get; init; } = ContentSafetyResult.Passed;
    public Func<string, ContentSafetyStage, ContentSafetyResult?>? Matcher { get; set; }

    public FakeContentSafetyEvaluator Enqueue(ContentSafetyStage stage, ContentSafetyResult result)
    {
        Queue<ContentSafetyResult> q = Scripts.GetOrAdd(stage, _ => new Queue<ContentSafetyResult>());
        lock (q) { q.Enqueue(result); }
        return this;
    }

    public Task<ContentSafetyResult> EvaluateAsync(
        string text,
        ContentSafetyStage stage,
        ContentSafetyEvaluationContext context,
        CancellationToken cancellationToken)
    {
        Calls.Enqueue((text, stage, context));
        if (Matcher is not null)
        {
            ContentSafetyResult? matched = Matcher(text, stage);
            if (matched is not null)
            {
                return Task.FromResult(matched);
            }
        }
        if (Scripts.TryGetValue(stage, out Queue<ContentSafetyResult>? q))
        {
            lock (q)
            {
                if (q.Count > 0)
                {
                    return Task.FromResult(q.Dequeue());
                }
            }
        }
        return Task.FromResult(DefaultResult);
    }
}
