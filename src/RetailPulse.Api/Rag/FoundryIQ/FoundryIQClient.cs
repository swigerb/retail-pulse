using System.Runtime.CompilerServices;
using Azure;
using Azure.AI.Agents.Persistent;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// Production <see cref="IFoundryIQClient"/> that wraps
/// <see cref="PersistentAgentsClient"/> (1.1.0 GA). Every SDK call runs
/// under the caller's <see cref="CancellationToken"/> — the bounded timeout
/// is enforced one level up by <see cref="FoundryIQKnowledgeBase"/> so a
/// single seam owns retry/breaker/timeout composition and the SDK stays
/// free of duplicate pipelines.
/// </summary>
internal sealed class FoundryIQClient : IFoundryIQClient
{
    private readonly PersistentAgentsClient _client;
    private readonly FoundryIQOptions _options;

    public FoundryIQClient(PersistentAgentsClient client, FoundryIQOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<FoundryIQVectorStoreInfo> GetVectorStoreAsync(string vectorStoreId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorStoreId);
        Response<PersistentAgentsVectorStore> response = await _client.VectorStores
            .GetVectorStoreAsync(vectorStoreId, ct)
            .ConfigureAwait(false);
        PersistentAgentsVectorStore vs = response.Value;
        return new FoundryIQVectorStoreInfo(vs.Id, vs.Name ?? string.Empty, vs.Status.ToString());
    }

    public async IAsyncEnumerable<FoundryIQVectorStoreInfo> EnumerateVectorStoresAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (PersistentAgentsVectorStore vs in _client.VectorStores
            .GetVectorStoresAsync(cancellationToken: ct)
            .ConfigureAwait(false))
        {
            yield return new FoundryIQVectorStoreInfo(vs.Id, vs.Name ?? string.Empty, vs.Status.ToString());
        }
    }

    public async IAsyncEnumerable<FoundryIQVectorStoreFileInfo> EnumerateVectorStoreFilesAsync(
        string vectorStoreId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorStoreId);
        await foreach (VectorStoreFile file in _client.VectorStores
            .GetVectorStoreFilesAsync(vectorStoreId, cancellationToken: ct)
            .ConfigureAwait(false))
        {
            yield return new FoundryIQVectorStoreFileInfo(file.Id);
        }
    }

    public async Task<FoundryIQFileInfo?> GetFileAsync(string fileId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        try
        {
            Response<PersistentAgentFileInfo> response = await _client.Files
                .GetFileAsync(fileId, ct)
                .ConfigureAwait(false);
            PersistentAgentFileInfo info = response.Value;
            return new FoundryIQFileInfo(info.Id, info.Filename ?? info.Id, info.CreatedAt.UtcDateTime);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<FoundryIQAgentInfo> EnumerateAgentsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (PersistentAgent agent in _client.Administration
            .GetAgentsAsync(cancellationToken: ct)
            .ConfigureAwait(false))
        {
            yield return new FoundryIQAgentInfo(agent.Id, agent.Name ?? string.Empty);
        }
    }

    public async Task<string> CreateRetrievalAgentAsync(
        string model,
        string name,
        string instructions,
        string vectorStoreId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorStoreId);

        var fileSearch = new FileSearchToolResource();
        fileSearch.VectorStoreIds.Add(vectorStoreId);
        var toolResources = new ToolResources
        {
            FileSearch = fileSearch,
        };

        Response<PersistentAgent> response = await _client.Administration
            .CreateAgentAsync(
                model: model,
                name: name,
                description: null,
                instructions: instructions,
                tools: [new FileSearchToolDefinition()],
                toolResources: toolResources,
                cancellationToken: ct)
            .ConfigureAwait(false);

        return response.Value.Id;
    }

    public async Task<FoundryIQSearchRunResult> RunFileSearchAsync(
        string agentId,
        string query,
        int pollIntervalMs,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        Response<PersistentAgentThread> threadResponse = await _client.Threads
            .CreateThreadAsync(cancellationToken: ct)
            .ConfigureAwait(false);
        string threadId = threadResponse.Value.Id;

        try
        {
            await _client.Messages
                .CreateMessageAsync(threadId, MessageRole.User, query, cancellationToken: ct)
                .ConfigureAwait(false);

            var include = new List<RunAdditionalFieldList> { RunAdditionalFieldList.FileSearchContents };
            Response<ThreadRun> runResponse = await _client.Runs
                .CreateRunAsync(
                    threadId: threadId,
                    assistantId: agentId,
                    overrideModelName: null,
                    overrideInstructions: null,
                    additionalInstructions: null,
                    additionalMessages: null,
                    overrideTools: null,
                    stream: null,
                    temperature: null,
                    topP: null,
                    maxPromptTokens: null,
                    maxCompletionTokens: null,
                    truncationStrategy: null,
                    toolChoice: null,
                    responseFormat: null,
                    parallelToolCalls: null,
                    metadata: null,
                    include: include,
                    cancellationToken: ct)
                .ConfigureAwait(false);

            ThreadRun run = runResponse.Value;
            var poll = TimeSpan.FromMilliseconds(Math.Max(50, pollIntervalMs));
            while (run.Status == RunStatus.Queued
                   || run.Status == RunStatus.InProgress
                   || run.Status == RunStatus.RequiresAction)
            {
                await Task.Delay(poll, ct).ConfigureAwait(false);
                run = (await _client.Runs
                    .GetRunAsync(threadId, run.Id, ct)
                    .ConfigureAwait(false)).Value;
            }

            if (run.Status != RunStatus.Completed)
            {
                string reason = run.LastError?.Message ?? run.Status.ToString();
                throw new FoundryIQRunFailedException($"Foundry retrieval run ended with status '{run.Status}': {reason}");
            }

            var hits = new List<FoundryIQSearchHit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stepInclude = new List<RunAdditionalFieldList> { RunAdditionalFieldList.FileSearchContents };
            await foreach (RunStep step in _client.Runs
                .GetRunStepsAsync(threadId, run.Id, include: stepInclude, cancellationToken: ct)
                .ConfigureAwait(false))
            {
                if (step.StepDetails is not RunStepToolCallDetails toolCallDetails)
                {
                    continue;
                }

                foreach (RunStepToolCall toolCall in toolCallDetails.ToolCalls)
                {
                    if (toolCall is not RunStepFileSearchToolCall fileSearch || fileSearch.FileSearch?.Results is null)
                    {
                        continue;
                    }

                    foreach (RunStepFileSearchToolCallResult result in fileSearch.FileSearch.Results)
                    {
                        string fileId = result.FileId ?? string.Empty;
                        string fileName = result.FileName ?? fileId;
                        string chunk = string.Empty;
                        if (result.Content is { Count: > 0 })
                        {
                            chunk = string.Join(
                                "\n",
                                result.Content
                                    .Where(c => c.Type == FileSearchToolCallContentType.Text && !string.IsNullOrEmpty(c.Text))
                                    .Select(c => c.Text));
                        }

                        string dedupeKey = fileId + "\u0001" + chunk;
                        if (!seen.Add(dedupeKey))
                        {
                            continue;
                        }

                        hits.Add(new FoundryIQSearchHit(fileId, fileName, result.Score, chunk));
                    }
                }
            }

            int promptTokens = run.Usage?.PromptTokens is long p ? (int)Math.Min(int.MaxValue, p) : 0;
            int completionTokens = run.Usage?.CompletionTokens is long c ? (int)Math.Min(int.MaxValue, c) : 0;
            bool usageReported = run.Usage is not null;
            return new FoundryIQSearchRunResult(hits, promptTokens, completionTokens, usageReported);
        }
        finally
        {
            try
            {
                await _client.Threads
                    .DeleteThreadAsync(threadId, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (RequestFailedException)
            {
                // Best-effort cleanup — leaked threads on cancellation are a
                // Foundry-side quota concern, not a request-path failure.
            }
        }
    }
}

/// <summary>
/// Raised by <see cref="FoundryIQClient.RunFileSearchAsync"/> when a run
/// terminates in a non-<c>Completed</c> state. Translated into
/// <see cref="Contracts.Rag.KnowledgeProviderUnavailableException"/> by the
/// knowledge base so the degradation layer sees a consistent shape.
/// </summary>
internal sealed class FoundryIQRunFailedException : Exception
{
    public FoundryIQRunFailedException(string message) : base(message) { }
}
