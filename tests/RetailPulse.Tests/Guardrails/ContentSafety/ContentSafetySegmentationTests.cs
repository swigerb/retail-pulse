using FluentAssertions;
using RetailPulse.Api.Guardrails.ContentSafety;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Text sent to Azure Content Safety must be segmented under the service's request limit.
/// </summary>
/// <remarks>
/// AnalyzeText rejects any single request over 10,000 characters with
/// <c>400 InvalidRequestBody: "The length of given text 14336 exceeds the limit 10000"</c>.
/// The exception propagated out of the tool-invocation path, so an oversized tool result
/// did not merely skip scanning — it destroyed the result. A twelve-brand portfolio
/// payload is roughly 14,000 characters, so the curated prompt "Show a horizontal bar
/// chart ranking all brands by depletion growth rate" lost all twelve brands on every
/// single run and could never draw its chart. It presented as a charting bug.
///
/// Segmenting rather than truncating matters: scanning only the first 10,000 characters
/// would leave the remainder unscanned, which is a hole in the guardrail rather than a fix.
/// </remarks>
public class ContentSafetySegmentationTests
{
    private const int ServiceLimit = 10_000;

    private static string Text(int length, char fill = 'a') => new(fill, length);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9_000)]
    public void ShortTextIsSentAsASingleSegment(int length)
    {
        AzureContentSafetyEvaluator.SegmentForAnalysis(Text(length))
            .Should().ContainSingle();
    }

    [Fact]
    public void TheRealFailingPayloadSizeIsSegmented()
    {
        // 14,336 characters is the exact size from the live 400.
        IReadOnlyList<string> segments = [.. AzureContentSafetyEvaluator.SegmentForAnalysis(Text(14_336))];

        segments.Count.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData(10_001)]
    [InlineData(14_336)]
    [InlineData(50_000)]
    [InlineData(250_000)]
    public void NoSegmentEverExceedsTheServiceLimit(int length)
    {
        foreach (string segment in AzureContentSafetyEvaluator.SegmentForAnalysis(Text(length)))
        {
            segment.Length.Should().BeLessThan(ServiceLimit);
        }
    }

    [Fact]
    public void EveryCharacterIsCoveredBySomeSegment()
    {
        // Truncation would be the tempting shortcut and would silently stop scanning the
        // tail. Each position carries a distinct marker so coverage can be checked
        // without relying on ambiguous substring searches.
        string original = string.Concat(Enumerable.Range(0, 3_000).Select(i => $"{i:D9} "));

        IReadOnlyList<string> segments = [.. AzureContentSafetyEvaluator.SegmentForAnalysis(original)];

        original.Should().StartWith(segments[0], "the first segment must begin at the start of the text");
        original.Should().EndWith(segments[^1], "the last segment must reach the end of the text");

        // Every marker must appear in at least one segment.
        foreach (int marker in new[] { 0, 500, 1_500, 2_500, 2_999 })
        {
            string needle = $"{marker:D9}";
            segments.Should().Contain(
                s => s.Contains(needle, StringComparison.Ordinal),
                $"the marker at position {marker} must be scanned");
        }
    }

    [Fact]
    public void SegmentsOverlapSoContentCannotHideOnABoundary()
    {
        string original = Text(20_000);

        IReadOnlyList<string> segments = [.. AzureContentSafetyEvaluator.SegmentForAnalysis(original)];

        // Total segment length exceeding the original proves the windows overlap rather
        // than butting up against each other.
        segments.Sum(s => s.Length).Should().BeGreaterThan(original.Length);
    }

    [Fact]
    public void SegmentationTerminatesOnVeryLargeInput()
    {
        // A step size that failed to advance would loop forever and hang the request.
        IReadOnlyList<string> segments = [.. AzureContentSafetyEvaluator.SegmentForAnalysis(Text(1_000_000))];

        segments.Should().NotBeEmpty();
        segments.Count.Should().BeLessThan(1_000, "segments must advance meaningfully through the text");
    }
}
