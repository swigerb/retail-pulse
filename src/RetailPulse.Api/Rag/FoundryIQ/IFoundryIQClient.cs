using Azure.AI.Agents.Persistent;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// Thin seam over the <c>Azure.AI.Agents.Persistent</c> 1.1.0 GA SDK entry
/// points the Foundry IQ knowledge provider needs. Introduced so unit tests
/// can supply a hand-rolled implementation without spinning up a real
/// PersistentAgentsClient — the SDK types are effectively unmockable and
/// would otherwise force every test through the live conformance gate.
///
/// Every method surfaces the exact contract the provider uses. New surface
/// area lands here before it lands on the provider so the test double stays
/// small enough to hand-write.
/// </summary>
public interface IFoundryIQClient
{
    /// <summary>Look up a vector store by exact <c>vs_...</c> id.</summary>
    Task<FoundryIQVectorStoreInfo> GetVectorStoreAsync(string vectorStoreId, CancellationToken ct);

    /// <summary>Enumerate vector stores accessible in the current project (for name resolution).</summary>
    IAsyncEnumerable<FoundryIQVectorStoreInfo> EnumerateVectorStoresAsync(CancellationToken ct);

    /// <summary>Enumerate files in a vector store (for ListDocuments).</summary>
    IAsyncEnumerable<FoundryIQVectorStoreFileInfo> EnumerateVectorStoreFilesAsync(string vectorStoreId, CancellationToken ct);

    /// <summary>Get a file by id (fills Title/Source on ListDocuments hits).</summary>
    Task<FoundryIQFileInfo?> GetFileAsync(string fileId, CancellationToken ct);

    /// <summary>Enumerate the persistent agents accessible in the current project (for retrieval-agent name resolution).</summary>
    IAsyncEnumerable<FoundryIQAgentInfo> EnumerateAgentsAsync(CancellationToken ct);

    /// <summary>Create a persistent agent with the file_search tool bound to a vector store.</summary>
    Task<string> CreateRetrievalAgentAsync(
        string model,
        string name,
        string instructions,
        string vectorStoreId,
        CancellationToken ct);

    /// <summary>Run one file_search retrieval against the resolved agent + vector store and return the mapped hits.</summary>
    Task<FoundryIQSearchRunResult> RunFileSearchAsync(
        string agentId,
        string query,
        int pollIntervalMs,
        CancellationToken ct);
}

/// <summary>Minimal projection of <see cref="PersistentAgentsVectorStore"/>.</summary>
public sealed record FoundryIQVectorStoreInfo(string Id, string Name, string Status);

/// <summary>Minimal projection of a vector-store file entry.</summary>
public sealed record FoundryIQVectorStoreFileInfo(string FileId);

/// <summary>Minimal projection of <see cref="PersistentAgentFileInfo"/>.</summary>
public sealed record FoundryIQFileInfo(string Id, string Filename, DateTime CreatedAt);

/// <summary>Minimal projection of <see cref="PersistentAgent"/>.</summary>
public sealed record FoundryIQAgentInfo(string Id, string Name);

/// <summary>One file_search hit surfaced from a retrieval run.</summary>
/// <param name="FileId">Vector-store-side file id.</param>
/// <param name="FileName">Ingested file name (used for <c>Source</c>/<c>Title</c>).</param>
/// <param name="Score">Foundry file_search score in <c>[0..1]</c>.</param>
/// <param name="Chunk">Concatenated text content items for the hit (empty when the include list was not honored).</param>
public sealed record FoundryIQSearchHit(string FileId, string FileName, double Score, string Chunk);

/// <summary>Aggregated output of a completed retrieval run.</summary>
/// <param name="Hits">Rank-ordered, de-duplicated hits.</param>
/// <param name="PromptTokens">Prompt-side token usage reported by the run, or 0 when unavailable.</param>
/// <param name="CompletionTokens">Completion-side token usage reported by the run, or 0 when unavailable.</param>
/// <param name="UsageReported">Whether the SDK populated <c>RunCompletionUsage</c>. Debug-log skip signal.</param>
public sealed record FoundryIQSearchRunResult(
    IReadOnlyList<FoundryIQSearchHit> Hits,
    int PromptTokens,
    int CompletionTokens,
    bool UsageReported);
