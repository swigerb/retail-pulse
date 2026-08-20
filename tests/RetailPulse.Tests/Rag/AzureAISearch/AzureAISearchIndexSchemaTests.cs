using Azure.Search.Documents.Indexes.Models;
using FluentAssertions;
using RetailPulse.Api.Rag.AzureAISearch;

namespace RetailPulse.Tests.Rag.AzureAISearch;

/// <summary>
/// Index schema contract. Every field the provider reads must exist with the
/// right type; the vector dimension must match the configured Embeddings
/// dimensions. A drifted schema is detected explicitly so operators can run
/// the documented reindex procedure rather than silently corrupt a live index.
/// </summary>
public class AzureAISearchIndexSchemaTests
{
    [Fact]
    public void Build_IncludesEveryConsumedField()
    {
        var opts = new AzureAISearchOptions
        {
            Endpoint = "https://x.search.windows.net",
            IndexName = "test",
        };
        opts.Embeddings.Dimensions = 128;

        SearchIndex index = AzureAISearchIndexSchema.Build(opts);

        var fieldNames = index.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        fieldNames.Should().Contain([
            AzureAISearchIndexSchema.ChunkIdField,
            AzureAISearchIndexSchema.DocumentIdField,
            AzureAISearchIndexSchema.ChunkIndexField,
            AzureAISearchIndexSchema.TitleField,
            AzureAISearchIndexSchema.ContentField,
            AzureAISearchIndexSchema.SourceField,
            AzureAISearchIndexSchema.SectionHeaderField,
            AzureAISearchIndexSchema.IngestedAtField,
            AzureAISearchIndexSchema.SchemaVersionField,
            AzureAISearchIndexSchema.AgentScopeField,
            AzureAISearchIndexSchema.VectorField,
        ]);

        SearchField vector = index.Fields.Single(f => f.Name == AzureAISearchIndexSchema.VectorField);
        vector.VectorSearchDimensions.Should().Be(128);
    }

    [Fact]
    public void Build_KeyFieldIsChunkId()
    {
        SearchIndex index = AzureAISearchIndexSchema.Build(new AzureAISearchOptions
        {
            Endpoint = "https://x.search.windows.net",
            IndexName = "test",
        });

        SearchField key = index.Fields.Single(f => f.IsKey == true);
        key.Name.Should().Be(AzureAISearchIndexSchema.ChunkIdField,
            "the chunk id is the durable identity of each document");
    }

    [Fact]
    public void Build_IncludesHnswVectorProfile()
    {
        SearchIndex index = AzureAISearchIndexSchema.Build(new AzureAISearchOptions
        {
            Endpoint = "https://x.search.windows.net",
            IndexName = "test",
            VectorProfileName = "vec-p",
            HnswAlgorithmName = "hnsw-a",
        });

        index.VectorSearch.Should().NotBeNull();
        index.VectorSearch.Algorithms.Should().ContainSingle(a => a.Name == "hnsw-a");
        index.VectorSearch.Profiles.Should().ContainSingle(p => p.Name == "vec-p");
    }

    [Fact]
    public void Build_SemanticEnabled_IncludesSemanticConfiguration()
    {
        var opts = new AzureAISearchOptions
        {
            Endpoint = "https://x.search.windows.net",
            IndexName = "test",
            SemanticRankingEnabled = true,
            SemanticConfigurationName = "sem-c",
        };

        SearchIndex index = AzureAISearchIndexSchema.Build(opts);

        index.SemanticSearch.Should().NotBeNull();
        index.SemanticSearch.Configurations
            .Should().ContainSingle(c => c.Name == "sem-c");
    }

    [Fact]
    public void Build_SemanticDisabled_OmitsSemanticConfiguration()
    {
        SearchIndex index = AzureAISearchIndexSchema.Build(new AzureAISearchOptions
        {
            Endpoint = "https://x.search.windows.net",
            IndexName = "test",
            SemanticRankingEnabled = false,
        });

        index.SemanticSearch.Should().BeNull(
            "the demo default keeps the free semantic feature off unless explicitly opted into");
    }

    [Fact]
    public void DetectMismatch_HappyPath_ReturnsNull()
    {
        var opts = new AzureAISearchOptions
        {
            Endpoint = "https://x.search.windows.net",
            IndexName = "test",
        };
        SearchIndex live = AzureAISearchIndexSchema.Build(opts);

        string? diff = AzureAISearchIndexSchema.DetectMismatch(live, opts);

        diff.Should().BeNull();
    }

    [Fact]
    public void DetectMismatch_ReportsMissingField()
    {
        var opts = new AzureAISearchOptions
        {
            Endpoint = "https://x.search.windows.net",
            IndexName = "test",
        };
        SearchIndex live = AzureAISearchIndexSchema.Build(opts);
        SearchField missing = live.Fields.Single(f => f.Name == AzureAISearchIndexSchema.SourceField);
        live.Fields.Remove(missing);

        string? diff = AzureAISearchIndexSchema.DetectMismatch(live, opts);

        diff.Should().NotBeNull().And.Contain(AzureAISearchIndexSchema.SourceField);
    }

    [Fact]
    public void DetectMismatch_ReportsVectorDimensionDrift()
    {
        var opts = new AzureAISearchOptions
        {
            Endpoint = "https://x.search.windows.net",
            IndexName = "test",
        };
        opts.Embeddings.Dimensions = 1536;
        SearchIndex live = AzureAISearchIndexSchema.Build(opts);

        var drifted = new AzureAISearchOptions
        {
            Endpoint = opts.Endpoint,
            IndexName = opts.IndexName,
        };
        drifted.Embeddings.Dimensions = 768;

        string? diff = AzureAISearchIndexSchema.DetectMismatch(live, drifted);

        diff.Should().NotBeNull().And.Contain("dimension");
    }
}
