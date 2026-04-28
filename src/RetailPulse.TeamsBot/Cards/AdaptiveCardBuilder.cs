using Microsoft.Agents.Core.Models;
using RetailPulse.Contracts;
using RetailPulse.TeamsBot.Models;

namespace RetailPulse.TeamsBot.Cards;

/// <summary>
/// Service for building Adaptive Cards for Teams bot responses
/// </summary>
public class AdaptiveCardBuilder
{
    private readonly IConfiguration _configuration;

    public AdaptiveCardBuilder(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Builds a chat response card with AI reply, optional charts, and collapsible telemetry
    /// </summary>
    public Attachment BuildChatResponseCard(string reply, List<AgentSpan>? spans, List<ChartSpec>? charts, string? sessionId = null)
    {
        var bodyItems = new List<object>
        {
            // Header with branding
            new
            {
                type = "Container",
                style = "emphasis",
                items = new object[]
                {
                    new
                    {
                        type = "ColumnSet",
                        columns = new object[]
                        {
                            new
                            {
                                type = "Column",
                                width = "auto",
                                items = new object[]
                                {
                                    new
                                    {
                                        type = "TextBlock",
                                        text = "🥃",
                                        size = "large"
                                    }
                                }
                            },
                            new
                            {
                                type = "Column",
                                width = "stretch",
                                items = new object[]
                                {
                                    new
                                    {
                                        type = "TextBlock",
                                        text = "Retail Pulse",
                                        weight = "bolder",
                                        size = "large",
                                        color = "accent"
                                    }
                                }
                            }
                        }
                    }
                }
            },
            // AI Response
            new
            {
                type = "TextBlock",
                text = reply,
                wrap = true,
                spacing = "medium"
            }
        };

        // Add chart visualizations if present
        if (charts?.Any() == true)
        {
            var chartItems = new List<object>
            {
                new
                {
                    type = "TextBlock",
                    text = "📊 Charts",
                    weight = "bolder",
                    size = "medium",
                    color = "accent"
                }
            };

            foreach (var chart in charts)
            {
                var acChartType = chart.Type switch
                {
                    "line" => "Chart.Line",
                    "bar" => "Chart.VerticalBar",
                    "groupedBar" => "Chart.VerticalBar.Grouped",
                    "pie" => "Chart.Pie",
                    "donut" => "Chart.Donut",
                    "horizontalBar" => "Chart.HorizontalBar",
                    "stackedBar" => "Chart.HorizontalBar.Stacked",
                    _ => (string?)null
                };

                if (acChartType != null)
                {
                    var chartData = chart.Data.Select((series, idx) => new
                    {
                        legend = series.Legend,
                        color = MapToAdaptiveCardColor(series.Color, idx),
                        values = series.Values.Select(v => new { x = v.X, y = v.Y }).ToArray()
                    }).ToArray();

                    var chartElement = new Dictionary<string, object>
                    {
                        ["type"] = acChartType,
                        ["title"] = chart.Title,
                        ["data"] = chartData
                    };

                    if (!string.IsNullOrEmpty(chart.XAxisTitle))
                        chartElement["xAxisTitle"] = chart.XAxisTitle;
                    if (!string.IsNullOrEmpty(chart.YAxisTitle))
                        chartElement["yAxisTitle"] = chart.YAxisTitle;

                    chartItems.Add(chartElement);
                }
                else if (chart.Type == "gauge")
                {
                    // Render gauge as a FactSet (AC doesn't have a native gauge element)
                    var facts = chart.Data.SelectMany(s => s.Values.Select(v => new { title = v.X, value = v.Y.ToString("N1") })).ToArray();
                    chartItems.Add(new
                    {
                        type = "TextBlock",
                        text = $"📊 {chart.Title}",
                        weight = "bolder",
                        size = "small"
                    });
                    chartItems.Add(new
                    {
                        type = "FactSet",
                        facts = facts
                    });
                }
                else if (chart.Type == "table")
                {
                    // Render table as text summary
                    chartItems.Add(new
                    {
                        type = "TextBlock",
                        text = $"📋 {chart.Title}",
                        weight = "bolder",
                        size = "small"
                    });
                    foreach (var series in chart.Data)
                    {
                        var rows = string.Join("\n", series.Values.Select(v => $"- {v.X}: {v.Y:N0}"));
                        chartItems.Add(new
                        {
                            type = "TextBlock",
                            text = $"**{series.Legend}**\n{rows}",
                            wrap = true,
                            size = "small"
                        });
                    }
                }
            }

            bodyItems.Add(new
            {
                type = "Container",
                spacing = "medium",
                separator = true,
                items = chartItems.ToArray()
            });
        }

        // Build telemetry section with enhanced formatting
        var telemetryItems = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = "📊 Telemetry Summary",
                weight = "bolder",
                size = "medium",
                spacing = "small"
            }
        };

        if (spans?.Any() == true)
        {
            var sortedSpans = spans.OrderBy(s => s.Timestamp).ToList();
            
            // Show compact rows for each span
            foreach (var span in sortedSpans)
            {
                var icon = TelemetryFormatter.GetSpanIcon(span.Type);
                var typeBadge = TelemetryFormatter.GetTypeBadge(span.Type);
                var truncatedName = TelemetryFormatter.TruncateName(span.Name, 35);
                var truncatedDetail = TelemetryFormatter.TruncateDetail(span.Detail, 50);
                var formattedDuration = TelemetryFormatter.FormatDuration(span.DurationMs);

                var spanItems = new List<object>
                {
                    new
                    {
                        type = "TextBlock",
                        text = $"**{truncatedName}**",
                        wrap = true,
                        size = "small"
                    }
                };

                // Only show detail if not empty
                if (!string.IsNullOrWhiteSpace(truncatedDetail))
                {
                    spanItems.Add(new
                    {
                        type = "TextBlock",
                        text = truncatedDetail,
                        wrap = true,
                        size = "small",
                        isSubtle = true,
                        spacing = "none"
                    });
                }

                telemetryItems.Add(new
                {
                    type = "ColumnSet",
                    spacing = "small",
                    columns = new object[]
                    {
                        new
                        {
                            type = "Column",
                            width = "auto",
                            items = new object[]
                            {
                                new
                                {
                                    type = "TextBlock",
                                    text = icon,
                                    size = "small"
                                }
                            }
                        },
                        new
                        {
                            type = "Column",
                            width = "stretch",
                            items = spanItems.ToArray()
                        },
                        new
                        {
                            type = "Column",
                            width = "auto",
                            items = new object[]
                            {
                                new
                                {
                                    type = "TextBlock",
                                    text = typeBadge,
                                    size = "small",
                                    weight = "bolder",
                                    color = "accent",
                                    horizontalAlignment = "center"
                                }
                            }
                        },
                        new
                        {
                            type = "Column",
                            width = "auto",
                            items = new object[]
                            {
                                new
                                {
                                    type = "TextBlock",
                                    text = formattedDuration,
                                    size = "small",
                                    horizontalAlignment = "right",
                                    fontType = "monospace"
                                }
                            }
                        }
                    }
                });
            }

            // Summary row with total duration and span count
            var totalMs = sortedSpans.Sum(s => s.DurationMs);
            var formattedTotal = TelemetryFormatter.FormatDuration(totalMs);
            var spanCount = sortedSpans.Count;

            telemetryItems.Add(new
            {
                type = "ColumnSet",
                spacing = "small",
                separator = true,
                columns = new object[]
                {
                    new
                    {
                        type = "Column",
                        width = "stretch",
                        items = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                text = $"**Total: {spanCount} span{(spanCount != 1 ? "s" : "")}**",
                                size = "small",
                                weight = "bolder"
                            }
                        }
                    },
                    new
                    {
                        type = "Column",
                        width = "auto",
                        items = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                text = formattedTotal,
                                size = "small",
                                weight = "bolder",
                                horizontalAlignment = "right",
                                fontType = "monospace",
                                color = "accent"
                            }
                        }
                    }
                }
            });
        }
        else
        {
            telemetryItems.Add(new
            {
                type = "TextBlock",
                text = "No telemetry data available",
                size = "small",
                isSubtle = true,
                spacing = "small"
            });
        }

        // Add telemetry section (collapsed by default)
        bodyItems.Add(new
        {
            type = "Container",
            id = "telemetrySection",
            isVisible = false,
            spacing = "medium",
            separator = true,
            style = "emphasis",
            items = telemetryItems.ToArray()
        });

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.8",
            schema = "http://adaptivecards.io/schemas/adaptive-card.json",
            body = bodyItems.ToArray(),
            actions = BuildCardActions(spans, sessionId)
        };

        return new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = card
        };
    }

    /// <summary>
    /// Builds action buttons for the chat response card
    /// </summary>
    private object[] BuildCardActions(List<AgentSpan>? spans, string? sessionId)
    {
        var actions = new List<object>
        {
            new
            {
                type = "Action.ToggleVisibility",
                title = "📊 View Telemetry",
                targetElements = new[] { "telemetrySection" }
            }
        };

        // Add detailed telemetry button if we have spans and sessionId
        if (spans?.Any() == true && !string.IsNullOrEmpty(sessionId))
        {
            actions.Add(new
            {
                type = "Action.Submit",
                title = "📋 Full Telemetry Report",
                data = new
                {
                    action = "detailed_telemetry",
                    sessionId = sessionId
                }
            });
        }

        return actions.ToArray();
    }

    /// <summary>
    /// Builds a welcome card for new members or chat reset
    /// </summary>
    public Attachment BuildWelcomeCard(bool isReset, UserContext? userContext)
    {
        var userName = userContext?.DisplayName ?? "there";
        var title = isReset ? "✨ Chat Reset" : $"👋 Welcome to Retail Pulse, {userName}!";
        var intro = isReset
            ? "Your conversation has been reset. Start a new chat below!"
            : "I'm your AI-powered brand analytics assistant. Ask me about brand performance, market trends, or request visualizations!";

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.8",
            schema = "http://adaptivecards.io/schemas/adaptive-card.json",
            body = new object[]
            {
                // Branded header
                new
                {
                    type = "Container",
                    style = "emphasis",
                    items = new object[]
                    {
                        new
                        {
                            type = "ColumnSet",
                            columns = new object[]
                            {
                                new
                                {
                                    type = "Column",
                                    width = "auto",
                                    items = new object[]
                                    {
                                        new
                                        {
                                            type = "TextBlock",
                                            text = "🥃",
                                            size = "extraLarge"
                                        }
                                    }
                                },
                                new
                                {
                                    type = "Column",
                                    width = "stretch",
                                    verticalContentAlignment = "center",
                                    items = new object[]
                                    {
                                        new
                                        {
                                            type = "TextBlock",
                                            text = title,
                                            weight = "bolder",
                                            size = "large",
                                            color = "accent"
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                // Intro message
                new
                {
                    type = "TextBlock",
                    text = intro,
                    wrap = true,
                    spacing = "medium"
                },
                // Suggested actions header
                new
                {
                    type = "TextBlock",
                    text = "**💡 Try asking:**",
                    weight = "bolder",
                    spacing = "medium",
                    size = "medium"
                },
                // Suggested actions list
                new
                {
                    type = "TextBlock",
                    text = "• **Show shipment status** — Track SH-2025-042\n• **Analyze trends** — Performance over time\n• **Generate report** — Charts and insights",
                    wrap = true,
                    spacing = "small"
                }
            }
        };

        return new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = card
        };
    }

    /// <summary>
    /// Builds an error card for API failures
    /// </summary>
    public Attachment BuildErrorCard(string errorMessage)
    {
        var card = new
        {
            type = "AdaptiveCard",
            version = "1.8",
            schema = "http://adaptivecards.io/schemas/adaptive-card.json",
            body = new object[]
            {
                new
                {
                    type = "Container",
                    style = "attention",
                    items = new object[]
                    {
                        new
                        {
                            type = "ColumnSet",
                            columns = new object[]
                            {
                                new
                                {
                                    type = "Column",
                                    width = "auto",
                                    items = new object[]
                                    {
                                        new
                                        {
                                            type = "TextBlock",
                                            text = "⚠️",
                                            size = "large"
                                        }
                                    }
                                },
                                new
                                {
                                    type = "Column",
                                    width = "stretch",
                                    verticalContentAlignment = "center",
                                    items = new object[]
                                    {
                                        new
                                        {
                                            type = "TextBlock",
                                            text = "Error",
                                            weight = "bolder",
                                            size = "large",
                                            color = "attention"
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                new
                {
                    type = "TextBlock",
                    text = $"Sorry, I encountered an error while processing your request:",
                    wrap = true,
                    spacing = "medium"
                },
                new
                {
                    type = "TextBlock",
                    text = errorMessage,
                    wrap = true,
                    spacing = "small",
                    fontType = "monospace",
                    isSubtle = true
                },
                new
                {
                    type = "TextBlock",
                    text = "Please try again or rephrase your question.",
                    wrap = true,
                    spacing = "small",
                    isSubtle = true
                }
            },
            actions = new object[]
            {
                new
                {
                    type = "Action.Submit",
                    title = "🔄 Try Again",
                    data = new
                    {
                        action = "retry"
                    }
                }
            }
        };

        return new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = card
        };
    }

    /// <summary>
    /// Builds a simple typing indicator card (optional)
    /// </summary>
    public Attachment BuildTypingCard()
    {
        var card = new
        {
            type = "AdaptiveCard",
            version = "1.8",
            schema = "http://adaptivecards.io/schemas/adaptive-card.json",
            body = new object[]
            {
                new
                {
                    type = "ColumnSet",
                    columns = new object[]
                    {
                        new
                        {
                            type = "Column",
                            width = "auto",
                            items = new object[]
                            {
                                new
                                {
                                    type = "TextBlock",
                                    text = "🥃",
                                    size = "medium"
                                }
                            }
                        },
                        new
                        {
                            type = "Column",
                            width = "stretch",
                            verticalContentAlignment = "center",
                            items = new object[]
                            {
                                new
                                {
                                    type = "TextBlock",
                                    text = "Thinking...",
                                    isSubtle = true,
                                    size = "small"
                                }
                            }
                        }
                    }
                }
            }
        };

        return new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = card
        };
    }

    /// <summary>
    /// Builds a detailed telemetry report card with full span details and waterfall visualization
    /// </summary>
    public Attachment BuildDetailedTelemetryCard(List<AgentSpan> spans)
    {
        var bodyItems = new List<object>
        {
            // Header
            new
            {
                type = "Container",
                style = "emphasis",
                items = new object[]
                {
                    new
                    {
                        type = "ColumnSet",
                        columns = new object[]
                        {
                            new
                            {
                                type = "Column",
                                width = "auto",
                                items = new object[]
                                {
                                    new
                                    {
                                        type = "TextBlock",
                                        text = "📋",
                                        size = "large"
                                    }
                                }
                            },
                            new
                            {
                                type = "Column",
                                width = "stretch",
                                items = new object[]
                                {
                                    new
                                    {
                                        type = "TextBlock",
                                        text = "Full Telemetry Report",
                                        weight = "bolder",
                                        size = "large",
                                        color = "accent"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        if (!spans.Any())
        {
            bodyItems.Add(new
            {
                type = "TextBlock",
                text = "No telemetry data available.",
                wrap = true,
                spacing = "medium",
                isSubtle = true
            });
        }
        else
        {
            var sortedSpans = spans.OrderBy(s => s.Timestamp).ToList();
            var totalMs = sortedSpans.Sum(s => s.DurationMs);
            var avgMs = totalMs / sortedSpans.Count;
            var slowestSpan = sortedSpans.OrderByDescending(s => s.DurationMs).First();
            var maxDuration = slowestSpan.DurationMs;

            // Summary statistics
            bodyItems.Add(new
            {
                type = "Container",
                spacing = "medium",
                items = new object[]
                {
                    new
                    {
                        type = "TextBlock",
                        text = "**📊 Summary**",
                        weight = "bolder",
                        size = "medium"
                    },
                    new
                    {
                        type = "FactSet",
                        spacing = "small",
                        facts = new object[]
                        {
                            new { title = "Total Duration", value = TelemetryFormatter.FormatDuration(totalMs) },
                            new { title = "Total Spans", value = sortedSpans.Count.ToString() },
                            new { title = "Average Duration", value = TelemetryFormatter.FormatDuration(avgMs) },
                            new { title = "Slowest Span", value = $"{TelemetryFormatter.TruncateName(slowestSpan.Name, 25)} ({TelemetryFormatter.FormatDuration(slowestSpan.DurationMs)})" }
                        }
                    }
                }
            });

            // Waterfall visualization
            bodyItems.Add(new
            {
                type = "Container",
                spacing = "medium",
                separator = true,
                items = new object[]
                {
                    new
                    {
                        type = "TextBlock",
                        text = "**⏱️ Timing Waterfall**",
                        weight = "bolder",
                        size = "medium"
                    }
                }
            });

            foreach (var span in sortedSpans)
            {
                var icon = TelemetryFormatter.GetSpanIcon(span.Type);
                var formattedDuration = TelemetryFormatter.FormatDuration(span.DurationMs);
                var widthPercent = TelemetryFormatter.CalculateWaterfallWidth(span.DurationMs, maxDuration);

                bodyItems.Add(new
                {
                    type = "Container",
                    spacing = "small",
                    items = new object[]
                    {
                        new
                        {
                            type = "ColumnSet",
                            columns = new object[]
                            {
                                new
                                {
                                    type = "Column",
                                    width = "auto",
                                    items = new object[]
                                    {
                                        new
                                        {
                                            type = "TextBlock",
                                            text = icon,
                                            size = "small"
                                        }
                                    }
                                },
                                new
                                {
                                    type = "Column",
                                    width = "stretch",
                                    items = new object[]
                                    {
                                        new
                                        {
                                            type = "TextBlock",
                                            text = $"{(span.DurationMs == maxDuration ? "🔥 " : "")}{span.Name}",
                                            size = "small",
                                            weight = span.DurationMs == maxDuration ? "bolder" : "default",
                                            wrap = true
                                        }
                                    }
                                },
                                new
                                {
                                    type = "Column",
                                    width = "auto",
                                    items = new object[]
                                    {
                                        new
                                        {
                                            type = "TextBlock",
                                            text = formattedDuration,
                                            size = "small",
                                            fontType = "monospace",
                                            color = span.DurationMs == maxDuration ? "attention" : "default"
                                        }
                                    }
                                }
                            }
                        },
                        // Visual waterfall bar
                        new
                        {
                            type = "ColumnSet",
                            spacing = "none",
                            columns = new object[]
                            {
                                new
                                {
                                    type = "Column",
                                    width = $"{widthPercent}px",
                                    items = new object[]
                                    {
                                        new
                                        {
                                            type = "TextBlock",
                                            text = "█",
                                            color = span.DurationMs == maxDuration ? "attention" : "accent"
                                        }
                                    }
                                }
                            }
                        }
                    }
                });
            }

            // Detailed span list
            bodyItems.Add(new
            {
                type = "Container",
                spacing = "medium",
                separator = true,
                items = new object[]
                {
                    new
                    {
                        type = "TextBlock",
                        text = "**📝 Detailed Spans**",
                        weight = "bolder",
                        size = "medium"
                    }
                }
            });

            foreach (var span in sortedSpans)
            {
                var icon = TelemetryFormatter.GetSpanIcon(span.Type);
                var typeBadge = TelemetryFormatter.GetTypeBadge(span.Type);
                var formattedDuration = TelemetryFormatter.FormatDuration(span.DurationMs);

                var spanDetailItems = new List<object>
                {
                    new
                    {
                        type = "ColumnSet",
                        columns = new object[]
                        {
                            new
                            {
                                type = "Column",
                                width = "auto",
                                items = new object[]
                                {
                                    new
                                    {
                                        type = "TextBlock",
                                        text = icon,
                                        size = "small"
                                    }
                                }
                            },
                            new
                            {
                                type = "Column",
                                width = "stretch",
                                items = new object[]
                                {
                                    new
                                    {
                                        type = "TextBlock",
                                        text = $"**{span.Name}**",
                                        wrap = true,
                                        size = "small"
                                    }
                                }
                            },
                            new
                            {
                                type = "Column",
                                width = "auto",
                                items = new object[]
                                {
                                    new
                                    {
                                        type = "TextBlock",
                                        text = typeBadge,
                                        size = "small",
                                        weight = "bolder",
                                        color = "accent"
                                    }
                                }
                            },
                            new
                            {
                                type = "Column",
                                width = "auto",
                                items = new object[]
                                {
                                    new
                                    {
                                        type = "TextBlock",
                                        text = formattedDuration,
                                        size = "small",
                                        fontType = "monospace"
                                    }
                                }
                            }
                        }
                    }
                };

                // Add full detail text if available
                if (!string.IsNullOrWhiteSpace(span.Detail))
                {
                    spanDetailItems.Add(new
                    {
                        type = "TextBlock",
                        text = span.Detail,
                        wrap = true,
                        size = "small",
                        isSubtle = true,
                        spacing = "small"
                    });
                }

                bodyItems.Add(new
                {
                    type = "Container",
                    spacing = "small",
                    separator = true,
                    items = spanDetailItems.ToArray()
                });
            }
        }

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.8",
            schema = "http://adaptivecards.io/schemas/adaptive-card.json",
            body = bodyItems.ToArray()
        };

        return new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = card
        };
    }

    /// <summary>
    /// Maps hex colors from ChartSpec to Adaptive Card named colors
    /// </summary>
    private static string MapToAdaptiveCardColor(string? hexColor, int index)
    {
        if (!string.IsNullOrEmpty(hexColor))
        {
            var mapped = hexColor.ToUpperInvariant() switch
            {
                "#1B4D7A" => "categoricalBlue",
                "#E8A838" => "categoricalMarigold",
                "#4682B4" => "categoricalBlue",
                "#2E8B57" => "categoricalTeal",
                "#CD853F" => "categoricalPeach",
                "#B8860B" => "categoricalMarigold",
                "#5F9EA0" => "categoricalLavender",
                "#D2691E" => "categoricalRed",
                _ => (string?)null
            };
            if (mapped != null) return mapped;
        }

        var defaultColors = new[]
        {
            "categoricalBlue", "categoricalRed", "categoricalTeal", "categoricalMarigold",
            "categoricalPeach", "categoricalLavender", "categoricalSteel", "categoricalPumpkin"
        };
        return defaultColors[index % defaultColors.Length];
    }

    /// <summary>
    /// Gets the API base URL from configuration
    /// </summary>
    private string GetApiBaseUrl()
    {
        // Try Aspire service discovery first
        var apiUrl = _configuration["services:api:https:0"]
            ?? _configuration["services:api:http:0"]
            ?? "http://localhost:5000";

        return apiUrl.TrimEnd('/');
    }

    /// <summary>
    /// Resolves relative image URLs to absolute URLs
    /// </summary>
    private string ResolveImageUrl(string url, string baseUrl)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            // Already absolute
            return url;
        }

        // Relative URL - resolve against API base
        var cleanUrl = url.TrimStart('/');
        return $"{baseUrl}/{cleanUrl}";
    }
}
