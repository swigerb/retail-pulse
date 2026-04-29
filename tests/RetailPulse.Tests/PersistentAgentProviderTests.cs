using Azure.AI.Agents.Persistent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents;

namespace RetailPulse.Tests;

/// <summary>
/// Tests for <see cref="PersistentAgentProvider{TAgent}"/> focusing on resolution
/// behavior that can be exercised without a live Foundry endpoint.
/// </summary>
public class PersistentAgentProviderTests
{
    // Marker interface for the generic constraint
    public interface ITestAgent { }
    public class TestAgent : ITestAgent { }

    [Fact]
    public async Task DirectAgentId_BypassesNameResolution_AndReturnsConfiguredId()
    {
        var options = new AgentResolutionOptions
        {
            FriendlyName = "Test Agent",
            ProjectEndpoint = "https://example.foundry.azure.com",
            DirectAgentId = "asst_directbypass123"
        };

        // Client is never invoked when DirectAgentId is set, so a null-forgiving
        // placeholder is acceptable here. We pass a non-null sentinel by
        // constructing through reflection to avoid the network-bound real client.
            var provider = new PersistentAgentProvider<TestAgent>(
                options,
                client: null!,
                NullLogger<PersistentAgentProvider<TestAgent>>.Instance);

        var info = await provider.GetAgentInfoAsync();

        info.Id.Should().Be("asst_directbypass123");
        info.FriendlyName.Should().Be("Test Agent");
        info.Runtime.Should().Be("Azure AI Foundry");
    }

    [Fact]
    public async Task DirectAgentId_GetAgentIdAsync_ReturnsSameId()
    {
        var options = new AgentResolutionOptions
        {
            FriendlyName = "Test",
            ProjectEndpoint = "https://example.foundry.azure.com",
            DirectAgentId = "asst_xyz"
        };

        var provider = new PersistentAgentProvider<TestAgent>(
            options,
            client: null!,
            NullLogger<PersistentAgentProvider<TestAgent>>.Instance);

        var id = await provider.GetAgentIdAsync();
        id.Should().Be("asst_xyz");
    }

    [Fact]
    public async Task DirectAgentId_ResultIsCached_AcrossCalls()
    {
        var options = new AgentResolutionOptions
        {
            FriendlyName = "Test",
            ProjectEndpoint = "https://example.foundry.azure.com",
            DirectAgentId = "asst_cache"
        };

        var provider = new PersistentAgentProvider<TestAgent>(
            options,
            client: null!,
            NullLogger<PersistentAgentProvider<TestAgent>>.Instance);

        var first = await provider.GetAgentInfoAsync();
        var second = await provider.GetAgentInfoAsync();

        // Cached AgentInfo should be the same instance
        ReferenceEquals(first, second).Should().BeTrue("resolved agent info is cached after the first call");
    }

    [Fact]
    public void GetAgentsClient_ReturnsConfiguredInstance()
    {
        var options = new AgentResolutionOptions
        {
            FriendlyName = "Test",
            ProjectEndpoint = "https://example.foundry.azure.com",
            DirectAgentId = "asst_x"
        };

        // Use a sentinel client reference and ensure it round-trips
        PersistentAgentsClient? sentinel = null;
        var provider = new PersistentAgentProvider<TestAgent>(
            options,
            client: sentinel!,
            NullLogger<PersistentAgentProvider<TestAgent>>.Instance);

        provider.GetAgentsClient().Should().BeSameAs(sentinel);
    }

    [Fact]
    public void Options_RequireFriendlyNameAndProjectEndpoint()
    {
        // Validates that the required-modifier contract is preserved. If a future
        // change drops 'required', this test forces the team to revisit it.
        var props = typeof(AgentResolutionOptions).GetProperties();
        props.Should().Contain(p => p.Name == nameof(AgentResolutionOptions.FriendlyName));
        props.Should().Contain(p => p.Name == nameof(AgentResolutionOptions.ProjectEndpoint));
        props.Should().Contain(p => p.Name == nameof(AgentResolutionOptions.DirectAgentId));
    }
}
