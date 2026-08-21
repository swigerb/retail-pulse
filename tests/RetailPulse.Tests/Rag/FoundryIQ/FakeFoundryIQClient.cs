using System.Runtime.CompilerServices;
using RetailPulse.Api.Rag.FoundryIQ;

namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>
/// Hand-rolled <see cref="IFoundryIQClient"/> used to exercise the
/// Foundry IQ knowledge provider without hitting Foundry. Every knob the
/// provider observes is a public setter so a single fake covers all
/// disabled-startup, happy-path, unauthorised, unknown-vector-store, and
/// unreachable failure tests.
/// </summary>
public sealed class FakeFoundryIQClient : IFoundryIQClient
{
    public Dictionary<string, FoundryIQVectorStoreInfo> Stores { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<FoundryIQVectorStoreFileInfo>> FilesByStore { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FoundryIQFileInfo> FileMetadata { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FoundryIQAgentInfo> AgentsByName { get; } = new(StringComparer.Ordinal);
    public List<FoundryIQSearchHit> NextSearchHits { get; } = [];

    public int PromptTokens { get; set; } = 25;
    public int CompletionTokens { get; set; } = 4;
    public bool UsageReported { get; set; } = true;
    public bool ThrowUnknownVectorStore { get; set; }
    public Exception? ThrowOnSearch { get; set; }
    public Exception? ThrowOnGetVectorStore { get; set; }
    public Exception? ThrowOnEnumerateVectorStores { get; set; }
    public Exception? ThrowOnEnumerateFiles { get; set; }
    public Exception? ThrowOnGetFile { get; set; }
    public Exception? ThrowOnEnumerateAgents { get; set; }
    public Exception? ThrowOnCreateAgent { get; set; }

    public int RunFileSearchCalls { get; private set; }
    public int GetVectorStoreCalls { get; private set; }
    public string? LastCreatedAgentName { get; private set; }
    public string? LastCreatedVectorStoreId { get; private set; }

    public Task<FoundryIQVectorStoreInfo> GetVectorStoreAsync(string vectorStoreId, CancellationToken ct)
    {
        GetVectorStoreCalls++;
        return ThrowOnGetVectorStore is not null
            ? throw ThrowOnGetVectorStore
            : !Stores.TryGetValue(vectorStoreId, out FoundryIQVectorStoreInfo? info)
            ? throw new FoundryIQVectorStoreNotFoundException($"Vector store '{vectorStoreId}' not found in fake project.")
            : Task.FromResult(info);
    }

    public async IAsyncEnumerable<FoundryIQVectorStoreInfo> EnumerateVectorStoresAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ThrowOnEnumerateVectorStores is not null) throw ThrowOnEnumerateVectorStores;
        foreach (FoundryIQVectorStoreInfo info in Stores.Values)
        {
            ct.ThrowIfCancellationRequested();
            yield return info;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<FoundryIQVectorStoreFileInfo> EnumerateVectorStoreFilesAsync(
        string vectorStoreId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ThrowOnEnumerateFiles is not null) throw ThrowOnEnumerateFiles;
        if (!FilesByStore.TryGetValue(vectorStoreId, out List<FoundryIQVectorStoreFileInfo>? files))
        {
            yield break;
        }
        foreach (FoundryIQVectorStoreFileInfo file in files)
        {
            ct.ThrowIfCancellationRequested();
            yield return file;
            await Task.Yield();
        }
    }

    public Task<FoundryIQFileInfo?> GetFileAsync(string fileId, CancellationToken ct)
    {
        if (ThrowOnGetFile is not null) throw ThrowOnGetFile;
        FileMetadata.TryGetValue(fileId, out FoundryIQFileInfo? info);
        return Task.FromResult(info);
    }

    public async IAsyncEnumerable<FoundryIQAgentInfo> EnumerateAgentsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ThrowOnEnumerateAgents is not null) throw ThrowOnEnumerateAgents;
        foreach (FoundryIQAgentInfo agent in AgentsByName.Values)
        {
            ct.ThrowIfCancellationRequested();
            yield return agent;
            await Task.Yield();
        }
    }

    public Task<string> CreateRetrievalAgentAsync(
        string model,
        string name,
        string instructions,
        string vectorStoreId,
        CancellationToken ct)
    {
        if (ThrowOnCreateAgent is not null) throw ThrowOnCreateAgent;
        LastCreatedAgentName = name;
        LastCreatedVectorStoreId = vectorStoreId;
        string id = "asst_" + Guid.NewGuid().ToString("N")[..8];
        AgentsByName[name] = new FoundryIQAgentInfo(id, name);
        return Task.FromResult(id);
    }

    public Task<FoundryIQSearchRunResult> RunFileSearchAsync(string agentId, string query, int pollIntervalMs, CancellationToken ct)
    {
        RunFileSearchCalls++;
        return ThrowOnSearch is not null
            ? throw ThrowOnSearch
            : Task.FromResult(new FoundryIQSearchRunResult(
                Hits: [.. NextSearchHits],
                PromptTokens: PromptTokens,
                CompletionTokens: CompletionTokens,
                UsageReported: UsageReported));
    }
}
