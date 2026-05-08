using FluentAssertions;
using RetailPulse.TeamsBot.Cards;

namespace RetailPulse.Tests.Cards;

public class TelemetryFormatterTests
{
    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(50, "50ms")]
    [InlineData(500, "500ms")]
    [InlineData(999, "999ms")]
    [InlineData(1000, "1.0s")]
    [InlineData(1500, "1.5s")]
    [InlineData(2345, "2.3s")]
    [InlineData(60000, "60.0s")]
    [InlineData(120500, "120.5s")]
    public void FormatDuration_ReturnsCorrectFormat(double durationMs, string expected)
    {
        var result = TelemetryFormatter.FormatDuration(durationMs);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("thought", "🤔")]
    [InlineData("THOUGHT", "🤔")]
    [InlineData("tool_call", "🔧")]
    [InlineData("tool_result", "✅")]
    [InlineData("response", "📡")]
    [InlineData("foundry", "⚡")]
    [InlineData("agent_delegation", "🤖")]
    [InlineData("agent_call", "🤖")]
    [InlineData("unknown", "•")]
    [InlineData("", "•")]
    public void GetSpanIcon_ReturnsCorrectIcon(string spanType, string expectedIcon)
    {
        var result = TelemetryFormatter.GetSpanIcon(spanType);

        result.Should().Be(expectedIcon);
    }

    [Theory]
    [InlineData("thought", "THINK")]
    [InlineData("THOUGHT", "THINK")]
    [InlineData("tool_call", "TOOL")]
    [InlineData("tool_result", "RESULT")]
    [InlineData("response", "REPLY")]
    [InlineData("foundry", "FOUNDRY")]
    [InlineData("agent_delegation", "AGENT")]
    [InlineData("agent_call", "AGENT")]
    [InlineData("custom_type", "CUSTOM_TYPE")]
    public void GetTypeBadge_ReturnsCorrectBadge(string spanType, string expected)
    {
        var result = TelemetryFormatter.GetTypeBadge(spanType);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("short", 30, "short")]
    [InlineData("exact", 5, "exact")]
    [InlineData("", 10, "")]
    [InlineData(null, 10, "")]
    public void TruncateName_ShortNames_ReturnsUnchanged(string? name, int maxLength, string expected)
    {
        var result = TelemetryFormatter.TruncateName(name!, maxLength);

        result.Should().Be(expected);
    }

    [Fact]
    public void TruncateName_LongName_AddsEllipsis()
    {
        var longName = "This is a very long function name that exceeds the limit";
        var maxLength = 30;

        var result = TelemetryFormatter.TruncateName(longName, maxLength);

        result.Should().HaveLength(30);
        result.Should().EndWith("…");
        result.Should().StartWith("This is a very long function");
    }

    [Theory]
    [InlineData("simple detail", 60, "simple detail")]
    [InlineData("", 60, "")]
    [InlineData(null, 60, "")]
    public void TruncateDetail_ShortText_ReturnsUnchanged(string? detail, int maxLength, string expected)
    {
        var result = TelemetryFormatter.TruncateDetail(detail, maxLength);

        result.Should().Be(expected);
    }

    [Fact]
    public void TruncateDetail_WithNewlines_ReplacesWithSpaces()
    {
        var detailWithNewlines = "Line 1\nLine 2\r\nLine 3";

        var result = TelemetryFormatter.TruncateDetail(detailWithNewlines, 100);

        result.Should().NotContain("\n");
        result.Should().NotContain("\r\n");
        result.Should().Be("Line 1 Line 2 Line 3");
    }

    [Fact]
    public void TruncateDetail_LongText_TruncatesAndAddsEllipsis()
    {
        var longDetail = "This is a very detailed message that goes on for quite a while and should be truncated";
        var maxLength = 50;

        var result = TelemetryFormatter.TruncateDetail(longDetail, maxLength);

        result.Should().HaveLength(50);
        result.Should().EndWith("…");
    }

    [Theory]
    [InlineData(100, 100, 100)]
    [InlineData(50, 100, 50)]
    [InlineData(10, 100, 10)]
    [InlineData(1, 100, 5)] // Minimum 5%
    [InlineData(0, 100, 5)] // Minimum 5%
    public void CalculateWaterfallWidth_ReturnsCorrectPercentage(double spanDuration, double maxDuration, int expectedMin)
    {
        var result = TelemetryFormatter.CalculateWaterfallWidth(spanDuration, maxDuration);

        result.Should().BeGreaterThanOrEqualTo(expectedMin);
        result.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void CalculateWaterfallWidth_ZeroMaxDuration_ReturnsZero()
    {
        var result = TelemetryFormatter.CalculateWaterfallWidth(50, 0);

        result.Should().Be(0);
    }

    [Fact]
    public void CalculateWaterfallWidth_NegativeMaxDuration_ReturnsZero()
    {
        var result = TelemetryFormatter.CalculateWaterfallWidth(50, -100);

        result.Should().Be(0);
    }
}
