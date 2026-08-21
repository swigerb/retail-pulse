using System.Globalization;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Models;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Rag;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;

namespace RetailPulse.Api.Endpoints;

public static class KnowledgeEndpoints
{
    public static WebApplication MapKnowledgeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/knowledge/upload", async (KnowledgeUploadRequest body, IKnowledgeBase kb, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Content))
                return Results.BadRequest(new { error = "Fields 'title' and 'content' are required." });

            string source = body.Source ?? "upload";
            try
            {
                string id = await kb.IngestDocumentAsync(body.Title, body.Content, source, ct);

                // Enrich the response with the resolved chunk count so the UI can
                // report an honest ingestion outcome ("accepted N chunks") without
                // making a second round-trip. When ListDocumentsAsync cannot see
                // the new id (e.g. asynchronous cloud indexing), fall back to 0
                // rather than fabricating a count.
                IReadOnlyList<DocumentInfo> docs = await kb.ListDocumentsAsync(ct);
                DocumentInfo? persisted = docs.FirstOrDefault(d => d.Id == id);
                int chunkCount = persisted?.ChunkCount ?? 0;
                return Results.Ok(new
                {
                    documentId = id,
                    title = body.Title,
                    status = "ingested",
                    chunkCount,
                    source = persisted?.Source ?? source,
                });
            }
            catch (InvalidOperationException ex)
            {
                // Provider-enforced quota rejection surfaces as a structured 409
                // so the frontend can render a "quota reached" outcome distinct
                // from a generic failure.
                KnowledgeBaseCapabilities caps = kb.GetCapabilities();
                IReadOnlyList<DocumentInfo> docs = await kb.ListDocumentsAsync(ct);
                return Results.Json(new
                {
                    quotaRejected = true,
                    reason = ex.Message,
                    quotas = new
                    {
                        maxDocuments = caps.Quotas.MaxDocuments,
                        maxChunks = caps.Quotas.MaxChunks,
                        maxDocumentSizeBytes = caps.Quotas.MaxDocumentSizeBytes,
                    },
                    usage = new
                    {
                        documentCount = docs.Count,
                        chunkCount = docs.Sum(d => d.ChunkCount),
                    },
                }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (NotSupportedException ex)
            {
                // Read-only providers (Foundry IQ, issue #104) report mutation
                // as unsupported via a first-class exception. Bubble the
                // capability signal to the UI as 405 so the panel can hide the
                // upload affordance rather than showing a bare 500.
                return Results.Json(new
                {
                    mutationUnsupported = true,
                    reason = ex.Message,
                }, statusCode: StatusCodes.Status405MethodNotAllowed);
            }
        })
        .WithName("UploadKnowledge").RequireAuthorization().RequireRateLimiting("upload");

        app.MapGet("/api/knowledge/documents", async (IKnowledgeBase kb, CancellationToken ct) =>
        {
            IReadOnlyList<DocumentInfo> docs = await kb.ListDocumentsAsync(ct);
            return Results.Ok(docs);
        })
        .WithName("ListKnowledgeDocuments").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapDelete("/api/knowledge/documents/{id}", async (string id, IKnowledgeBase kb, CancellationToken ct) =>
        {
            await kb.DeleteDocumentAsync(id, ct);
            return Results.Ok(new { documentId = id, status = "deleted" });
        })
        .WithName("DeleteKnowledgeDocument").RequireAuthorization().RequireRateLimiting("moderate");

        app.MapPost("/api/knowledge/search", async (KnowledgeSearchRequest body, IKnowledgeBase kb, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Query))
                return Results.BadRequest(new { error = "Field 'query' is required." });

            IReadOnlyList<SearchResult> results = await kb.SearchAsync(body.Query, body.TopK ?? 5, ct);
            return Results.Ok(new { query = body.Query, results });
        })
        .WithName("SearchKnowledge").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/knowledge/stats", async (IKnowledgeBase kb, CancellationToken ct) =>
        {
            IReadOnlyList<DocumentInfo> docs = await kb.ListDocumentsAsync(ct);
            int docCount = docs.Count;
            int chunkCount = docs.Sum(d => d.ChunkCount);
            double avgChunks = docCount > 0 ? (double)chunkCount / docCount : 0;

            return Results.Ok(new
            {
                documentCount = docCount,
                chunkCount,
                averageChunksPerDocument = Math.Round(avgChunks, 1)
            });
        })
        .WithName("KnowledgeStats").RequireAuthorization().RequireRateLimiting("relaxed");

        // Knowledge provider snapshot (issue #106). Surfaces the honest
        // capabilities of the active provider — durable vs volatile, relevance
        // kind, quotas, actual usage, degradation policy, whether the primary
        // was replaced by the in-memory fallback — plus the named source
        // catalog and every per-agent binding. The frontend Knowledge panel
        // consumes this to warn on volatile uploads, disclose provider score
        // semantics honestly, and render the per-agent binding view.
        app.MapGet("/api/knowledge/provider", async (
            IKnowledgeBase kb,
            KnowledgeSourceRegistry sourceRegistry,
            IOptionsSnapshot<KnowledgeSourcesOptions> sourcesOptions,
            PromptConfiguration promptConfig,
            CancellationToken ct) =>
        {
            KnowledgeBaseCapabilities caps = kb.GetCapabilities();
            IReadOnlyList<DocumentInfo> docs = await kb.ListDocumentsAsync(ct);

            // Degradation metadata is only meaningful when the DI-registered
            // IKnowledgeBase is the decorator; unit-tests may inject a bare
            // provider. Reading through pattern-matching keeps the endpoint
            // portable without a hard cast.
            string? degradationMode = null;
            bool primaryReplacedByFallback = false;
            if (kb is DegradingKnowledgeBase decorator)
            {
                degradationMode = decorator.DegradationMode.ToString();
                primaryReplacedByFallback = decorator.PrimaryReplacedByFallback;
            }

            KnowledgeSourcesOptions namedSources = sourcesOptions.Value;
            var sourceCatalog = namedSources.Named
                .Select(kvp => new
                {
                    name = kvp.Key,
                    documents = kvp.Value.Documents
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .Select(d => d.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                })
                .OrderBy(s => s.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Build the per-agent binding view from the resolved registry, then
            // enrich each row with the agent's display name and the DECLARED
            // knowledge_base_name from prompts.yaml so the UI can label the
            // binding by named source (not the raw document list).
            var bindingRows = new List<object>(promptConfig.Agents.Count);
            foreach ((string sectionKey, AgentDefinition def) in promptConfig.Agents
                .OrderBy(kv => kv.Value.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase))
            {
                // Orchestration/router entries are not user-facing specialists
                // that "see" a knowledge scope in the way the UI surfaces. Skip
                // them so the binding view stays focused on retrieval-relevant
                // agents.
                if (string.Equals(def.Role, "orchestration", StringComparison.OrdinalIgnoreCase))
                    continue;

                string agentKey = string.IsNullOrWhiteSpace(def.Key) ? sectionKey : def.Key;
                KnowledgeBinding binding = sourceRegistry.GetBinding(agentKey);
                bindingRows.Add(new
                {
                    agentKey,
                    agentDisplayName = def.EffectiveDisplayName,
                    enabled = binding.Enabled,
                    sourceName = def.KnowledgeBaseName,
                    sources = binding.Sources.ToArray(),
                });
            }

            return Results.Ok(new
            {
                provider = new
                {
                    name = caps.ProviderName,
                    relevance = caps.Relevance.ToString(),
                    persistent = caps.Persistent,
                    requiresCloud = caps.RequiresCloud,
                    supportsMutation = caps.SupportsMutation,
                    scoreSemantics = caps.ScoreSemantics,
                },
                degradation = new
                {
                    mode = degradationMode,
                    primaryReplacedByFallback,
                },
                quotas = new
                {
                    maxDocuments = caps.Quotas.MaxDocuments,
                    maxChunks = caps.Quotas.MaxChunks,
                    maxDocumentSizeBytes = caps.Quotas.MaxDocumentSizeBytes,
                },
                usage = new
                {
                    documentCount = docs.Count,
                    chunkCount = docs.Sum(d => d.ChunkCount),
                },
                sources = sourceCatalog,
                bindings = bindingRows,
            });
        })
        .WithName("KnowledgeProviderSnapshot").RequireAuthorization().RequireRateLimiting("relaxed");

        // ── Message Extension endpoints ──────────────────────────────────────
        app.MapPost("/api/message-extension/query", async (MessageExtensionRequest body, IKnowledgeBase kb, IEnumerable<ISpecialistAgent> specialists, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "Field 'text' is required." });

            // Search knowledge base for relevant context
            IReadOnlyList<SearchResult> searchResults = await kb.SearchAsync(body.Text, 5, ct);
            var citations = searchResults
                .Where(r => r.Score >= 0.3)
                .Select(r => new
                {
                    source = r.Title,
                    chunk = r.ChunkIndex >= 0 ? $"Chunk {r.ChunkIndex}" : r.Title,
                    relevance = Math.Round(r.Score, 2)
                })
                .ToList();

            // Build grounded context for the agent
            var contextBuilder = new System.Text.StringBuilder();
            if (searchResults.Count > 0)
            {
                contextBuilder.AppendLine("--- Reference Context (from knowledge base) ---");
                foreach (SearchResult? result in searchResults.Take(3))
                {
                    contextBuilder.AppendLine(CultureInfo.InvariantCulture, $"[Source: {result.Title}, chunk {result.ChunkIndex}]");
                    contextBuilder.AppendLine(result.Chunk);
                    contextBuilder.AppendLine();
                }
                contextBuilder.AppendLine("--- End Reference Context ---");
            }

            // Route to GeneralAgent with RAG context
            ISpecialistAgent? generalAgent = specialists.FirstOrDefault(s => s.Key == "general");
            if (generalAgent is null)
                return Results.StatusCode(503);

            var ragHistory = new List<ChatHistoryMessage>();
            if (contextBuilder.Length > 0)
                ragHistory.Add(new ChatHistoryMessage("system", contextBuilder.ToString()));
            if (!string.IsNullOrWhiteSpace(body.Context))
                ragHistory.Add(new ChatHistoryMessage("system", $"Teams channel context: {body.Context}"));

            var chatRequest = new ChatRequest(
                body.Text,
                SessionId: null,
                User: null,
                History: ragHistory
            );

            try
            {
                ChatResponse response = await generalAgent.HandleAsync(chatRequest, ct);

                string confidence = citations.Count switch
                {
                    >= 3 => "high",
                    >= 1 => "medium",
                    _ => "low"
                };

                return Results.Ok(new
                {
                    answer = response.Reply,
                    citations,
                    confidence,
                    agentUsed = generalAgent.DisplayName
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Message extension query failed for text: {Text}", body.Text[..Math.Min(50, body.Text.Length)]);
                return Results.StatusCode(503);
            }
        })
        .WithName("MessageExtensionQuery").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/message-extension/manifest", () =>
        {
            var manifest = new
            {
                schema = "https://developer.microsoft.com/json-schemas/teams/v1.16/MicrosoftTeams.schema.json",
                manifestVersion = "1.16",
                version = "1.0.0",
                id = "retail-pulse-message-extension",
                name = new { @short = "Retail Pulse Lookup", full = "Retail Pulse Knowledge Base Lookup" },
                description = new
                {
                    @short = "Search retail knowledge base from Teams messages",
                    full = "Select text in a Teams message and look up relevant retail insights, best practices, and data from the Retail Pulse knowledge base."
                },
                composeExtensions = new[]
                {
                    new
                    {
                        botId = "{{BOT_ID}}",
                        commands = new[]
                        {
                            new
                            {
                                id = "searchKnowledge",
                                type = "query",
                                title = "Search Knowledge Base",
                                description = "Look up retail insights and best practices",
                                initialRun = false,
                                parameters = new[]
                                {
                                    new
                                    {
                                        name = "query",
                                        title = "Search Query",
                                        description = "Text to search for in the knowledge base",
                                        inputType = "text"
                                    }
                                }
                            }
                        }
                    }
                }
            };

            return Results.Ok(manifest);
        })
        .WithName("MessageExtensionManifest").RequireAuthorization().RequireRateLimiting("relaxed");

        return app;
    }
}

record KnowledgeUploadRequest(string Title, string Content, string? Source = null);
record KnowledgeSearchRequest(string Query, int? TopK = 5);
record MessageExtensionRequest(string Text, string? Context = null);
