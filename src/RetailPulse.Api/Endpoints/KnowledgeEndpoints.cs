using System.Globalization;
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

            string id = await kb.IngestDocumentAsync(body.Title, body.Content, body.Source ?? "upload", ct);
            return Results.Ok(new { documentId = id, title = body.Title, status = "ingested" });
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
