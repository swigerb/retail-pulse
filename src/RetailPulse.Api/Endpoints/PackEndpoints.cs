using Microsoft.AspNetCore.Routing;
using RetailPulse.Api.Packs;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// Read-only endpoints that surface active content-pack data to the
/// frontend. Every response is a projection of the singleton
/// <see cref="LoadedPack"/>, so the responses can be served without
/// touching the tenant provider or the prompt library.
/// </summary>
public static class PackEndpoints
{
    public static IEndpointRouteBuilder MapPackEndpoints(this IEndpointRouteBuilder app)
    {
        // Public metadata + tenant + theme for the active pack. Called by
        // the frontend during app bootstrap to build the header, brand
        // list, and theme variables. Anonymous mode also needs this so
        // its unauthenticated shell can render the tenant branding.
        app.MapGet("/api/pack", (LoadedPack pack) => Results.Ok(new PackInfoResponse(
                Key: pack.Name,
                DisplayName: pack.Metadata.DisplayName,
                Description: pack.Metadata.Description,
                Version: pack.Metadata.Version,
                Segment: pack.Metadata.Segment,
                Attribution: pack.Metadata.Attribution,
                Tenant: new PackTenantResponse(
                    Company: pack.Tenant.Company,
                    Industry: pack.Tenant.Industry,
                    Description: pack.Tenant.Description,
                    Brands: pack.Tenant.Brands,
                    Regions: pack.Tenant.Regions,
                    Channels: pack.Tenant.Channels,
                    Theme: pack.Tenant.Theme,
                    Distribution: pack.Tenant.Distribution))))
        .WithName("GetActivePack")
        .AllowAnonymous()
        .RequireRateLimiting("relaxed");

        // Curated starting prompts from the active pack. Shape matches
        // the frontend's PROMPT_CATEGORIES contract so a client can
        // render the pack-supplied set with the same components used for
        // the built-in defaults.
        app.MapGet("/api/pack/starting-tasks", (LoadedPack pack) =>
        {
            IReadOnlyList<PackStartingTaskCategory> categories = pack.StartingTasks;
            PackStartingTaskResponse[] payload = [.. categories.Select(c => new PackStartingTaskResponse(
                Id: c.Id,
                Label: c.Label,
                Emoji: c.Emoji,
                Prompts: c.Prompts))];

            return Results.Ok(new PackStartingTasksResponse(
                PackKey: pack.Name,
                Categories: payload));
        })
        .WithName("GetActivePackStartingTasks")
        .AllowAnonymous()
        .RequireRateLimiting("relaxed");

        return app;
    }
}

public sealed record PackInfoResponse(
    string Key,
    string DisplayName,
    string Description,
    string Version,
    string Segment,
    string Attribution,
    PackTenantResponse Tenant);

public sealed record PackTenantResponse(
    string Company,
    string Industry,
    string Description,
    IReadOnlyList<BrandConfig> Brands,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Channels,
    ThemeConfig Theme,
    DistributionConfig Distribution);

public sealed record PackStartingTasksResponse(
    string PackKey,
    IReadOnlyList<PackStartingTaskResponse> Categories);

public sealed record PackStartingTaskResponse(
    string Id,
    string Label,
    string Emoji,
    IReadOnlyList<string> Prompts);
