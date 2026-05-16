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
}
