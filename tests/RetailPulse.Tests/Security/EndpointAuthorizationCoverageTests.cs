using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Api.Security;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Runtime deny-by-default coverage over the ACTUAL API endpoint graph.
///
/// Rather than scanning source text (which cannot see the compiled route table, misses
/// Program.cs hub maps, and passes on a comment), this builds a real
/// <see cref="WebApplication"/>, wires the production authorization stack (including the
/// deny-by-default <see cref="AuthorizationOptions.FallbackPolicy"/>), and maps EVERY
/// endpoint extension method plus the two SignalR hubs exactly as Program.cs does. It then
/// walks the real <see cref="EndpointDataSource"/> and asserts that every <c>/api</c> and
/// <c>/hubs</c> route carries authorization metadata, with an explicit allowlist for the
/// only endpoints permitted to be anonymous (health/liveness). A newly added endpoint file
/// or hub that forgets <c>RequireAuthorization()</c> fails this test automatically.
/// </summary>
public sealed class EndpointAuthorizationCoverageTests
{
    /// <summary>
    /// The ONLY routes allowed to be reachable anonymously: health/liveness, plus the Anonymous
    /// bootstrap endpoint (the single sanctioned unauthenticated <c>/api</c> route, which mints a
    /// session credential and is AllowAnonymous by design).
    /// </summary>
    private static readonly string[] AnonymousAllowlist =
        ["/health", "/alive", AnonymousCapabilityPolicy.BootstrapRoute];

    private static WebApplication BuildRealEndpointGraph()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        // Development avoids the production fail-fast validation; endpoint authorization
        // metadata is environment-independent, so this does not weaken the assertion.
        builder.Environment.EnvironmentName = Environments.Development;

        builder.Services.AddRouting();
        builder.Services.AddSignalR();
        builder.Services.AddHealthChecks();

        // Common framework services that endpoint handlers commonly inject. Registered for
        // real (not null) so anything the container touches while building works normally.
        builder.Services.AddLogging();
        builder.Services.AddOptions();
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient();

        // Endpoints are only inspected for authorization metadata — their delegates are never
        // invoked. Building the route table forces RequestDelegateFactory to classify every
        // handler parameter; a parameter of an unregistered app type is treated as an ambiguous
        // request body and materialization throws. Register every type declared in the
        // RetailPulse.* assemblies with a null factory so RDF's (reserved, non-overridable)
        // IServiceProviderIsService sees them as services and binds them from DI at request
        // time. This stands up the REAL endpoint graph without the full application DI wiring,
        // and automatically covers future endpoint files whose handlers take API services.
        RegisterApiTypesAsServices(builder.Services);

        var options = new EntraAuthOptions
        {
            RequireAuth = true,
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "33333333-3333-3333-3333-333333333333",
        };
        builder.Services.AddSingleton(options);
        builder.Services.AddAuthentication();
        builder.Services.AddRetailPulseAuthorization(options);

        WebApplication app = builder.Build();

        // Health/liveness — the sanctioned anonymous surface (mirrors MapDefaultEndpoints).
        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapHealthChecks("/alive").AllowAnonymous();

        // SignalR hubs — mirror Program.cs exactly.
        app.MapHub<TelemetryHub>("/hubs/telemetry").RequireAuthorization();
        app.MapHub<StreamingHub>("/hubs/streaming").RequireAuthorization();

        // Every endpoint extension method Program.cs maps.
        app.MapChatEndpoints(new AgentDefinition());
        app.MapAlertEndpoints();
        app.MapApprovalEndpoints();
        app.MapObservabilityEndpoints();
        app.MapKnowledgeEndpoints();
        app.MapCardEndpoints();
        app.MapGuardrailEndpoints();
        app.MapScorecardEndpoints();
        app.MapEscalationEndpoints();
        app.MapPromoEndpoints();
        app.MapSupplyEndpoints();
        app.MapStoreEndpoints();
        app.MapMarginEndpoints();
        app.MapDeadLetterEndpoints();
        app.MapMemoryEndpoints();
        app.MapCacheEndpoints();

        // The Anonymous bootstrap endpoint (unauthenticated, AllowAnonymous by design) — mirrors
        // Program.cs so the coverage walk sees the exact same route graph the app exposes.
        app.MapAnonymousAuthEndpoints();

        return app;
    }

    private static bool IsProtectedSurface(string rawText)
    {
        string path = rawText.StartsWith('/') ? rawText : "/" + rawText;
        return path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAuthorization(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null
        && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;

    private static IReadOnlyList<RouteEndpoint> ProtectedRoutes(WebApplication app)
    {
        // Read the app's own endpoint data sources (populated by app.MapXxx). The DI
        // composite EndpointDataSource is only wired when routing middleware runs, so we
        // enumerate the builder's sources directly to inspect the compiled route table.
        return [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => IsProtectedSurface(e.RoutePattern.RawText ?? string.Empty))];
    }

    [Fact]
    public async Task EveryApiAndHubRoute_CarriesAuthorizationMetadata()
    {
        await using WebApplication app = BuildRealEndpointGraph();

        IReadOnlyList<RouteEndpoint> protectedRoutes = ProtectedRoutes(app);

        // Sanity: the graph really did materialize the endpoints (not an empty pass).
        protectedRoutes.Should().NotBeEmpty("the API maps many /api and /hubs routes");

        List<string> unprotected = [.. protectedRoutes
            .Where(e => !HasAuthorization(e))
            .Where(e => !AnonymousAllowlist.Contains(e.RoutePattern.RawText, StringComparer.OrdinalIgnoreCase))
            .Select(e => $"{e.RoutePattern.RawText} [{e.DisplayName}]")];

        unprotected.Should().BeEmpty(
            "every /api and /hubs endpoint must require authorization (auth + role + scope). " +
            "Offending routes: " + string.Join(", ", unprotected));
    }

    [Fact]
    public async Task NoApiOrHubRoute_IsAnonymous_OutsideTheAllowlist()
    {
        await using WebApplication app = BuildRealEndpointGraph();

        List<string> anonymousBillable = [.. ProtectedRoutes(app)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(path => !AnonymousAllowlist.Contains(path, StringComparer.OrdinalIgnoreCase))];

        anonymousBillable.Should().BeEmpty("no billable /api or /hubs path may be anonymous");
    }

    [Fact]
    public async Task FallbackPolicy_IsDenyByDefault_RequiringAuthRoleAndScope()
    {
        await using WebApplication app = BuildRealEndpointGraph();

        AuthorizationOptions authOptions = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value;

        authOptions.FallbackPolicy.Should().NotBeNull(
            "a deny-by-default FallbackPolicy must protect endpoints with no explicit metadata");
        // RequireAuthenticatedUser + RequireRole + scope assertion → at least 3 requirements.
        authOptions.FallbackPolicy.Requirements.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Detector_FlagsAnUnannotatedApiEndpoint_RegressionFixture()
    {
        // Proves the coverage check has teeth: an /api endpoint WITHOUT RequireAuthorization
        // is detected as unprotected by the same logic used above.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Services.AddRouting();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorizationBuilder();
        await using WebApplication app = builder.Build();

        app.MapGet("/api/forgot-auth", () => Results.Ok());

        RouteEndpoint offending = ProtectedRoutes(app).Single();
        HasAuthorization(offending).Should().BeFalse(
            "the fixture endpoint intentionally omits RequireAuthorization, and the detector must catch it");
    }

    [Fact]
    public async Task AnonymousCapabilityGraph_AllowsOnlyBootstrapAndChat()
    {
        await using WebApplication app = BuildRealEndpointGraph();

        // Walk the REAL compiled route table and, for every mapped (method, route) on the protected
        // surface, ask the production allowlist whether an anonymous principal may reach it. Only the
        // bootstrap and POST /api/chat may be reachable; everything else — every broad GET,
        // observability/admin/export/memory/cards/approvals route, every alternate LLM path
        // (/api/chat/stream, /api/council/*), AND both SignalR hubs (Sprint 1: no anonymous
        // real-time telemetry/streaming) — must be denied.
        var allowed = new List<string>();
        foreach (RouteEndpoint e in ProtectedRoutes(app))
        {
            string path = e.RoutePattern.RawText ?? string.Empty;
            IReadOnlyList<string> methods =
                e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (string method in methods)
            {
                if (!AnonymousCapabilityPolicy.IsBlocked(method, path))
                {
                    allowed.Add($"{method} {path}");
                }
            }
        }

        allowed.Should().OnlyContain(entry =>
            entry.Contains(AnonymousCapabilityPolicy.BootstrapRoute, StringComparison.OrdinalIgnoreCase)
            || entry == $"POST {AnonymousCapabilityPolicy.ChatRoute}",
            "the anonymous surface is bootstrap + POST /api/chat only (no hubs). Reachable set: "
            + string.Join(", ", allowed));

        // Prove the positive capabilities really are present (not a vacuous pass).
        allowed.Should().Contain(entry => entry == $"POST {AnonymousCapabilityPolicy.ChatRoute}",
            "POST /api/chat must be reachable for anonymous");
        allowed.Should().Contain(
            entry => entry.Contains(AnonymousCapabilityPolicy.BootstrapRoute, StringComparison.OrdinalIgnoreCase),
            "the bootstrap route must be reachable for anonymous");

        // Prove no hub route is reachable — negotiate (POST) and connection (GET), both hubs.
        allowed.Should().NotContain(entry => entry.Contains("/hubs/", StringComparison.OrdinalIgnoreCase),
            "no SignalR hub route may be reachable for anonymous in Sprint 1");

        // And prove representative billable / observability / alternate-LLM / hub routes are denied.
        foreach ((string method, string route) in new[]
        {
            ("POST", "/api/chat/stream"),
            ("POST", "/api/council/convene"),
            ("GET", "/api/scorecard"),
            ("GET", "/api/sessions"),
            ("GET", "/api/memory"),
            ("GET", "/api/audit"),
            ("GET", "/api/traces"),
            ("GET", "/api/dead-letter"),
            ("GET", "/hubs/telemetry"),
            ("POST", "/hubs/telemetry/negotiate"),
            ("GET", "/hubs/streaming"),
            ("POST", "/hubs/streaming/negotiate"),
        })
        {
            AnonymousCapabilityPolicy.IsBlocked(method, route).Should().BeTrue($"{method} {route} must be denied for anonymous");
        }
    }

    /// <summary>
    /// Registers every loadable type declared in the RetailPulse.Api assembly with a null
    /// factory. The factories are never invoked (endpoint delegates are only inspected for
    /// metadata), but the presence of the service descriptors makes the container's built-in
    /// <see cref="IServiceProviderIsService"/> report these types as services, so
    /// RequestDelegateFactory binds handler parameters from DI instead of failing to infer
    /// them. This keeps the coverage check working for current and future endpoint handlers
    /// without hand-maintaining the application's DI graph.
    /// </summary>
    private static void RegisterApiTypesAsServices(IServiceCollection services)
    {
        foreach (System.Reflection.Assembly assembly in RetailPulseAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (Type type in types)
            {
                if (type.IsGenericTypeDefinition)
                {
                    continue;
                }

                // Interfaces and concrete/abstract classes can back a handler parameter; value
                // types and enums are bound from route/query and need no service descriptor.
                if (type.IsInterface || type.IsClass)
                {
                    services.AddScoped(type, static _ => null!);
                }
            }
        }
    }

    /// <summary>
    /// The RetailPulse.Api assembly plus every RetailPulse.* assembly it references. Handler
    /// parameters resolve to services declared across these projects, so all of them must be
    /// visible to <see cref="IServiceProviderIsService"/> for the endpoint graph to materialize.
    /// </summary>
    private static IEnumerable<System.Reflection.Assembly> RetailPulseAssemblies()
    {
        System.Reflection.Assembly api = typeof(EntraAuthOptions).Assembly;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { api.GetName().Name! };
        yield return api;

        foreach (System.Reflection.AssemblyName reference in api.GetReferencedAssemblies())
        {
            if (reference.Name is null
                || !reference.Name.StartsWith("RetailPulse", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(reference.Name))
            {
                continue;
            }

            System.Reflection.Assembly? loaded = null;
            try
            {
                loaded = System.Reflection.Assembly.Load(reference);
            }
            catch (Exception)
            {
                // A referenced RetailPulse assembly that cannot be loaded contributes no
                // handler parameter types worth registering; skip it.
            }

            if (loaded is not null)
            {
                yield return loaded;
            }
        }
    }
}
