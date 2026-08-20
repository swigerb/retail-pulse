using FluentAssertions;
using RetailPulse.Api.Rag.AzureAISearch;

namespace RetailPulse.Tests.Rag.AzureAISearch;

/// <summary>
/// Options-shape tests. The provider is fully optional — a blank endpoint
/// must resolve to IsConfigured=false and never throw, and every enabled-path
/// validation must produce an actionable message naming the offending field.
/// </summary>
public class AzureAISearchOptionsTests
{
    [Fact]
    public void Default_IsNotConfigured()
    {
        var opts = new AzureAISearchOptions();
        opts.IsConfigured.Should().BeFalse(
            "blank endpoint means the provider is fully disabled and no client is materialized");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void BlankEndpoint_IsNotConfigured(string? endpoint)
    {
        var opts = new AzureAISearchOptions { Endpoint = endpoint };
        opts.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void ValidateEnabled_RejectsBlankEndpoint()
    {
        var opts = new AzureAISearchOptions();
        opts.Embeddings.Endpoint = "https://apim.example.com/inference";
        Action act = opts.ValidateEnabled;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Endpoint is required*");
    }

    [Fact]
    public void ValidateEnabled_RejectsNonAbsoluteEndpoint()
    {
        var opts = new AzureAISearchOptions
        {
            Endpoint = "not-a-url",
        };
        opts.Embeddings.Endpoint = "https://apim.example.com/inference";
        Action act = opts.ValidateEnabled;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*absolute http(s) URL*");
    }

    [Fact]
    public void ValidateEnabled_RequiresEmbeddingsEndpoint()
    {
        var opts = new AzureAISearchOptions
        {
            Endpoint = "https://mysearch.search.windows.net",
        };
        Action act = opts.ValidateEnabled;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Embeddings*Endpoint*APIM*");
    }

    [Fact]
    public void ValidateEnabled_HappyPath_DoesNotThrow()
    {
        var opts = new AzureAISearchOptions
        {
            Endpoint = "https://mysearch.search.windows.net",
            IndexName = "retail-pulse",
        };
        opts.Embeddings.Endpoint = "https://apim.example.com/inference";
        opts.Embeddings.Deployment = "text-embedding-3-small";

        Action act = opts.ValidateEnabled;
        act.Should().NotThrow();
    }

    [Fact]
    public void ResolveModelId_DefaultsToDeployment_WhenModelIdBlank()
    {
        var e = new AzureAISearchEmbeddingsOptions { Deployment = "custom-embed" };
        e.ResolveModelId().Should().Be("custom-embed");
    }

    [Fact]
    public void ResolveModelId_HonorsExplicitModelId()
    {
        var e = new AzureAISearchEmbeddingsOptions
        {
            Deployment = "custom-embed",
            ModelId = "text-embedding-3-small",
        };
        e.ResolveModelId().Should().Be("text-embedding-3-small");
    }
}
