using FluentAssertions;
using RetailPulse.Api.Guardrails;

namespace RetailPulse.Tests.Middleware;

/// <summary>
/// Tests for PiiRedactor — regex-based PII detection and redaction.
/// Covers: SSN, email, phone, credit card, multiple PII, no PII,
/// false positives, partial matches.
/// </summary>
public class PiiRedactionTests
{
    #region SSN Redaction

    [Theory]
    [InlineData("My SSN is 123-45-6789", "My SSN is [REDACTED:ssn]")]
    [InlineData("SSN: 999-88-7777 on file", "SSN: [REDACTED:ssn] on file")]
    public void Redact_Ssn_ReplacedWithPlaceholder(string input, string expected)
    {
        PiiRedactor.Redact(input).Should().Be(expected);
    }

    [Fact]
    public void Redact_SsnWithSpaces_ReplacedWithPlaceholder()
    {
        PiiRedactor.Redact("SSN is 123 45 6789").Should().Be("SSN is [REDACTED:ssn]");
    }

    #endregion

    #region Email Redaction

    [Theory]
    [InlineData("Contact user@example.com for details", "Contact [REDACTED:email] for details")]
    [InlineData("Email: admin@company.org today", "Email: [REDACTED:email] today")]
    public void Redact_Email_ReplacedWithPlaceholder(string input, string expected)
    {
        PiiRedactor.Redact(input).Should().Be(expected);
    }

    [Fact]
    public void Redact_EmailWithSubdomain_Redacted()
    {
        var result = PiiRedactor.Redact("Send to user@mail.example.co.uk");
        result.Should().Contain("[REDACTED:email]");
        result.Should().NotContain("user@");
    }

    #endregion

    #region Phone Redaction

    [Theory]
    [InlineData("Call (555) 123-4567 now", "Call [REDACTED:phone] now")]
    [InlineData("Phone: 555-123-4567", "Phone: [REDACTED:phone]")]
    public void Redact_Phone_ReplacedWithPlaceholder(string input, string expected)
    {
        PiiRedactor.Redact(input).Should().Be(expected);
    }

    [Fact]
    public void Redact_PhoneWithDots_Redacted()
    {
        var result = PiiRedactor.Redact("Dial 555.123.4567 for support");
        result.Should().Contain("[REDACTED:phone]");
    }

    #endregion

    #region Credit Card Redaction

    [Fact]
    public void Redact_CreditCard_SpaceSeparated_Redacted()
    {
        var result = PiiRedactor.Redact("Card: 4111 1111 1111 1111");
        result.Should().Be("Card: [REDACTED:credit_card]");
    }

    [Fact]
    public void Redact_CreditCard_DashSeparated_Redacted()
    {
        var result = PiiRedactor.Redact("Card: 4111-1111-1111-1111");
        result.Should().Be("Card: [REDACTED:credit_card]");
    }

    [Fact]
    public void Redact_CreditCard_Contiguous_Redacted()
    {
        var result = PiiRedactor.Redact("Card: 4111111111111111");
        result.Should().Be("Card: [REDACTED:credit_card]");
    }

    #endregion

    #region Multiple PII in Same Response

    [Fact]
    public void Redact_MultiplePiiTypes_AllRedacted()
    {
        var input = "Contact user@example.com or call (555) 123-4567. SSN: 123-45-6789.";
        var result = PiiRedactor.Redact(input);

        result.Should().Contain("[REDACTED:email]");
        result.Should().Contain("[REDACTED:phone]");
        result.Should().Contain("[REDACTED:ssn]");
        result.Should().NotContain("user@example.com");
        result.Should().NotContain("555");
        result.Should().NotContain("123-45-6789");
    }

    [Fact]
    public void Redact_MultipleSameType_AllRedacted()
    {
        var input = "Emails: alice@corp.com and bob@corp.com";
        var result = PiiRedactor.Redact(input);

        // Both should be redacted
        result.Should().NotContain("alice@");
        result.Should().NotContain("bob@");
    }

    #endregion

    #region No PII — Response Unchanged

    [Theory]
    [InlineData("Sales for Q3 were strong across all regions.")]
    [InlineData("Brand X saw a 15% increase in demand.")]
    [InlineData("The promotion runs from March to June.")]
    [InlineData("Inventory is at 85% capacity.")]
    public void Redact_NoPii_ResponseUnchanged(string input)
    {
        PiiRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_EmptyString_ReturnsEmpty()
    {
        PiiRedactor.Redact("").Should().BeEmpty();
    }

    [Fact]
    public void Redact_Null_ReturnsNull()
    {
        PiiRedactor.Redact(null!).Should().BeNull();
    }

    #endregion

    #region PII-Like But Not PII (False Positives)

    [Fact]
    public void Redact_ProductCode_NotRedacted()
    {
        // Product codes with dashes should not trigger SSN redaction
        var input = "Product SKU: ABC-12-345";
        PiiRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_ShortNumber_NotRedacted()
    {
        // 5-digit numbers should not trigger anything
        var input = "Order #12345 is confirmed";
        PiiRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_PercentageWithDash_NotRedacted()
    {
        var input = "Growth was 12-15% year over year";
        PiiRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_DateWithSlashes_NotRedacted()
    {
        var input = "Report date: 01/15/2026";
        PiiRedactor.Redact(input).Should().Be(input);
    }

    #endregion

    #region ContainsPii Detection

    [Fact]
    public void ContainsPii_WithSsn_ReturnsTrue()
    {
        PiiRedactor.ContainsPii("My SSN is 123-45-6789").Should().BeTrue();
    }

    [Fact]
    public void ContainsPii_NoPii_ReturnsFalse()
    {
        PiiRedactor.ContainsPii("Normal text about sales").Should().BeFalse();
    }

    [Fact]
    public void ContainsPii_Empty_ReturnsFalse()
    {
        PiiRedactor.ContainsPii("").Should().BeFalse();
    }

    #endregion
}
