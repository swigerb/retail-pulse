using FluentAssertions;
using RetailPulse.Contracts.ValueObjects;

namespace RetailPulse.Tests.ValueObjects;

public class BrandNameTests
{
    [Fact]
    public void Constructor_WithValidName_CreatesInstance()
    {
        var brand = new BrandName("Sierra Gold Tequila");
        brand.Value.Should().Be("Sierra Gold Tequila");
    }

    [Fact]
    public void Constructor_TrimsWhitespace()
    {
        var brand = new BrandName("  Sierra Gold  ");
        brand.Value.Should().Be("Sierra Gold");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidName_Throws(string? value)
    {
        Func<BrandName> act = () => new BrandName(value!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_FromString_Works()
    {
        BrandName brand = "Ridgeline Bourbon";
        brand.Value.Should().Be("Ridgeline Bourbon");
    }

    [Fact]
    public void ImplicitConversion_ToString_Works()
    {
        var brand = new BrandName("Summit Vodka");
        string value = brand;
        value.Should().Be("Summit Vodka");
    }

    [Fact]
    public void Equality_ByValue()
    {
        var a = new BrandName("Sierra Gold Tequila");
        var b = new BrandName("Sierra Gold Tequila");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentValues_NotEqual()
    {
        var a = new BrandName("Sierra Gold");
        var b = new BrandName("Summit Vodka");
        a.Should().NotBe(b);
    }
}
