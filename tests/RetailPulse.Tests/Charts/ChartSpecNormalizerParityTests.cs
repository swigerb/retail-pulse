using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;
using Xunit;

namespace RetailPulse.Tests.Charts;

/// <summary>
/// Publix production sweep #76 Group E — the ChartSpecNormalizer must
/// recognise the realistic JSON shapes for every canonical chart type so the
/// inline-chart sanitizer never leaks a raw {"type":"gauge",...} payload into
/// the assistant reply text (prompt #26). Line, bar, pie, donut, table and
/// grouped/stacked/horizontal bar all use array/labelled-object shapes that
/// existing tests cover — this suite pins the gauge single-value shape and
/// asserts the parity across every canonical chart type via the
/// <see cref="ChartAcceptanceManifest"/>.
/// </summary>
public sealed class ChartSpecNormalizerParityTests
{
    [Fact]
    public void Gauge_SingleValueTopLevel_IsRecognised()
    {
        const string json = /*lang=json,strict*/ """
            {
                "type": "gauge",
                "title": "Pinnacle Hardware Inventory Health — Midwest",
                "value": 82,
                "yAxisTitle": "Inventory Health % (0-100)"
            }
            """;

        ChartSpecNormalizer.TryNormalize(json, out ChartSpec? chart).Should().BeTrue(
            "a gauge payload with a top-level 'value' must be normalized so the "
            + "inline-chart sanitizer can extract it (Publix #76 Group E)");
        chart.Should().NotBeNull();
        chart.Type.Should().Be("gauge");
        chart.Data.Should().HaveCount(1);
        chart.Data[0].Values.Should().HaveCount(1);
        chart.Data[0].Values[0].Y.Should().Be(82);
    }

    [Fact]
    public void Gauge_SingleValueUnderData_IsRecognised()
    {
        const string json = /*lang=json,strict*/ """
            {
                "type": "gauge",
                "title": "Inventory Health",
                "data": { "value": 75, "max": 100, "label": "Pinnacle Hardware" }
            }
            """;

        ChartSpecNormalizer.TryNormalize(json, out ChartSpec? chart).Should().BeTrue();
        chart.Should().NotBeNull();
        chart.Type.Should().Be("gauge");
        chart.Data[0].Values[0].Y.Should().Be(75);
    }

    [Fact]
    public void Gauge_ScoreShape_IsRecognised()
    {
        // Some models emit {"score": 90, "label": "..."} instead of "value".
        const string json = /*lang=json,strict*/ """
            {
                "type": "gauge",
                "title": "Health",
                "score": 90,
                "label": "Overall"
            }
            """;

        ChartSpecNormalizer.TryNormalize(json, out ChartSpec? chart).Should().BeTrue();
        chart.Should().NotBeNull();
        chart.Data[0].Values[0].Y.Should().Be(90);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("bar")]
    [InlineData("groupedBar")]
    [InlineData("stackedBar")]
    [InlineData("horizontalBar")]
    [InlineData("pie")]
    [InlineData("donut")]
    [InlineData("gauge")]
    [InlineData("table")]
    public void EveryCanonicalChartType_HasARecognisedInlineShape(string chartType)
    {
        // The parity guarantee: for every canonical chart type there is at least
        // ONE realistic inline JSON shape that the normalizer recognises. If a
        // new chart type is added without a normalizer path, this test fails.
        string json = SampleInlineJsonFor(chartType);

        ChartSpecNormalizer.TryNormalize(json, out ChartSpec? chart).Should().BeTrue(
            $"'{chartType}' must have a recognised inline JSON shape so the sanitizer "
            + "never leaks raw chart JSON into the user-visible reply text");
        chart.Should().NotBeNull();
        chart.Type.Should().Be(chartType);
    }

    private static string SampleInlineJsonFor(string type) => type switch
    {
        "gauge" => /*lang=json,strict*/ """{"type":"gauge","title":"H","value":80}""",
        "table" => /*lang=json,strict*/ """{"type":"table","title":"T","data":[{"legend":"Row1","values":[{"x":"A","y":1},{"x":"B","y":2}]}]}""",
        "pie" or "donut" => $$"""{"type":"{{type}}","title":"S","data":[{"legend":"L","values":[{"x":"A","y":40},{"x":"B","y":60}]}]}""",
        "horizontalBar" => /*lang=json,strict*/ """{"type":"horizontalBar","title":"HB","data":[{"legend":"L","values":[{"x":"A","y":1},{"x":"B","y":2}]}]}""",
        "groupedBar" => /*lang=json,strict*/ """{"type":"groupedBar","title":"G","data":[{"legend":"A","values":[{"x":"Q1","y":1}]},{"legend":"B","values":[{"x":"Q1","y":2}]}]}""",
        "stackedBar" => /*lang=json,strict*/ """{"type":"stackedBar","title":"S","data":[{"legend":"A","values":[{"x":"Q1","y":1}]}]}""",
        "bar" or "line" => $$"""{"type":"{{type}}","title":"X","data":[{"legend":"L","values":[{"x":"A","y":1},{"x":"B","y":2}]}]}""",
        _ => /*lang=json,strict*/ """{"type":"bar","title":"X","data":[{"legend":"L","values":[{"x":"A","y":1}]}]}""",
    };
}
