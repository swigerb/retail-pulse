using FluentAssertions;
using RetailPulse.Api.Validation;
using RetailPulse.Contracts;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests for ChatRequestValidator covering edge cases: empty, too long, XSS, format issues.
/// </summary>
public class ChatRequestValidatorTests
{
    [Fact]
    public void NullRequest_ReturnsInvalid()
    {
        ValidationResult result = ChatRequestValidator.Validate(null);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("request");
    }

    [Fact]
    public void EmptyMessage_ReturnsInvalid()
    {
        var request = new ChatRequest("", "session1");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("message");
        result.Errors["message"].Should().Contain(e => e.Contains("required"));
    }

    [Fact]
    public void WhitespaceMessage_ReturnsInvalid()
    {
        var request = new ChatRequest("   \t\n  ", "session1");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("message");
    }

    [Fact]
    public void MessageExceedsMaxLength_ReturnsInvalid()
    {
        string longMessage = new('X', ChatRequestValidator.MaxMessageLength + 1);
        var request = new ChatRequest(longMessage, "session1");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("message");
        result.Errors["message"].Should().Contain(e => e.Contains("4000"));
    }

    [Fact]
    public void MessageAtMaxLength_IsValid()
    {
        string maxMessage = new('X', ChatRequestValidator.MaxMessageLength);
        var request = new ChatRequest(maxMessage, "session1");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidRequest_ReturnsValid()
    {
        var request = new ChatRequest("What are my top depleting brands?", "abc123");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void NullSessionId_IsValid()
    {
        var request = new ChatRequest("Hello", null);

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("valid-session-id")]
    [InlineData("abc123")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]
    public void ValidSessionIdFormats_AreAccepted(string sessionId)
    {
        var request = new ChatRequest("Hello", sessionId);

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("session with spaces")]
    [InlineData("session/path")]
    [InlineData("'; DROP TABLE --")]
    [InlineData("<script>alert(1)</script>")]
    public void InvalidSessionIdFormats_AreRejected(string sessionId)
    {
        var request = new ChatRequest("Hello", sessionId);

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("sessionId");
    }

    [Fact]
    public void SessionIdExceeding64Chars_IsRejected()
    {
        string longSessionId = new('a', 65);
        var request = new ChatRequest("Hello", longSessionId);

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("sessionId");
    }

    [Theory]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("javascript:void(0)")]
    public void XssPayloadsInMessage_AreAcceptedForGuardrailProcessing(string xssPayload)
    {
        // XSS in messages is handled by the guardrails middleware, not input validation
        var request = new ChatRequest(xssPayload, "session1");

        ValidationResult result = ChatRequestValidator.Validate(request);

        result.IsValid.Should().BeTrue(
            "content safety is enforced by guardrails, not by the format validator");
    }
}
