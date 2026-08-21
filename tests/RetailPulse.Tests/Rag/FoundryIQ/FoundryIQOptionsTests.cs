using FluentAssertions;
using RetailPulse.Api.Rag.FoundryIQ;

namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>
/// Options-only unit coverage for the Foundry IQ knowledge provider.
/// <see cref="FoundryIQOptions.IsConfigured"/> is the fully-optional gate the
/// DI extension checks, and <see cref="FoundryIQOptions.ValidateEnabled"/>
/// enforces fail-fast startup on the enabled path.
/// </summary>
public sealed class FoundryIQOptionsTests
{
    [Fact]
    public void IsConfigured_BlankEndpoint_ReturnsFalse()
    {
        var options = new FoundryIQOptions();
        options.IsConfigured.Should().BeFalse(
            "no endpoint means the provider must stay unregistered — this is the fully-optional gate");
    }

    [Fact]
    public void IsConfigured_EndpointWithoutVectorStoreSelector_ReturnsFalse()
    {
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
        };
        options.IsConfigured.Should().BeFalse(
            "a vector store binding is required — endpoint alone is not enough to serve retrieval");
    }

    [Fact]
    public void IsConfigured_EndpointWithVectorStoreName_ReturnsTrue()
    {
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreName = "retail-corpus",
        };
        options.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_EndpointWithVectorStoreId_ReturnsTrue()
    {
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreId = "vs_abc123",
        };
        options.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void ValidateEnabled_MissingModel_ThrowsActionableMessage()
    {
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreName = "retail-corpus",
        };
        Action act = () => options.ValidateEnabled();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Knowledge:FoundryIQ:Model*");
    }

    [Fact]
    public void ValidateEnabled_NonAbsoluteEndpoint_ThrowsActionableMessage()
    {
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "foundry.example/api/projects/p",
            VectorStoreName = "retail-corpus",
            Model = "gpt-5.4-mini",
        };
        Action act = () => options.ValidateEnabled();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*absolute http(s) URL*");
    }

    [Fact]
    public void ValidateEnabled_MissingAgentIdentifier_ThrowsWhenBothBlank()
    {
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreName = "retail-corpus",
            Model = "gpt-5.4-mini",
            RetrievalAgentName = "",
            RetrievalAgentId = "",
        };
        Action act = () => options.ValidateEnabled();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RetrievalAgentName*RetrievalAgentId*");
    }

    [Fact]
    public void ValidateEnabled_HappyPath_DoesNotThrow()
    {
        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreName = "retail-corpus",
            Model = "gpt-5.4-mini",
        };
        Action act = () => options.ValidateEnabled();
        act.Should().NotThrow();
    }
}
