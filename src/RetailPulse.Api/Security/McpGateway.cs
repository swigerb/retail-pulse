using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;

namespace RetailPulse.Api.Security;

/// <summary>
/// Settings for the governed APIM MCP gateway path (ADR 017). Read per call from the
/// <c>McpGateway</c> configuration section so tests and revisions need no restart wiring.
/// </summary>
public static class McpGatewayOptions
{
    public const string SectionName = "McpGateway";
    public const int DefaultMaxItems = 50;
    public const int DefaultMaxResponseBytes = 262144;

    public static IReadOnlyList<string> GetAllowedOperations(IConfiguration configuration) =>
        configuration.GetSection($"{SectionName}:AllowedOperations").GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToArray();

    public static int GetMaxItems(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue($"{SectionName}:MaxItems", DefaultMaxItems));

    public static int GetMaxResponseBytes(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue($"{SectionName}:MaxResponseBytes", DefaultMaxResponseBytes));

    /// <summary>
    /// A principal is app-only when authenticated, carrying a <c>roles</c> claim and no scope
    /// claim in either the short (<c>scp</c>) or long form.
    /// </summary>
    public static bool IsAppOnly(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true
        && !AuthenticationSetup.HasScopeClaim(principal)
        && AuthenticationSetup.HasRolesClaim(principal);
}

/// <summary>
/// Defense in depth for the APIM managed identity (ADR 017): app-only callers may reach only
/// the exact <c>"{METHOD} {route template}"</c> entries in <c>McpGateway:AllowedOperations</c>.
/// Runs after routing, authentication and authorization, so it can only narrow access.
/// An empty or missing list denies every app-only request (fail closed).
/// </summary>
public sealed class AppOnlyRouteAllowListMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!McpGatewayOptions.IsAppOnly(context.User))
        {
            await next(context);
            return;
        }

        if (!IsAllowed(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private bool IsAllowed(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint || endpoint.RoutePattern.RawText is not { } template)
        {
            return false;
        }

        foreach (string entry in McpGatewayOptions.GetAllowedOperations(configuration))
        {
            int space = entry.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            string method = entry[..space];
            string allowedTemplate = entry[(space + 1)..].Trim();
            if (string.Equals(method, context.Request.Method, StringComparison.Ordinal)
                && string.Equals(allowedTemplate, template, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// API-side result caps for the four v1 MCP tool endpoints (ADR 017). APIM cannot cap
/// response bodies on MCP servers, so app-only callers get at most <c>McpGateway:MaxItems</c>
/// items and a <c>413 response_too_large</c> when the payload exceeds
/// <c>McpGateway:MaxResponseBytes</c>. Delegated (SPA) callers are unaffected.
/// </summary>
public sealed class McpGatewayResponseBudgetFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        object? result = await next(context);
        HttpContext http = context.HttpContext;
        if (result is null || !McpGatewayOptions.IsAppOnly(http.User))
        {
            return result;
        }

        IResult inner = result as IResult ?? Results.Json(result);
        Stream original = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await inner.ExecuteAsync(http);
        }
        finally
        {
            http.Response.Body = original;
        }

        int maxBytes = McpGatewayOptions.GetMaxResponseBytes(configuration);
        if (buffer.Length > maxBytes)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            http.Response.ContentType = "application/json";
            http.Response.ContentLength = null;
            await http.Response.WriteAsync("{\"error\":\"response_too_large\"}", http.RequestAborted);
            return Results.Empty;
        }

        byte[] body = buffer.ToArray();
        if (http.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            body = CapItems(body, McpGatewayOptions.GetMaxItems(configuration));
            if (body.Length > maxBytes)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                http.Response.ContentType = "application/json";
                http.Response.ContentLength = null;
                await http.Response.WriteAsync("{\"error\":\"response_too_large\"}", http.RequestAborted);
                return Results.Empty;
            }
        }

        http.Response.ContentLength = body.Length;
        await original.WriteAsync(body, http.RequestAborted);
        return Results.Empty;
    }

    /// <summary>Truncates a root array, or any top-level array property, to <paramref name="maxItems"/>.</summary>
    internal static byte[] CapItems(byte[] json, int maxItems)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }

        bool changed = false;
        if (root is JsonArray rootArray)
        {
            changed = Truncate(rootArray, maxItems);
        }
        else if (root is JsonObject obj)
        {
            foreach (JsonArray array in obj.Select(p => p.Value).OfType<JsonArray>().ToList())
            {
                changed |= Truncate(array, maxItems);
            }
        }

        return changed ? System.Text.Encoding.UTF8.GetBytes(root!.ToJsonString()) : json;
    }

    private static bool Truncate(JsonArray array, int maxItems)
    {
        if (array.Count <= maxItems)
        {
            return false;
        }

        while (array.Count > maxItems)
        {
            array.RemoveAt(array.Count - 1);
        }

        return true;
    }
}
