using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using RetailPulse.Contracts;
using RetailPulse.TeamsBot.Cards;
using RetailPulse.TeamsBot.Models;
using System.Text.Json;

namespace RetailPulse.Tests.Cards;

public class AdaptiveCardBuilderTests
{
    private readonly AdaptiveCardBuilder _builder;
    private readonly Mock<IConfiguration> _mockConfiguration;

    public AdaptiveCardBuilderTests()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(x => x["services:api:https:0"]).Returns("https://localhost:5001");
        _builder = new AdaptiveCardBuilder(_mockConfiguration.Object);
    }

    [Fact]
    public void BuildChatResponseCard_TextOnly_CreatesValidCard()
    {
        // Arrange
        var reply = "This is a test reply from the AI agent.";
        
        // Act
        var attachment = _builder.BuildChatResponseCard(reply, null, null);
        
        // Assert
        attachment.Should().NotBeNull();
        attachment.ContentType.Should().Be("application/vnd.microsoft.card.adaptive");
        
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Pulse"); // Check for "Pulse" without special chars
        cardJson.Should().Contain(reply);
    }

    [Fact]
    public void BuildChatResponseCard_HasCorrectVersion()
    {
        // Arrange
        var reply = "Test reply";
        
        // Act
        var attachment = _builder.BuildChatResponseCard(reply, null, null);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        var cardObject = JsonDocument.Parse(cardJson);
        
        cardObject.RootElement.GetProperty("version").GetString().Should().Be("1.8");
    }

    [Fact]
    public void BuildChatResponseCard_WithSpans_IncludesTelemetrySection()
    {
        // Arrange
        var reply = "Test reply with telemetry";
        var spans = new List<AgentSpan>
        {
            new AgentSpan("Thinking", "thought", "Analyzing request", 150, DateTimeOffset.UtcNow),
            new AgentSpan("Tool Call", "tool_call", "Fetching data", 250, DateTimeOffset.UtcNow)
        };
        
        // Act
        var attachment = _builder.BuildChatResponseCard(reply, spans, null);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Telemetry Summary");
        cardJson.Should().Contain("Thinking");
        cardJson.Should().Contain("Tool Call");
        cardJson.Should().Contain("View Telemetry");
    }

    [Fact]
    public void BuildChatResponseCard_WithCharts_IncludesChartSection()
    {
        // Arrange
        var reply = "Here's your chart";
        var charts = new List<ChartSpec>
        {
            new ChartSpec
            {
                Type = "bar",
                Title = "Sales by Region",
                XAxisTitle = "Region",
                YAxisTitle = "Cases",
                Data = new List<ChartSeries>
                {
                    new ChartSeries
                    {
                        Legend = "Patrón Silver",
                        Color = "#8B4513",
                        Values = new List<ChartDataPoint>
                        {
                            new ChartDataPoint { X = "Florida", Y = 1200 },
                            new ChartDataPoint { X = "Texas", Y = 950 }
                        }
                    }
                }
            }
        };
        
        // Act
        var attachment = _builder.BuildChatResponseCard(reply, null, charts);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Charts");
        cardJson.Should().Contain("Sales by Region");
        cardJson.Should().Contain("Chart.VerticalBar");
    }

    [Fact]
    public void BuildChatResponseCard_WithSpansAndCharts_IncludesBothSections()
    {
        // Arrange
        var reply = "Complete response with everything";
        var spans = new List<AgentSpan>
        {
            new AgentSpan("Generate Chart", "tool_call", "Creating visualization", 500, DateTimeOffset.UtcNow)
        };
        var charts = new List<ChartSpec>
        {
            new ChartSpec
            {
                Type = "line",
                Title = "Trend Analysis",
                Data = new List<ChartSeries>
                {
                    new ChartSeries
                    {
                        Legend = "Sales",
                        Values = new List<ChartDataPoint>
                        {
                            new ChartDataPoint { X = "Jan", Y = 100 },
                            new ChartDataPoint { X = "Feb", Y = 150 }
                        }
                    }
                }
            }
        };
        
        // Act
        var attachment = _builder.BuildChatResponseCard(reply, spans, charts);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Charts");
        cardJson.Should().Contain("Telemetry Summary");
        cardJson.Should().Contain("Generate Chart");
        cardJson.Should().Contain("Chart.Line");
    }

    [Fact]
    public void BuildChatResponseCard_WithSessionId_IncludesFullTelemetryButton()
    {
        // Arrange
        var reply = "Test with session";
        var sessionId = "test-session-123";
        var spans = new List<AgentSpan>
        {
            new AgentSpan("Test", "thought", "Testing", 100, DateTimeOffset.UtcNow)
        };
        
        // Act
        var attachment = _builder.BuildChatResponseCard(reply, spans, null, sessionId);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Full Telemetry Report");
        cardJson.Should().Contain(sessionId);
    }

    [Fact]
    public void BuildWelcomeCard_NewUser_IncludesBrandingAndSuggestedActions()
    {
        // Arrange
        var userContext = new UserContext("user-123", "John Doe", "john@example.com");
        
        // Act
        var attachment = _builder.BuildWelcomeCard(false, userContext);
        
        // Assert
        attachment.Should().NotBeNull();
        attachment.ContentType.Should().Be("application/vnd.microsoft.card.adaptive");
        
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Welcome to");
        cardJson.Should().Contain("Pulse");
        cardJson.Should().Contain("John Doe");
        cardJson.Should().Contain("Try asking");
        cardJson.Should().Contain("Show shipment status");
    }

    [Fact]
    public void BuildWelcomeCard_Reset_ShowsResetMessage()
    {
        // Arrange
        var userContext = new UserContext("user-123", "Jane Doe", "jane@example.com");
        
        // Act
        var attachment = _builder.BuildWelcomeCard(true, userContext);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Chat Reset");
        cardJson.Should().Contain("conversation has been reset");
    }

    [Fact]
    public void BuildWelcomeCard_HasCorrectVersion()
    {
        // Act
        var attachment = _builder.BuildWelcomeCard(false, null);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        var cardObject = JsonDocument.Parse(cardJson);
        
        cardObject.RootElement.GetProperty("version").GetString().Should().Be("1.8");
    }

    [Fact]
    public void BuildErrorCard_IncludesErrorMessage()
    {
        // Arrange
        var errorMessage = "Connection to API failed";
        
        // Act
        var attachment = _builder.BuildErrorCard(errorMessage);
        
        // Assert
        attachment.Should().NotBeNull();
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Error");
        cardJson.Should().Contain(errorMessage);
        cardJson.Should().Contain("Try Again");
    }

    [Fact]
    public void BuildErrorCard_HasCorrectVersion()
    {
        // Act
        var attachment = _builder.BuildErrorCard("Test error");
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        var cardObject = JsonDocument.Parse(cardJson);
        
        cardObject.RootElement.GetProperty("version").GetString().Should().Be("1.8");
    }

    [Fact]
    public void BuildDetailedTelemetryCard_EmptySpans_ShowsNoDataMessage()
    {
        // Arrange
        var spans = new List<AgentSpan>();
        
        // Act
        var attachment = _builder.BuildDetailedTelemetryCard(spans);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Full Telemetry Report");
        cardJson.Should().Contain("No telemetry data available");
    }

    [Fact]
    public void BuildDetailedTelemetryCard_WithSpans_IncludesSummaryAndWaterfall()
    {
        // Arrange
        var spans = new List<AgentSpan>
        {
            new AgentSpan("Fast Operation", "tool_call", "Quick task", 50, DateTimeOffset.UtcNow),
            new AgentSpan("Slow Operation", "tool_call", "Long task", 1500, DateTimeOffset.UtcNow.AddMilliseconds(50)),
            new AgentSpan("Medium Operation", "thought", "Thinking", 300, DateTimeOffset.UtcNow.AddMilliseconds(1550))
        };
        
        // Act
        var attachment = _builder.BuildDetailedTelemetryCard(spans);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Summary");
        cardJson.Should().Contain("Total Duration");
        cardJson.Should().Contain("Total Spans");
        cardJson.Should().Contain("Average Duration");
        cardJson.Should().Contain("Timing Waterfall");
        cardJson.Should().Contain("Detailed Spans");
        cardJson.Should().Contain("Fast Operation");
        cardJson.Should().Contain("Slow Operation");
        cardJson.Should().Contain("Medium Operation");
    }

    [Fact]
    public void BuildDetailedTelemetryCard_HighlightsSlowestSpan()
    {
        // Arrange
        var spans = new List<AgentSpan>
        {
            new AgentSpan("Fast", "tool_call", "Quick", 50, DateTimeOffset.UtcNow),
            new AgentSpan("Slowest", "tool_call", "Very slow", 2000, DateTimeOffset.UtcNow.AddMilliseconds(50))
        };
        
        // Act
        var attachment = _builder.BuildDetailedTelemetryCard(spans);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Slowest Span");
        cardJson.Should().Contain("Slowest");
    }

    [Fact]
    public void BuildTypingCard_CreatesValidCard()
    {
        // Act
        var attachment = _builder.BuildTypingCard();
        
        // Assert
        attachment.Should().NotBeNull();
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        cardJson.Should().Contain("Thinking");
    }

    [Fact]
    public void BuildChatResponseCard_ProducesValidJson()
    {
        // Arrange
        var reply = "Valid JSON test";
        var spans = new List<AgentSpan>
        {
            new AgentSpan("Test Span", "thought", "Testing JSON", 100, DateTimeOffset.UtcNow)
        };
        
        // Act
        var attachment = _builder.BuildChatResponseCard(reply, spans, null);
        
        // Assert
        var cardJson = JsonSerializer.Serialize(attachment.Content);
        var parseAction = () => JsonDocument.Parse(cardJson);
        parseAction.Should().NotThrow("Card should produce valid JSON");
    }
}
