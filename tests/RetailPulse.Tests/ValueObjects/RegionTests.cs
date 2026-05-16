using FluentAssertions;
using RetailPulse.Contracts.ValueObjects;

namespace RetailPulse.Tests.ValueObjects;

public class RegionTests
{
    [Theory]
    [InlineData("Northeast")]
    [InlineData("Southeast")]
    [InlineData("Midwest")]
    [InlineData("Southwest")]
    [InlineData("West Coast")]
    [InlineData("Pacific Northwest")]
    public void Constructor_WithKnownRegion_CreatesInstance(string regionName)
    {
        var region = new Region(regionName);
        region.Value.Should().Be(regionName);
    }

    [Fact]
    public void Constructor_CaseInsensitive()
    {
        var region = new Region("northeast");
        region.Value.Should().Be("northeast");
    }

    [Fact]
    public void Constructor_WithUnknownRegion_Throws()
    {
        Func<Region> act = () => new Region("Mars Colony");
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown region*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNullOrEmpty_Throws(string? value)
    {
        Func<Region> act = () => new Region(value!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromUnchecked_AllowsAnyValue()
    {
        var region = Region.FromUnchecked("Custom Territory");
        region.Value.Should().Be("Custom Territory");
    }

    [Fact]
    public void ImplicitConversion_FromString_Works()
    {
        Region region = "Midwest";
        region.Value.Should().Be("Midwest");
    }

    [Fact]
    public void ImplicitConversion_ToString_Works()
    {
        var region = new Region("Southwest");
        string value = region;
        value.Should().Be("Southwest");
    }

    [Fact]
    public void IsKnown_ReturnsTrueForKnownRegions()
    {
        Region.IsKnown("Northeast").Should().BeTrue();
        Region.IsKnown("Unknown Place").Should().BeFalse();
    }

    [Fact]
    public void Equality_ByValue()
    {
        var a = new Region("Midwest");
        var b = new Region("Midwest");
        a.Should().Be(b);
    }
}
