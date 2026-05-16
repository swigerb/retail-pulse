using RetailPulse.Api.Scorecard;

namespace RetailPulse.Api.Endpoints;

public static class ScorecardEndpoints
{
    public static WebApplication MapScorecardEndpoints(this WebApplication app)
    {
        app.MapPost("/api/scorecard", async (ScorecardRequest body, ScorecardOrchestrator? scorecard, CancellationToken ct) =>
        {
            if (scorecard is null)
                return Results.StatusCode(503);

            if (body.Brands is null || body.Brands.Length == 0)
                return Results.BadRequest(new { error = "At least one brand is required." });

            ScorecardOrchestrator.PortfolioScorecard result = await scorecard.GenerateAsync(body.Brands, body.Region, ct);
            return Results.Ok(result);
        })
        .WithName("GenerateScorecard").RequireAuthorization().RequireRateLimiting("strict");

        return app;
    }
}

record ScorecardRequest(string[] Brands, string? Region = null);
