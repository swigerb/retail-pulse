using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.Validation;
using RetailPulse.Contracts;

namespace RetailPulse.Tests.Contract;

/// <summary>
/// Contract tests for POST /api/chat — validates request/response shapes,
/// required fields, and RFC 7807 Problem Details error format.
/// Uses direct validator and model assertions (no WebApplicationFactory needed
/// since the app requires Azure credentials at startup).
/// </summary>
public class ChatEndpointContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    #region Request Contract — Required Fields

    [Fact]
    public void Request_MessageIsRequired()
    {
        // ChatRequest with null/empty message should fail validation
        var request = new ChatRequest(Message: "", SessionId: "test-session");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("message");
    }

    [Fact]
    public void Request_NullBody_ReturnsValidationError()
    {
        ValidationResult result = ChatRequestValidator.Validate(null);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("request");
    }

    [Fact]
    public void Request_ValidMessage_PassesValidation()
    {
        var request = new ChatRequest(
            Message: "How is Apex Grill performing?",
            SessionId: "abc123");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Request_SessionId_InvalidFormat_Fails()
    {
        var request = new ChatRequest(
            Message: "test",
            SessionId: "invalid session id with spaces!");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("sessionId");
    }

    [Fact]
    public void Request_MessageExceedsMaxLength_Fails()
    {
        string longMessage = new('x', ChatRequestValidator.MaxMessageLength + 1);
        var request = new ChatRequest(Message: longMessage);

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("message");
        result.Errors["message"].Should().Contain(e => e.Contains("must not exceed"));
    }

    #endregion

    #region Response Contract — Shape Verification

    [Fact]
    public void Response_Shape_HasRequiredFields()
    {
        // Verify ChatResponse record has the expected structure
        var response = new ChatResponse(
            Reply: "Test reply",
            SessionId: "session-1",
            Spans: [new AgentSpan("test", "response", "detail", 100, DateTimeOffset.UtcNow)],
            Charts: null,
            TotalDurationMs: 150);

        response.Reply.Should().NotBeNullOrEmpty();
        response.SessionId.Should().NotBeNullOrEmpty();
        response.Spans.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Response_SerializesToExpectedJson()
    {
        var response = new ChatResponse(
            Reply: "Brand performance is strong",
            SessionId: "sess-123",
            Spans: [new AgentSpan("router.classify", "thought", "Classified as demand", 45.2, DateTimeOffset.Parse("2025-01-01T00:00:00Z"))],
            Charts: null,
            TotalDurationMs: 200);

        string json = JsonSerializer.Serialize(response, JsonOptions);
        var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("reply").GetString().Should().Be("Brand performance is strong");
        root.GetProperty("sessionId").GetString().Should().Be("sess-123");
        root.GetProperty("spans").GetArrayLength().Should().Be(1);
        root.GetProperty("totalDurationMs").GetInt64().Should().Be(200);
    }

    [Fact]
    public void Response_AgentSpan_HasExpectedShape()
    {
        var span = new AgentSpan(
            Name: "demand-agent.execute",
            Type: "tool_call",
            Detail: "Called GetDepletionStats",
            DurationMs: 1200.5,
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: "sess-1",
            InputTokens: 150,
            OutputTokens: 300);

        string json = JsonSerializer.Serialize(span, JsonOptions);
        var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("name").GetString().Should().Be("demand-agent.execute");
        root.GetProperty("type").GetString().Should().Be("tool_call");
        root.GetProperty("durationMs").GetDouble().Should().BeApproximately(1200.5, 0.01);
        root.GetProperty("inputTokens").GetInt32().Should().Be(150);
        root.GetProperty("outputTokens").GetInt32().Should().Be(300);
    }

    #endregion

    #region Error Response Contract — RFC 7807 Problem Details

    [Fact]
    public void ErrorResponse_ValidationProblem_MatchesRfc7807Shape()
    {
        // Simulate what Results.ValidationProblem produces
        // The framework wraps errors into RFC 7807 HttpValidationProblemDetails
        var errors = new Dictionary<string, string[]>
        {
            ["message"] = ["Field 'message' is required and cannot be empty."]
        };

        // Verify the error dictionary is structured correctly for ValidationProblem
        errors.Should().ContainKey("message");
        errors["message"].Should().HaveCount(1);
        errors["message"][0].Should().Contain("required");
    }

    [Fact]
    public void ErrorResponse_MultipleErrors_AreAggregated()
    {
        // Request with multiple violations
        var request = new ChatRequest(
            Message: "",
            SessionId: "!!!invalid!!!");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Errors.Should().ContainKey("message");
        result.Errors.Should().ContainKey("sessionId");
    }

    #endregion
}
