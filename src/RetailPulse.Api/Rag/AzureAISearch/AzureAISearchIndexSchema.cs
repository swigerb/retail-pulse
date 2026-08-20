using Azure.Search.Documents.Indexes.Models;

namespace RetailPulse.Api.Rag.AzureAISearch;

/// <summary>
/// Owns the shape of the Azure AI Search index used by the Retail Pulse
/// knowledge provider. Codified so index creation and drift detection agree,
/// and so a bumped schema version documents itself in the source tree.
///
/// Changing any field name, type, analyzer, or vector dimension requires
/// bumping <see cref="AzureAISearchOptions.SchemaVersion"/> and running the
/// documented reindex procedure — the provider never silently mutates an
/// existing index.
/// </summary>
public static class AzureAISearchIndexSchema
{
    public const string DocumentIdField = "documentId";
    public const string ChunkIdField = "id";
    public const string ChunkIndexField = "chunkIndex";
    public const string TitleField = "title";
    public const string ContentField = "content";
    public const string SourceField = "source";
    public const string SectionHeaderField = "sectionHeader";
    public const string IngestedAtField = "ingestedAt";
    public const string SchemaVersionField = "schemaVersion";
    public const string AgentScopeField = "agentScope";
    public const string VectorField = "contentVector";

    /// <summary>
    /// Builds the <see cref="SearchIndex"/> for the configured target index
    /// name, vector dimensions, and semantic configuration. The result is
    /// deterministic — a second call with the same options produces the same
    /// structural shape.
    /// </summary>
    public static SearchIndex Build(AzureAISearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int dimensions = options.Embeddings.Dimensions;

        var fields = new List<SearchField>
        {
            new(ChunkIdField, SearchFieldDataType.String)
            {
                IsKey = true,
                IsFilterable = true,
            },
            new(DocumentIdField, SearchFieldDataType.String)
            {
                IsFilterable = true,
                IsSortable = false,
                IsFacetable = false,
            },
            new(ChunkIndexField, SearchFieldDataType.Int32)
            {
                IsFilterable = true,
                IsSortable = true,
            },
            new(TitleField, SearchFieldDataType.String)
            {
                IsSearchable = true,
                IsFilterable = true,
                IsSortable = true,
                AnalyzerName = LexicalAnalyzerName.EnLucene,
            },
            new(ContentField, SearchFieldDataType.String)
            {
                IsSearchable = true,
                AnalyzerName = LexicalAnalyzerName.EnLucene,
            },
            new(SourceField, SearchFieldDataType.String)
            {
                IsFilterable = true,
                IsSortable = false,
                IsFacetable = true,
            },
            new(SectionHeaderField, SearchFieldDataType.String)
            {
                IsFilterable = false,
                IsSortable = false,
            },
            new(IngestedAtField, SearchFieldDataType.DateTimeOffset)
            {
                IsFilterable = true,
                IsSortable = true,
            },
            new(SchemaVersionField, SearchFieldDataType.String)
            {
                IsFilterable = true,
            },
            // agentScope is the seam per-agent knowledge binding (#105) uses to
            // filter chunks to a subset of authorized agents. Multi-valued so a
            // single chunk can serve several agents without duplication.
            new(AgentScopeField, SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsFilterable = true,
                IsFacetable = true,
            },
            new VectorSearchField(VectorField, dimensions, options.VectorProfileName),
        };

        var index = new SearchIndex(options.IndexName, fields)
        {
            VectorSearch = new VectorSearch
            {
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(options.HnswAlgorithmName)
                    {
                        Parameters = new HnswParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.Cosine,
                        },
                    },
                },
                Profiles =
                {
                    new VectorSearchProfile(options.VectorProfileName, options.HnswAlgorithmName),
                },
            },
        };

        if (options.SemanticRankingEnabled)
        {
            var prioritizedFields = new SemanticPrioritizedFields
            {
                TitleField = new SemanticField(TitleField),
            };
            prioritizedFields.ContentFields.Add(new SemanticField(ContentField));

            index.SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(options.SemanticConfigurationName, prioritizedFields),
                },
            };
        }

        return index;
    }

    /// <summary>
    /// Confirms the live index carries the fields this provider depends on
    /// (name and type) so a drifted schema surfaces as a clear message rather
    /// than a search-time 404 on a missing field. Returns the first offending
    /// difference so operators know what to reindex.
    /// </summary>
    public static string? DetectMismatch(SearchIndex live, AzureAISearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(live);
        SearchIndex expected = Build(options);

        foreach (SearchField expectedField in expected.Fields)
        {
            SearchField? liveField = live.Fields.FirstOrDefault(
                f => string.Equals(f.Name, expectedField.Name, StringComparison.OrdinalIgnoreCase));
            if (liveField is null)
            {
                return $"Index '{live.Name}' is missing required field '{expectedField.Name}'.";
            }
            if (liveField.Type != expectedField.Type)
            {
                return $"Index '{live.Name}' field '{expectedField.Name}' has type {liveField.Type}, expected {expectedField.Type}.";
            }
            if (expectedField.Name == VectorField && liveField.VectorSearchDimensions != expectedField.VectorSearchDimensions)
            {
                return $"Index '{live.Name}' vector dimension mismatch: live={liveField.VectorSearchDimensions}, expected={expectedField.VectorSearchDimensions}. Bump SchemaVersion and reindex.";
            }
        }

        return null;
    }
}
