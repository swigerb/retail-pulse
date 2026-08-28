using RetailPulse.Api.Auth;
using RetailPulse.Contracts.Cards;

namespace RetailPulse.Api.Endpoints;

public static class CardEndpoints
{
    public static WebApplication MapCardEndpoints(this WebApplication app)
    {
        app.MapPost("/api/cards", async (CreateCardRequest body, IAdaptiveCardState cardState, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Title))
                return Results.BadRequest(new { error = "Field 'title' is required." });

            AdaptiveCard card = await cardState.CreateAsync(body, ct);
            return Results.Ok(card);
        })
        .WithName("CreateCard").RequireAuthorization().RequireRateLimiting("moderate");

        app.MapGet("/api/cards", async (HttpContext http, IAdaptiveCardState cardState, CancellationToken ct) =>
        {
            string? typeStr = http.Request.Query["type"].FirstOrDefault();
            string? lifecycleStr = http.Request.Query["lifecycle"].FirstOrDefault();

            CardType? typeFilter = Enum.TryParse(typeStr, true, out CardType t) ? t : null;
            CardLifecycle? lifecycleFilter = Enum.TryParse(lifecycleStr, true, out CardLifecycle l) ? l : null;

            if (cardState is Cards.InMemoryAdaptiveCardState impl)
            {
                IReadOnlyList<AdaptiveCard> cards = await impl.ListAsync(typeFilter, lifecycleFilter, ct);
                return Results.Ok(cards);
            }

            IReadOnlyList<AdaptiveCard> active = await cardState.GetActiveAsync(ct);
            return Results.Ok(active);
        })
        .WithName("ListCards").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/cards/{id}", async (string id, IAdaptiveCardState cardState, CancellationToken ct) =>
        {
            try
            {
                AdaptiveCard card = await cardState.GetAsync(id, ct);
                return Results.Ok(card);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Card '{id}' not found." });
            }
        })
        .WithName("GetCard").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapPost("/api/cards/{id}/action", async (string id, CardAction body, IAdaptiveCardState cardState, CancellationToken ct) =>
        {
            try
            {
                AdaptiveCard card = await cardState.ActionAsync(id, body, ct);
                return Results.Ok(card);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Card '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CardAction").RequireAuthorization().RequireRateLimiting("moderate");

        // The SPA has always called these two routes (cardsApi.ts), but only the generic
        // /action route was ever mapped — so voting and commenting from the Cards panel
        // 404'd. They delegate to the same ActionAsync pipeline.
        //
        // Identity is taken from the authenticated principal rather than the request
        // body, so a caller cannot cast a vote or post a comment as somebody else.
        app.MapPost("/api/cards/{id}/vote", async (string id, VoteRequest body, HttpContext http, IAdaptiveCardState cardState, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Choice))
                return Results.BadRequest(new { error = "Field 'choice' is required." });

            try
            {
                var action = new CardAction(
                    UserIdentity.Resolve(http.User),
                    ResolveDisplayName(http.User),
                    CardActionType.Vote,
                    new Dictionary<string, string> { ["vote"] = body.Choice });

                return Results.Ok(await cardState.ActionAsync(id, action, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Card '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CardVote").RequireAuthorization().RequireRateLimiting("moderate");

        app.MapPost("/api/cards/{id}/comments", async (string id, CommentRequest body, HttpContext http, IAdaptiveCardState cardState, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "Field 'text' is required." });

            try
            {
                var action = new CardAction(
                    UserIdentity.Resolve(http.User),
                    ResolveDisplayName(http.User),
                    CardActionType.Comment,
                    new Dictionary<string, string> { ["text"] = body.Text });

                AdaptiveCard card = await cardState.ActionAsync(id, action, ct);
                // The SPA expects the comment it just created, not the whole card.
                return Results.Ok(card.Comments.LastOrDefault());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Card '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CardComment").RequireAuthorization().RequireRateLimiting("moderate");

        app.MapPost("/api/cards/{id}/archive", async (string id, IAdaptiveCardState cardState, CancellationToken ct) =>
        {
            try
            {
                await cardState.ArchiveAsync(id, ct);
                return Results.Ok(new { id, status = "archived" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Card '{id}' not found." });
            }
        })
        .WithName("ArchiveCard").RequireAuthorization().RequireRateLimiting("moderate");

        return app;
    }

    /// <summary>
    /// Best-effort human-readable name for attribution on votes and comments. Falls back
    /// through the usual claim shapes and finally to a neutral label, so a missing claim
    /// never blocks the action.
    /// </summary>
    private static string ResolveDisplayName(System.Security.Claims.ClaimsPrincipal? principal) =>
        principal?.FindFirst("name")?.Value
        ?? principal?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
        ?? principal?.FindFirst("preferred_username")?.Value
        ?? "Retail Pulse user";
}

/// <summary>Body for <c>POST /api/cards/{id}/vote</c>, matching what the SPA sends.</summary>
public record VoteRequest(string Choice);

/// <summary>Body for <c>POST /api/cards/{id}/comments</c>, matching what the SPA sends.</summary>
public record CommentRequest(string Text);
