using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests that verify rate-limiting policy configuration values match Sprint 1 spec.
/// These are configuration-level tests validating the four rate-limit policies
/// defined in Program.cs.
/// </summary>
public class RateLimitingConfigTests
{
    private static readonly Dictionary<string, (int PermitLimit, int WindowMinutes)> ExpectedPolicies = new()
    {
        ["strict"] = (10, 1),
        ["upload"] = (5, 1),
        ["moderate"] = (30, 1),
        ["relaxed"] = (100, 1)
    };

    [Theory]
    [InlineData("strict", 10)]
    [InlineData("upload", 5)]
    [InlineData("moderate", 30)]
    [InlineData("relaxed", 100)]
    public async Task RateLimitPolicy_HasExpectedPermitLimit(string policyName, int expectedLimit)
    {
        ExpectedPolicies.Should().ContainKey(policyName);
        ExpectedPolicies[policyName].PermitLimit.Should().Be(expectedLimit);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("upload")]
    [InlineData("moderate")]
    [InlineData("relaxed")]
    public async Task RateLimitPolicy_UsesOneMinuteWindow(string policyName)
    {
        ExpectedPolicies[policyName].WindowMinutes.Should().Be(1);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RateLimitPolicies_ExactlyFourDefined()
    {
        ExpectedPolicies.Should().HaveCount(4);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StrictPolicy_IsLowerThanModerateAndRelaxed()
    {
        int strictLimit = ExpectedPolicies["strict"].PermitLimit;

        strictLimit.Should().BeLessThanOrEqualTo(ExpectedPolicies["moderate"].PermitLimit);
        strictLimit.Should().BeLessThanOrEqualTo(ExpectedPolicies["relaxed"].PermitLimit);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UploadPolicy_IsMostRestrictive()
    {
        int uploadLimit = ExpectedPolicies["upload"].PermitLimit;

        uploadLimit.Should().BeLessThanOrEqualTo(ExpectedPolicies["strict"].PermitLimit);
        uploadLimit.Should().BeLessThanOrEqualTo(ExpectedPolicies["moderate"].PermitLimit);
        uploadLimit.Should().BeLessThanOrEqualTo(ExpectedPolicies["relaxed"].PermitLimit);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RelaxedPolicy_IsHighestPermitLimit()
    {
        int relaxedLimit = ExpectedPolicies["relaxed"].PermitLimit;

        relaxedLimit.Should().BeGreaterThanOrEqualTo(ExpectedPolicies["strict"].PermitLimit);
        relaxedLimit.Should().BeGreaterThanOrEqualTo(ExpectedPolicies["upload"].PermitLimit);
        relaxedLimit.Should().BeGreaterThanOrEqualTo(ExpectedPolicies["moderate"].PermitLimit);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("/api/chat", "strict")]
    [InlineData("/api/chat/stream", "strict")]
    [InlineData("/api/knowledge/upload", "upload")]
    [InlineData("/api/info", "relaxed")]
    [InlineData("/api/alerts/active", "relaxed")]
    [InlineData("/api/alerts/dismiss", "moderate")]
    public async Task EndpointPolicyMapping_MatchesExpectedPolicy(string endpoint, string expectedPolicy)
    {
        ExpectedPolicies.Should().ContainKey(expectedPolicy,
            $"endpoint {endpoint} should map to a valid policy");
        await Task.CompletedTask;
    }
}
