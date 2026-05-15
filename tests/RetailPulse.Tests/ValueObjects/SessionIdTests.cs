using FluentAssertions;
using RetailPulse.Contracts.ValueObjects;

namespace RetailPulse.Tests.ValueObjects;

public class SessionIdTests
{
    [Theory]
    [InlineData("abc123")]
    [InlineData("session-001")]
    [InlineData("a1b2c3-d4e5")]
    [InlineData("UPPER-lower-123")]
    public void Constructor_WithValidFormat_CreatesInstance(string value)
    {
        var sessionId = new SessionId(value);
        sessionId.Value.Should().Be(value);
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("special!chars")]
    [InlineData("dots.not.allowed")]
    [InlineData("under_score")]
    public void Constructor_WithInvalidFormat_Throws(string value)
    {
        var act = () => new SessionId(value);
        act.Should().Throw<ArgumentException>().WithMessage("*alphanumeric*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNullOrEmpty_Throws(string? value)
    {
        var act = () => new SessionId(value!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsValid_ReturnsTrueForValidFormat()
    {
        SessionId.IsValid("abc-123").Should().BeTrue();
        SessionId.IsValid("no spaces").Should().BeFalse();
        SessionId.IsValid(null).Should().BeFalse();
        SessionId.IsValid("").Should().BeFalse();
    }

    [Fact]
    public void ImplicitConversion_FromString_Works()
    {
        SessionId id = "my-session-42";
        id.Value.Should().Be("my-session-42");
    }

    [Fact]
    public void ImplicitConversion_ToString_Works()
    {
        var id = new SessionId("sess-99");
        string value = id;
        value.Should().Be("sess-99");
    }

    [Fact]
    public void Equality_ByValue()
    {
        var a = new SessionId("abc-123");
        var b = new SessionId("abc-123");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }
}
