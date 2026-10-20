using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Http.HttpResults;
using RetailPulse.Contracts;

namespace RetailPulse.Api.OpenAI.Gateway;

public static class GatewayEvidenceExtensions
{
    /// <summary>Registers the capture policy per-try so retried attempts are each recorded.</summary>
    public static AzureOpenAIClientOptions AddGatewayEvidence(this AzureOpenAIClientOptions options)
    {
        options.AddPolicy(new GatewayEvidencePolicy(), PipelinePosition.PerTry);
        return options;
    }

    /// <summary>Removes caller-supplied x-rp-* headers so clients cannot spoof gateway evidence.</summary>
    public static IApplicationBuilder UseStripInboundGatewayHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            foreach (string name in context.Request.Headers.Keys
                .Where(k => k.StartsWith("x-rp-", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                context.Request.Headers.Remove(name);
            }

            await next();
        });

    /// <summary>Scopes evidence capture to a chat request and attaches it to the typed response.</summary>
    public static RouteHandlerBuilder WithGatewayEvidence(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            GatewayEvidenceCollector.Scope scope = GatewayEvidenceCollector.Begin();
            object? result = await next(ctx);
            return result is Ok<ChatResponse> ok && ok.Value is { } body
                ? TypedResults.Ok(body with { Gateway = scope.Snapshot() })
                : result;
        });
}
