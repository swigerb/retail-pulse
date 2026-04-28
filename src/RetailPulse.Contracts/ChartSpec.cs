namespace RetailPulse.Contracts;

/// <summary>
/// Structured chart specification returned by the agent for native rendering
/// </summary>
public record ChartSpec
{
    public required string Type { get; init; } // line, bar, groupedBar, pie, donut, horizontalBar, stackedBar, gauge, table
    public required string Title { get; init; }
    public string? XAxisTitle { get; init; }
    public string? YAxisTitle { get; init; }
    public List<ChartSeries> Data { get; init; } = [];
}

public record ChartSeries
{
    public required string Legend { get; init; } // series name / legend label
    public string? Color { get; init; } // hex color, e.g. "#1B4D7A"
    public List<ChartDataPoint> Values { get; init; } = [];
}

public record ChartDataPoint
{
    public required string X { get; init; } // category or x-axis label
    public required double Y { get; init; } // value
}
