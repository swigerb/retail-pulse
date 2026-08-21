using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Consensus;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Escalation;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Health;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Observability;
using RetailPulse.Api.OpenAI;
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.AzureAISearch;
using RetailPulse.Api.Resilience;
using RetailPulse.Api.Scorecard;
using RetailPulse.Api.Security;
using RetailPulse.Api.Telemetry;
using RetailPulse.Api.Tools;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Alerts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Caching;
using RetailPulse.Contracts.Cards;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults (OTel, health checks, service discovery)
builder.AddServiceDefaults();

// In-memory cache (used by MCP response caching handler, tool result cache, etc.)
builder.Services.AddMemoryCache();

// ── Tool Result Cache ────────────────────────────────────────────────────
builder.Services.Configure<ToolCacheOptions>(
    builder.Configuration.GetSection(ToolCacheOptions.SectionName));
builder.Services.AddSingleton<ToolResultCache>();
builder.Services.AddSingleton<CachingToolWrapper>();

// ── Router Classification Cache ─────────────────────────────────────────
builder.Services.AddSingleton<RouterClassificationCache>();

// ── Custom Business Metrics ─────────────────────────────────────────────
builder.Services.AddSingleton<RetailPulseMetrics>();

// Resilience — dead-letter queue and circuit breaker health check
builder.Services.AddSingleton<DeadLetterQueue>();
builder.Services.AddHealthChecks()
    .AddCheck<CircuitBreakerHealthCheck>("mcp-circuit-breaker")
    .AddCheck<McpServerHealthCheck>("mcp-server", tags: ["ready"])
    .AddCheck<AzureOpenAiHealthCheck>("azure-openai", tags: ["ready"]);

// Quota configuration (IOptions pattern)
builder.Services.Configure<KnowledgeOptions>(builder.Configuration.GetSection(KnowledgeOptions.SectionName));
builder.Services.Configure<KnowledgeProviderOptions>(builder.Configuration.GetSection(KnowledgeProviderOptions.SectionName));
builder.Services.Configure<ObservabilityOptions>(builder.Configuration.GetSection(ObservabilityOptions.SectionName));

// Load tenant configuration
string tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "tenant.yaml");
if (!File.Exists(tenantConfigPath))
{
    tenantConfigPath = Path.GetFullPath(
        Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml"));
}
var tenantProvider = new FileTenantProvider(tenantConfigPath);
builder.Services.AddSingleton<ITenantProvider>(tenantProvider);
builder.Services.AddSingleton(tenantProvider.GetTenant());

// Add our custom ActivitySource to the OTel pipeline
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("RetailPulse.Agent").AddSource("RetailPulse.Alerts"))
    .WithMetrics(metrics => metrics.AddMeter(RetailPulseMetrics.MeterName));

// SignalR for real-time telemetry
builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

// Session-ownership registry — binds a chat sessionId to its owning subject so the hubs can refuse
// a caller that tries to join another subject's session group (Finding 6). Consulted only for
// Anonymous callers; Entra/dev hub behavior is unchanged. Registered in all modes because the hubs
// take it via constructor injection.
builder.Services.AddSingleton<ISessionOwnershipRegistry, SessionOwnershipRegistry>();

// ── CORS — split policies for Development vs Production ─────────────────
// Development includes known local origins plus explicitly configured deployed
// origins. This keeps the fixed synthetic demo identity while allowing the SWA
// frontend to connect directly to ACA for SignalR, which SWA does not proxy.
string[] configuredCorsOrigins = builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>()
    ?? [];
string[] corsDevOrigins = CorsOriginResolver.ForDevelopment(configuredCorsOrigins);
string[] corsProdOrigins = configuredCorsOrigins;

builder.Services.AddCors(options =>
{
    options.AddPolicy("Development", policy => policy
            .WithOrigins(corsDevOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());

    options.AddPolicy("Production", policy =>
    {
        if (corsProdOrigins.Length > 0)
        {
            policy.WithOrigins(corsProdOrigins)
                .WithMethods("GET", "POST", "PUT", "DELETE")
                .WithHeaders("Content-Type", "Authorization", "X-Requested-With", "X-SignalR-User-Agent")
                .AllowCredentials();
        }
        // If no origins configured, policy allows nothing (deny by default)
    });
});

// ── Authentication & Authorization ──────────────────────────────────────
// Provider-neutral authentication boundary (see Security/ProviderNeutralAuthentication.cs).
// The configured Authentication:Mode selects the provider; only "Entra" is implemented today
// and routes to the single, tenant-scoped Entra security boundary (Security/AuthenticationSetup.cs):
// Production validates real Entra JWTs pinned to the configured tenant/audience/issuer,
// honours the access_token query param only for /hubs, and requires the app role + API
// scope on every protected endpoint and hub. Development uses the synthetic handler. The
// default policy is never permissive — no RequireAssertion(_ => true) when auth is on.
// GitHub mode wires a confidential OAuth BFF and short-lived Retail Pulse session tokens.
// A missing mode fails closed outside Development. Anonymous mode (Sprint 1) wires its own constrained session
// scheme + guardrails internally and returns null (see AddAnonymousMode); it never falls through
// to Entra/Development and, in a hosted environment, requires a second explicit opt-in.
AuthenticationMode resolvedAuthMode =
    AuthenticationModeOptions.Resolve(builder.Configuration, builder.Environment);
bool anonymousAuthMode = resolvedAuthMode == AuthenticationMode.Anonymous;
bool gitHubAuthMode = resolvedAuthMode == AuthenticationMode.GitHub;

EntraAuthOptions? entraAuthOptions =
    builder.Services.AddProviderNeutralAuthentication(builder.Configuration, builder.Environment);
if (entraAuthOptions is not null)
{
    builder.Services.AddRetailPulseAuthorization(entraAuthOptions);
}

// ── Rate Limiting ───────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindowLimiter("strict", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("upload", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("moderate", opt =>
    {
        opt.PermitLimit = 30;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("relaxed", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

    // Rate limiter for the unauthenticated Anonymous bootstrap endpoint. Always registered so the
    // limiter graph is stable and testable; only used when Authentication:Mode=Anonymous maps the
    // bootstrap route.
    //
    // IMPORTANT (ACA ingress reality): when hosted behind Azure Container Apps, every request
    // arrives from the ingress/proxy, so HttpContext.Connection.RemoteIpAddress is the proxy's IP,
    // NOT the client's — a per-IP partition would therefore collapse to a single global bucket
    // anyway. We do NOT trust X-Forwarded-For to recover the client IP: ACA does not give us a
    // cryptographically verifiable client-IP header, and an attacker can forge XFF to shard around a
    // per-IP limit. So bootstrap is intentionally a single GLOBAL, conservative fixed window — it
    // caps total anonymous session minting per minute for the whole replica and cannot be bypassed
    // by header spoofing. Fine-grained abuse control is enforced AFTER bootstrap by the per-subject
    // (immutable, server-minted sub) limits in AnonymousGuardMiddleware, which is the primary
    // control. Note: this window is replica-local; hosted Anonymous runs at maxReplicas=1.
    //
    // Config key: Anonymous:Bootstrap:GlobalPerMinute (conservative default 5). The legacy
    // Anonymous:Bootstrap:PerIpPerMinute key is still honoured as a fallback for backward
    // compatibility — it was never actually per-IP behind ACA, hence the rename.
    options.AddPolicy("anonymous-bootstrap", _ =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "anonymous-bootstrap-global",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("Anonymous:Bootstrap:GlobalPerMinute",
                    builder.Configuration.GetValue("Anonymous:Bootstrap:PerIpPerMinute", 5)),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Rate limiters for the GitHub confidential OAuth BFF endpoints. Always registered so the limiter
    // graph is stable and testable; only used when Authentication:Mode=GitHub maps the endpoints.
    // Behind ACA the client IP is the proxy's and X-Forwarded-For is forgeable, so these are single
    // GLOBAL per-replica fixed windows (not per-IP) that cap login-flow abuse (state minting and code
    // redemption) without being bypassable by header spoofing. Replica-local; hosted GitHub runs at
    // maxReplicas=1. Config keys: GitHub:RateLimits:StartPerMinute / ExchangePerMinute.
    options.AddPolicy("github-start", _ =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "github-start-global",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("GitHub:RateLimits:StartPerMinute", 10),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.AddPolicy("github-exchange", _ =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "github-exchange-global",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("GitHub:RateLimits:ExchangePerMinute", 20),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ── API Versioning ──────────────────────────────────────────────────────
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
});

// Load prompts from YAML and resolve tenant placeholders via PromptTemplateEngine
string promptsPath = Path.Combine(builder.Environment.ContentRootPath, "prompts.yaml");
PromptConfiguration promptConfig = RetailPulseAgent.LoadPrompts(promptsPath);
TenantConfiguration tenant = tenantProvider.GetTenant();
var promptEngine = new RetailPulse.Api.Prompts.PromptTemplateEngine(tenant);

// Normalize + hydrate every configured agent definition once so downstream lookups
// see effective keys and tenant-placeholder-free prompts. Section names become the
// default Key when `key:` is omitted, preserving pre-refactor behavior for existing
// entries and letting new specialists come in without any C# changes (issue #98).
foreach ((string sectionKey, AgentDefinition def) in promptConfig.Agents)
{
    if (string.IsNullOrWhiteSpace(def.Key))
    {
        def.Key = sectionKey;
    }

    // Router prompt is domain-generic; router entry stays un-hydrated so tenant
    // placeholders don't accidentally leak into classification instructions.
    if (!string.Equals(def.Key, "router", StringComparison.OrdinalIgnoreCase))
    {
        promptEngine.Hydrate(def);
    }
}

// ── Guardrails: agent-definition safety validator (issue #99) ──────────
// Bind the guardrails configuration, wire the durable audit sink, and (when
// enabled) construct the Content Safety evaluator BEFORE any agent is
// composed. The runtime pipeline later resolves the SAME singleton instances
// registered here, so startup-time rejections and runtime detections share
// one audit feed and one runtime config surface. The reorder is safe — none
// of these registrations depend on prompt/tool composition.
var guardrailsConfig = new GuardrailsConfig();
builder.Configuration.GetSection("Guardrails").Bind(guardrailsConfig);
builder.Services.AddSingleton(guardrailsConfig);

var suspiciousLog = new InMemorySuspiciousRequestLog();
builder.Services.AddSingleton(suspiciousLog);
builder.Services.AddSingleton<ISuspiciousRequestLog>(suspiciousLog);

builder.Services.AddContentSafety(guardrailsConfig.ContentSafety);

using (ILoggerFactory validatorLoggerFactory = LoggerFactory.Create(lb =>
{
    lb.AddConfiguration(builder.Configuration.GetSection("Logging"));
    lb.AddSimpleConsole();
}))
{
    ILogger<AgentDefinitionValidator> validatorLogger =
        validatorLoggerFactory.CreateLogger<AgentDefinitionValidator>();

    IContentSafetyEvaluator validatorEvaluator;
    ServiceProvider? evaluatorScope = null;
    try
    {
        if (guardrailsConfig.ContentSafety.Enabled && guardrailsConfig.AgentDefinition.SafetyChecksEnabled)
        {
            // Content Safety is registered as a singleton with no scoped
            // dependencies. Building a parallel mini-provider gives us the
            // evaluator without finalizing the outer DI graph — the runtime
            // pipeline will construct its own copy against the same config.
            var evaluatorServices = new ServiceCollection();
            evaluatorServices.AddSingleton(guardrailsConfig);
            evaluatorServices.AddContentSafety(guardrailsConfig.ContentSafety);
            evaluatorServices.AddLogging(lb => lb.AddConfiguration(builder.Configuration.GetSection("Logging")));
#pragma warning disable ASP0000 // Intentional startup-only mini-provider — outer DI graph is not finalized yet.
            evaluatorScope = evaluatorServices.BuildServiceProvider();
#pragma warning restore ASP0000
            validatorEvaluator = evaluatorScope.GetRequiredService<IContentSafetyEvaluator>();
        }
        else
        {
            validatorEvaluator = NoOpContentSafetyEvaluator.Instance;
        }

        AgentToolRegistry validatorToolRegistry = AgentDefinitionValidatorToolCatalog.Build();
        var validator = new AgentDefinitionValidator(
            guardrailsConfig,
            new JailbreakDetector(),
            validatorEvaluator,
            suspiciousLog,
            validatorToolRegistry,
            validatorLogger);

        _ = validator
            .ValidateAsync(promptConfig)
            .GetAwaiter()
            .GetResult();
    }
    finally
    {
        evaluatorScope?.Dispose();
    }
}

// Shorthand accessors kept for the orchestration wiring below.
AgentDefinition agentDef = ResolveAgent("retail-pulse", promptConfig);
AgentDefinition routerDef = ResolveAgent("router", promptConfig);
AgentDefinition? councilSynthesisDef = TryResolveAgent("council-synthesis", promptConfig);
AgentDefinition? councilVoteDef = TryResolveAgent("council-vote", promptConfig);
AgentDefinition? scorecardSynthesisDef = TryResolveAgent("scorecard-synthesis", promptConfig);
_ = TryResolveAgent("exec-brief", promptConfig); // referenced only for validation
// Planner definition (issue #93). Optional so the API still boots when the
// planner entry is absent from a tenant's prompts.yaml — the chat pipeline
// falls back to the single-specialist path when it is missing.
AgentDefinition? plannerDef = TryResolveAgent("planner", promptConfig);

static AgentDefinition ResolveAgent(string sectionKey, PromptConfiguration cfg) =>
    cfg.Agents.TryGetValue(sectionKey, out AgentDefinition? d)
        ? d
        : throw new InvalidOperationException(
            $"prompts.yaml is missing required agent section '{sectionKey}'.");
static AgentDefinition? TryResolveAgent(string sectionKey, PromptConfiguration cfg) =>
    cfg.Agents.TryGetValue(sectionKey, out AgentDefinition? d) ? d : null;

// Register HttpClient for MCP server communication. The default URL is a
// dev convenience — production should always set McpServer:BaseUrl.
string mcpBaseUrl = builder.Configuration["McpServer:BaseUrl"]
    ?? (builder.Environment.IsDevelopment() ? "http://localhost:5200" : null)
    ?? throw new InvalidOperationException(
        "Configuration value 'McpServer:BaseUrl' is required outside of Development.");
builder.Services.AddTransient<McpResponseCachingHandler>();
builder.Services.AddHttpClient("McpServer", client => client.BaseAddress = new Uri(mcpBaseUrl)).AddHttpMessageHandler<McpResponseCachingHandler>()
  .AddMcpResilienceHandler();

// Register tools
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DepletionStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DepletionStatsTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new PortfolioDepletionStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<PortfolioDepletionStatsTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new FieldSentimentTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<FieldSentimentTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ShipmentStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<ShipmentStatsTool>>());
});

// Chart data tool (always available)
builder.Services.AddScoped<ChartDataTool>();

// ── Tool-result budget boundary ─────────────────────────────────────────────
// Centralized compaction + dedup + per-request budget applied to every tool result
// before it enters model context (wired at the AgentExecutionPipeline tool-wrap
// choke point, so it covers all specialist agents). See docs/tool-context-budget.md.
builder.Services.AddSingleton(sp =>
{
    var options = new RetailPulse.Api.Budget.ToolResultBudgetOptions();
    builder.Configuration.GetSection(RetailPulse.Api.Budget.ToolResultBudgetOptions.SectionName).Bind(options);
    // Portfolio-wide aggregate tool: the compacted 12-brand payload is the
    // source of truth for horizontal-bar ranking coverage (issue #74). The
    // default 6 KB per-tool cap can clip brand rows below the 12-brand roster
    // threshold, which then triggers the fail-closed diagnostic even though
    // the tool did return complete data. Raise the ceiling ONLY for this tool
    // to preserve full portfolio coverage; the compactor still strips the
    // verbose per-brand sentiment narrative so the aggregate stays lean.
    if (!options.PerToolMaxResultChars.ContainsKey("GetPortfolioDepletionStats"))
    {
        options.PerToolMaxResultChars["GetPortfolioDepletionStats"] = 20_000;
    }
    return options;
});
builder.Services.AddSingleton<RetailPulse.Api.Budget.IToolResultCompactor, RetailPulse.Api.Budget.HistoricalDemandCompactor>();
builder.Services.AddSingleton<RetailPulse.Api.Budget.IToolResultCompactor, RetailPulse.Api.Budget.PortfolioDepletionCompactor>();
builder.Services.AddSingleton(sp =>
    new RetailPulse.Api.Budget.ToolResultBudget(
        sp.GetServices<RetailPulse.Api.Budget.IToolResultCompactor>()));

builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new VariantMixTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<VariantMixTool>>());
});

// Demand forecasting tools (deprecated — kept for backward compat during v2 transition)
#pragma warning disable CS0618 // Obsolete demand tool proxies still registered for legacy agent pipeline
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new HistoricalDemandTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<HistoricalDemandTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ForecastTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<ForecastTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new SeasonalityFactorsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<SeasonalityFactorsTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DemandRisksTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DemandRisksTool>>());
});
#pragma warning restore CS0618

// Predictive tool prefetch service
builder.Services.AddScoped<RetailPulse.Api.Prefetch.ToolPrefetchService>();

// Promo planning tools
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new PromoHistoryTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<PromoHistoryTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new CalculateLiftTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<CalculateLiftTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new EvaluateTimingTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<EvaluateTimingTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new EstimateROITool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<EstimateROITool>>());
});

// Competitive intelligence tools
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new CompetitorPricingTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<CompetitorPricingTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new MarketShareTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<MarketShareTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DetectThreatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DetectThreatsTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new CompetitiveLandscapeTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<CompetitiveLandscapeTool>>());
});

// Supply chain tools
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new InventoryLevelsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<InventoryLevelsTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new SupplyDisruptionsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<SupplyDisruptionsTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new FulfillmentRateTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<FulfillmentRateTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new SupplyHealthTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<SupplyHealthTool>>());
});

// Store operations tools
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new StorePerformanceTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<StorePerformanceTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ShelfLayoutTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<ShelfLayoutTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new OptimizePlanogramTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<OptimizePlanogramTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new PredictStockoutTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<PredictStockoutTool>>());
});

// Margin analysis tools
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new MarginByBrandTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<MarginByBrandTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new MarginDriversTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<MarginDriversTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new MarginTrendTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<MarginTrendTool>>());
});
builder.Services.AddScoped(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DetectMarginRisksTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DetectMarginRisksTool>>());
});

// Human-in-the-loop approval gate (SQLite-backed, singleton for shared state)
// The data directory is where every durable SQLite store lives (approvals, memory,
// alerts, costs, audit). The deployed synthetic demo runs
// ASPNETCORE_ENVIRONMENT=Production under Entra auth with no durable path, because
// this tenant's governance blocks account-key Azure Files mounts (see
// docs/deployment-azd.md). It therefore sets RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true
// to explicitly opt in to a writable per-replica temp directory: there is NO durable
// Azure volume, and observability history lives only within the current replica and
// resets on replacement/redeploy. Resolution still fails fast (no silent ephemeral
// fallback) when durability is EXPLICITLY required — a configured data directory that
// is unwritable, a truthy RETAIL_PULSE_REQUIRE_DURABLE_STORAGE, or Production WITHOUT
// the ephemeral opt-out — preserving the guarantee for any future policy-compatible
// durable backing. See DataDirectoryResolver.
string dataDirectory = DataDirectoryResolver.Resolve(builder.Configuration, builder.Environment);
string approvalDbPath = Path.Combine(dataDirectory, "approvals.db");
// Human-in-the-loop approval gate. Restart-safe (issue #91): every Pending row
// carries the durable identity of the process that owns its in-process waiter, the
// authoritative timeout used to create it, and a heartbeat; the startup
// reconciliation service closes rows abandoned by a previous process through the
// configured resume strategy so an approval never silently loses its execution.
// TimeProvider is injected so timeout/backoff tests never touch the wall clock.
builder.Services.Configure<ApprovalOptions>(builder.Configuration.GetSection(ApprovalOptions.SectionName));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IApprovalResumeStrategy, OrphanUnresumableStrategy>();
builder.Services.AddSingleton(sp =>
{
    ApprovalOptions opts = sp.GetRequiredService<IOptions<ApprovalOptions>>().Value;
    return new SqliteApprovalGate(
        approvalDbPath,
        sp.GetRequiredService<ILogger<SqliteApprovalGate>>(),
        opts.DefaultTimeout,
        sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<IApprovalGate>(sp => sp.GetRequiredService<SqliteApprovalGate>());
// Reconciliation runs during host startup as a hosted service. Ordering with the
// web host is not strictly guaranteed here — traffic may briefly race the sweep —
// so correctness does NOT depend on completing before Kestrel accepts requests.
// Race-safety is enforced at the row: every Pending → terminal write is a single
// conditional SQL UPDATE and RespondAsync returns the actual persisted winner,
// so a late human response can never silently overwrite a row that reconciliation
// (or a concurrent waiter) has already closed.
builder.Services.AddHostedService<ApprovalReconciliationBackgroundService>();

// Approval tool — available to specialist agents for high-impact recommendations
builder.Services.AddScoped(sp =>
    new ApprovalTool(
        sp.GetRequiredService<IApprovalGate>(),
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<ILogger<ApprovalTool>>()));

// Conversation memory — SQLite-backed, per-user, with configurable TTL
// Conversation memory — SQLite-backed with bounded-channel background extraction
string memoryDbPath = Path.Combine(dataDirectory, "memory.db");
builder.Services.AddConversationMemory(memoryDbPath);

// Proactive alerts — background anomaly detection with SQLite persistence
string alertsDbPath = Path.Combine(dataDirectory, "alerts.db");
builder.Services.AddProactiveAlerts(alertsDbPath);

// Durable session/turn persistence (issue #90). Off by default: when
// SessionPersistence:Enabled is false the store singleton is not registered and no
// database file is created — the chat pipeline behaves identically to Wave 1. When
// on, sessions and turns for AUTHENTICATED subjects survive an API restart, so a
// browser refresh can rehydrate the last conversation. Anonymous sessions are never
// written: the chat endpoint skips persistence via IAnonymousChatPolicy, and the
// session endpoints refuse anonymous callers at entry. See SessionPersistenceOptions.
string sessionsDbPath = Path.Combine(dataDirectory, "sessions.db");
builder.Services.AddSessionPersistence(builder.Configuration, sessionsDbPath);

// Durable plan/step persistence (issue #93). Off by default (see
// PlanPersistenceOptions.Enabled). When on, plans and steps survive an API
// restart, so an authenticated user can list and reopen owned plans. Mirrors
// the SessionPersistence pattern above and lives under the shared data
// directory so a single SMB-safe mount covers both stores.
string plansDbPath = Path.Combine(dataDirectory, "plans.db");
builder.Services.AddPlanPersistence(builder.Configuration, plansDbPath);

// Distributed tracing — in-memory ring buffer with bounded-channel SignalR push
builder.Services.AddSingleton<TelemetryPushChannel>();
builder.Services.AddSingleton(sp =>
    new InMemoryTraceCollector(
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<TelemetryPushChannel>()));
builder.Services.AddSingleton<ITraceCollector>(sp => sp.GetRequiredService<InMemoryTraceCollector>());
builder.Services.AddHostedService<TelemetryPushBackgroundService>();

// ── Pre-demo cache warming (populates MCP response cache on startup) ────
builder.Services.AddHostedService<RetailPulse.Api.Startup.CacheWarmingService>();

// ── RAG Knowledge Base ──────────────────────────────────────────────────
// The in-memory BM25 provider is the DEFAULT and works on a laptop with no
// cloud dependencies. The provider abstraction lets cloud providers (#103
// Azure AI Search, #104 Foundry IQ) opt in via configuration without changing
// call sites. See docs/adr/009-knowledge-provider-abstraction.md.
builder.Services.AddSingleton<InMemoryKnowledgeBase>();
builder.Services.AddSingleton(sp =>
{
    // Every process registers the in-memory factory automatically so it is
    // always available as the default primary and as the fallback target for
    // KnowledgeDegradationMode.FallbackToInMemory. Cloud modules (#103/#104)
    // register their own factories from their opt-in extension methods via
    // the IKnowledgeProviderContribution seam.
    var registry = new KnowledgeProviderRegistry();
    registry.Register(
        KnowledgeProviderMode.InMemory,
        s => s.GetRequiredService<InMemoryKnowledgeBase>());
    foreach (IKnowledgeProviderContribution contribution in sp.GetServices<IKnowledgeProviderContribution>())
    {
        contribution.Register(registry);
    }
    return registry;
});
builder.Services.AddSingleton<KnowledgeProviderSelector>();
// Optional Azure AI Search provider (issue #103). The extension is a no-op
// when Knowledge:AzureAISearch:Endpoint is blank, so the default demo path
// stays byte-for-byte unchanged — nothing about the InMemory-only flow is
// touched by adding this call.
builder.Services.AddAzureAISearchKnowledgeProvider(builder.Configuration);
builder.Services.AddSingleton(sp =>
{
    KnowledgeProviderSelector selector = sp.GetRequiredService<KnowledgeProviderSelector>();
    IKnowledgeBase primary = selector.CreatePrimary(sp);
    InMemoryKnowledgeBase fallback = sp.GetRequiredService<InMemoryKnowledgeBase>();
    KnowledgeDegradationMode degradation = selector.ResolveDegradation();
    return new DegradingKnowledgeBase(
        primary,
        fallback,
        degradation,
        sp.GetRequiredService<ILogger<DegradingKnowledgeBase>>());
});
builder.Services.AddSingleton<IKnowledgeBase>(sp => sp.GetRequiredService<DegradingKnowledgeBase>());
builder.Services.AddSingleton<RagContextProvider>();

// Response cache — in-memory with TTL expiration and LRU eviction
builder.Services.AddSingleton<InMemoryResponseCache>();
builder.Services.AddSingleton<IResponseCache>(sp => sp.GetRequiredService<InMemoryResponseCache>());

// Guardrails config binding, the InMemorySuspiciousRequestLog singleton, and
// the Content Safety evaluator are wired earlier in this file (immediately
// before the AgentDefinitionValidator call for issue #99) so the load-time
// validator writes to the SAME audit sink the middleware feeds later.
// See the "Guardrails: agent-definition safety validator" block above.

// Guardrails middleware — input filtering (jailbreak, injection) + output PII redaction
builder.Services.AddScoped<GuardrailsMiddleware>();

// Streaming middleware — progressive token delivery via SignalR
builder.Services.AddScoped<StreamingMiddleware>();

// ── Observability Services — cost tracking, audit log, conversation export ─
// (Registered below alongside Adaptive Card state)

// Collaborative Adaptive Cards — in-memory multi-user card state with SignalR sync
builder.Services.AddSingleton(sp =>
    new InMemoryAdaptiveCardState(
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<ILogger<InMemoryAdaptiveCardState>>()));
builder.Services.AddSingleton<IAdaptiveCardState>(sp => sp.GetRequiredService<InMemoryAdaptiveCardState>());

// Observability Suite — cost tracking, audit log, conversation export
// Cost history is SQLite-backed in the shared writable data directory (same as
// audit.db / memory.db). When that directory is the mounted Azure Files share
// (deployed ACA) history survives replica replacement and scale-to-zero; locally
// it is an ephemeral temp directory. Single-writer only (API runs maxReplicas: 1).
string costDbPath = Path.Combine(dataDirectory, "costs.db");
builder.Services.AddSingleton(sp => new DurableCostTracker(
    costDbPath,
    sp.GetRequiredService<IOptions<ObservabilityOptions>>(),
    sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ICostTracker>(sp => sp.GetRequiredService<DurableCostTracker>());

// Anonymous mode: decorate the cost tracker so every recorded usage event is also counted
// against the anonymous daily token/cost circuit breaker. The decorator delegates unchanged to
// the durable tracker (audit/export/telemetry intact) and feeds the SAME numbers to the budget —
// cache hits arrive as zero-token events and therefore never advance the token/cost ceilings.
if (anonymousAuthMode)
{
    builder.Services.AddSingleton<ICostTracker>(sp => new RetailPulse.Api.Security.Anonymous.AnonymousBudgetCostTracker(
        sp.GetRequiredService<DurableCostTracker>(),
        sp.GetRequiredService<RetailPulse.Api.Security.Anonymous.AnonymousUsageBudget>(),
        sp.GetRequiredService<IConfiguration>()));
}
string auditDbPath = Path.Combine(dataDirectory, "audit.db");
builder.Services.AddSingleton(_ => new DurableAuditLog(auditDbPath));
builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<DurableAuditLog>());
builder.Services.AddSingleton<ConversationExporter>();
builder.Services.AddSingleton<IConversationExport>(sp => sp.GetRequiredService<ConversationExporter>());

// Register IChatClient — Azure OpenAI via APIM AI Gateway.
// In Production we fail fast if neither an APIM subscription key nor a direct
// OpenAI API key is configured while managed identity is disabled.
var openAiConnection = OpenAiConnectionSettings.Load(
    builder.Configuration,
    builder.Environment);

// NetworkTimeout caps a single HTTP attempt (one LLM roundtrip) to the AI Gateway.
// 45s accommodates complex reasoning chains and tool-augmented responses that
// legitimately take 15-30s. The 90s request-level timeout in /api/chat is the ceiling.
// RetryPolicy: 2 retries with exponential backoff (respects Retry-After headers from
// APIM). This handles transient 429 bursts from multi-tool queries (e.g. cross-region
// comparisons that generate 4+ sequential LLM calls) without burning the full request
// budget. The SDK's exponential backoff starts at ~800ms and caps individual waits at
// ~4s, so 2 retries add at most ~8s — well within the 90s request-level ceiling.
var azureClientOptions = new Azure.AI.OpenAI.AzureOpenAIClientOptions
{
    NetworkTimeout = TimeSpan.FromSeconds(45),
    RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 2)
};

Azure.AI.OpenAI.AzureOpenAIClient azureClient = openAiConnection.CreateClient(azureClientOptions);

string agentDeployment = builder.Configuration["OpenAI:Deployment"] ?? agentDef.Model;

builder.Services.AddChatClient(
    azureClient.GetChatClient(agentDeployment).AsIChatClient())
    .UseFunctionInvocation(configure: client =>
        // Cap tool-call iterations: 3 allows the full tool-calling lifecycle:
        //   Iteration 1 — LLM decides to call tools (fast, ~5-10s).
        //   Iteration 2 — LLM sees tool results, may call more tools OR synthesize text.
        //   Iteration 3 — Safety margin for multi-step reasoning chains.
        // Why not 1: the LLM needs a SECOND turn after tools execute to synthesize
        // results into a natural-language response. With 1, response.Text is always
        // empty after tool calls → the fallback fires → wasted tokens.
        // Why not unlimited: prevents runaway loops; the 90s request-level timeout
        // is the hard ceiling regardless.
        client.MaximumIterationsPerRequest = 3)
    // EnableSensitiveData logs full prompts, responses, and tool arguments as span
    // attributes — these can contain user PII. Default to OFF in every environment
    // (including Development) and only enable when an operator explicitly opts in via
    // the Telemetry:EnableSensitiveData config flag for short, deliberate debugging.
    .UseOpenTelemetry(configure: c =>
        c.EnableSensitiveData = builder.Configuration.GetValue("Telemetry:EnableSensitiveData", false));

// ── Router-specific IChatClient (lighter model for intent classification) ───
// If OpenAI:RouterDeployment is configured and non-empty, create a separate
// ChatClient for the router using a smaller/cheaper model deployment.
// This reduces TPM consumption on the shared quota since routing is a simple
// JSON classification task (~500 tokens) that doesn't need the full reasoning model.
string? routerDeployment = builder.Configuration["OpenAI:RouterDeployment"];
if (!string.IsNullOrWhiteSpace(routerDeployment) && routerDeployment != agentDeployment)
{
    IChatClient routerChatClient = azureClient.GetChatClient(routerDeployment).AsIChatClient();
    builder.Services.AddKeyedSingleton("router", routerChatClient);
}
else
{
    // Fall back: router uses the same model as agents (backward compatible)
    builder.Services.AddKeyedSingleton("router",
        (sp, _) => sp.GetRequiredService<IChatClient>());
}

// Foundry agent — optional, controlled by FoundryAgent:Enabled config (default: false)
bool foundryEnabled = builder.Configuration.GetValue("FoundryAgent:Enabled", false);

if (foundryEnabled)
{
    string foundryProjectEndpoint = builder.Configuration["FoundryAgent:ProjectEndpoint"]
        ?? (builder.Environment.IsDevelopment()
            ? "https://bs-dev-swedencentral-aoai.services.ai.azure.com/api/projects/bs-dev-swedencentral-aoai-project"
            : null)
        ?? throw new InvalidOperationException(
            "Configuration value 'FoundryAgent:ProjectEndpoint' is required when FoundryAgent:Enabled=true outside of Development.");

    builder.Services.AddAzureAgent<IDistributionAnalysisAgent>(options =>
    {
        options.FriendlyName = builder.Configuration["FoundryAgent:ShipmentAgentName"]
            ?? "Distribution Analysis Specialist";
        options.ProjectEndpoint = foundryProjectEndpoint;
        options.DirectAgentId = builder.Configuration["FoundryAgent:ShipmentAgentId"];
    });

    builder.Services.AddScoped<FoundryShipmentAgent>();
}
else
{
    builder.Services.AddScoped<LocalShipmentAnalyzer>();
}

// ── Named tool registry (issue #98) ─────────────────────────────────────
// Every tool a specialist can request in prompts.yaml is registered here by name.
// AgentToolRegistry.ValidateAllReferences() fails startup if a specialist references
// a name we didn't register, so misconfiguration is caught before serving traffic.
var toolRegistry = new AgentToolRegistry();

toolRegistry
    .Register("GetDepletionStats", sp => AIFunctionFactory.Create(
        sp.GetRequiredService<DepletionStatsTool>().GetDepletionStats))
    .Register("GetPortfolioDepletionStats", sp => AIFunctionFactory.Create(
        sp.GetRequiredService<PortfolioDepletionStatsTool>().GetPortfolioDepletionStats))
    .Register("GetFieldSentiment", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<FieldSentimentTool>().GetFieldSentiment)))
    .Register("GetShipmentStats", sp => AIFunctionFactory.Create(
        sp.GetRequiredService<ShipmentStatsTool>().GetShipmentStats))
    .Register("GetVariantMix", sp => AIFunctionFactory.Create(
        sp.GetRequiredService<VariantMixTool>().GetVariantMix))
    .Register("CreateChart", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<ChartDataTool>().CreateChart)))
    .Register("AnalyzeShipments", sp => foundryEnabled
        ? AIFunctionFactory.Create(sp.GetRequiredService<FoundryShipmentAgent>().AnalyzeShipments)
        : AIFunctionFactory.Create(sp.GetRequiredService<LocalShipmentAnalyzer>().AnalyzeShipments));

// Demand-forecasting tools (legacy obsolete proxies retained for parity)
#pragma warning disable CS0618
toolRegistry
    .Register("GetHistoricalDemand", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<HistoricalDemandTool>().GetHistoricalDemand)))
    .Register("GenerateForecast", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<ForecastTool>().GenerateForecast)))
    .Register("GetSeasonalityFactors", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<SeasonalityFactorsTool>().GetSeasonalityFactors)))
    .Register("IdentifyDemandRisks", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<DemandRisksTool>().IdentifyDemandRisks)));
#pragma warning restore CS0618

// Promotion tools
toolRegistry
    .Register("GetPromoHistory", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<PromoHistoryTool>().GetPromoHistory)))
    .Register("CalculateLift", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<CalculateLiftTool>().CalculateLift)))
    .Register("EvaluateTiming", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<EvaluateTimingTool>().EvaluateTiming)))
    .Register("EstimateROI", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<EstimateROITool>().EstimateROI)))
    .Register("RequestApproval", sp => AIFunctionFactory.Create(
        sp.GetRequiredService<ApprovalTool>().RequestApproval));

// Competitive intelligence tools
toolRegistry
    .Register("GetCompetitorPricing", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<CompetitorPricingTool>().GetCompetitorPricing)))
    .Register("GetMarketShare", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<MarketShareTool>().GetMarketShare)))
    .Register("DetectThreats", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<DetectThreatsTool>().DetectThreats)))
    .Register("GetCompetitiveLandscape", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<CompetitiveLandscapeTool>().GetCompetitiveLandscape)));

// Supply chain tools
toolRegistry
    .Register("GetInventoryLevels", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<InventoryLevelsTool>().GetInventoryLevels)))
    .Register("GetSupplyDisruptions", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<SupplyDisruptionsTool>().GetSupplyDisruptions)))
    .Register("GetFulfillmentRate", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<FulfillmentRateTool>().GetFulfillmentRate)))
    .Register("GetSupplyHealthSummary", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<SupplyHealthTool>().GetSupplyHealthSummary)));

// Store ops + planogram tools
toolRegistry
    .Register("GetStorePerformance", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<StorePerformanceTool>().GetStorePerformance)))
    .Register("GetShelfLayout", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<ShelfLayoutTool>().GetShelfLayout)))
    .Register("OptimizePlanogram", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<OptimizePlanogramTool>().OptimizePlanogram)))
    .Register("PredictStockout", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<PredictStockoutTool>().PredictStockout)));

// Margin analysis tools
toolRegistry
    .Register("GetMarginByBrand", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<MarginByBrandTool>().GetMarginByBrand)))
    .Register("GetMarginDrivers", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<MarginDriversTool>().GetMarginDrivers)))
    .Register("GetMarginTrend", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<MarginTrendTool>().GetMarginTrend)))
    .Register("DetectMarginRisks", sp => WrapCached(sp, AIFunctionFactory.Create(
        sp.GetRequiredService<DetectMarginRisksTool>().DetectMarginRisks)));

// Orchestration intents — router fast-paths for in-process orchestrators that
// have no specialist owner. Keeps council/health + scorecard/portfolio detectable
// even when the ConsensusOrchestrator is not registered.
var orchestrationIntents = new List<RouterIntentConfig>
{
    new("council/health",
        ["how healthy is", "brand health report", "portfolio health",
         "overall assessment for", "how is the portfolio"]),
    new("scorecard/portfolio",
        ["scorecard", "portfolio scoring", "brand ranking",
         "top brand", "worst brand", "executive brief"]),
};

// Register the multi-agent routing pipeline — every specialist is now enumerated
// from prompts.yaml (issue #98).
builder.Services.AddAgentRouting(promptConfig, toolRegistry, orchestrationIntents);

// Cache-aware tool wrapper used above. Kept local to Program.cs so the registry
// stays free of composition-root concerns.
static AITool WrapCached(IServiceProvider sp, AITool tool)
{
    CachingToolWrapper wrapper = sp.GetRequiredService<CachingToolWrapper>();
    IList<AITool> wrapped = wrapper.WrapAll([tool]);
    return wrapped[0];
}

// Register ConsensusOrchestrator for Portfolio Health Council
if (councilSynthesisDef is not null && councilVoteDef is not null)
{
    builder.Services.AddScoped<RetailPulse.Contracts.Consensus.IConsensusCouncil>(sp =>
    {
        IEnumerable<ISpecialistAgent> specialists = sp.GetServices<ISpecialistAgent>();
        IChatClient chatClient = sp.GetRequiredService<IChatClient>();
        ILogger<ConsensusOrchestrator> logger = sp.GetRequiredService<ILogger<ConsensusOrchestrator>>();
        RouterAgentRoster roster = sp.GetRequiredService<RouterAgentRoster>();

        return new ConsensusOrchestrator(
            specialists,
            chatClient,
            councilSynthesisDef,
            councilVoteDef,
            logger,
            councilParticipants: roster.GetCouncilParticipants());
    });
}

// Register EscalationOrchestrator for L1→L2→L3 escalation
AgentDefinition? escalationSynthDef = councilSynthesisDef ?? scorecardSynthesisDef;
if (escalationSynthDef is not null)
{
    builder.Services.AddScoped(sp =>
    {
        IEnumerable<ISpecialistAgent> specialists = sp.GetServices<ISpecialistAgent>();
        IChatClient chatClient = sp.GetRequiredService<IChatClient>();
        ILogger<EscalationOrchestrator> logger = sp.GetRequiredService<ILogger<EscalationOrchestrator>>();

        return new EscalationOrchestrator(
            specialists, chatClient, escalationSynthDef, logger);
    });
}

// Register ScorecardOrchestrator for portfolio scoring
if (scorecardSynthesisDef is not null)
{
    builder.Services.AddScoped(sp =>
    {
        IEnumerable<ISpecialistAgent> specialists = sp.GetServices<ISpecialistAgent>();
        IChatClient chatClient = sp.GetRequiredService<IChatClient>();
        ILogger<ScorecardOrchestrator> logger = sp.GetRequiredService<ILogger<ScorecardOrchestrator>>();
        RouterAgentRoster roster = sp.GetRequiredService<RouterAgentRoster>();

        return new ScorecardOrchestrator(
            specialists,
            chatClient,
            scorecardSynthesisDef,
            logger,
            scoringDimensions: roster.GetScorecardDimensions());
    });
}

// Register the plan-first orchestrator (issue #93). Wired only when BOTH the
// planner AgentDefinition is present in prompts.yaml AND PlanPersistence is
// enabled (which is what causes AddPlanPersistence to register IPlanStore and
// the cleanup hosted service above). Gating on plannerDef alone leaves the
// executor/orchestrator factories asking for a missing IPlanStore at request
// time — that turns default /api/chat into a 500 whenever a tenant ships a
// planner definition but leaves PlanPersistence off, which is our default.
// The plan-first path in ChatEndpoints stays completely inert when either
// gate is closed: PlanOrchestrator is not registered, [FromServices] resolves
// null, and the endpoint drops through to the single-specialist path.
PlanPersistenceOptions planPersistenceOptsAtRegistration = builder.Configuration
    .GetSection(PlanPersistenceOptions.SectionName)
    .Get<PlanPersistenceOptions>() ?? new PlanPersistenceOptions();
if (plannerDef is not null && planPersistenceOptsAtRegistration.Enabled)
{
    builder.Services.AddScoped(sp =>
    {
        IChatClient chatClient = sp.GetRequiredKeyedService<IChatClient>("router");
        IOptions<PlanPersistenceOptions> opts =
            sp.GetRequiredService<IOptions<PlanPersistenceOptions>>();
        return new PlanBuilder(
            chatClient,
            plannerDef,
            opts.Value,
            sp.GetRequiredService<ILogger<PlanBuilder>>(),
            sp.GetService<ILoggerFactory>());
    });
    builder.Services.AddScoped(sp =>
    {
        IOptions<PlanPersistenceOptions> opts =
            sp.GetRequiredService<IOptions<PlanPersistenceOptions>>();
        return new PlanExecutor(
            sp.GetRequiredService<IPlanStore>(),
            sp.GetRequiredService<ICostTracker>(),
            sp.GetRequiredService<ITraceCollector>(),
            opts.Value,
            sp.GetRequiredService<ILogger<PlanExecutor>>(),
            sp.GetService<PlanClarifier>(),
            sp.GetService<PlanReviewCoordinator>());
    });
    builder.Services.AddScoped(sp =>
    {
        IOptions<PlanPersistenceOptions> opts =
            sp.GetRequiredService<IOptions<PlanPersistenceOptions>>();
        return new PlanOrchestrator(
            sp.GetRequiredService<PlanBuilder>(),
            sp.GetRequiredService<PlanExecutor>(),
            sp.GetRequiredService<IPlanStore>(),
            sp.GetRequiredService<ICostTracker>(),
            opts.Value,
            sp.GetRequiredService<ILogger<PlanOrchestrator>>(),
            sp.GetService<PlanReviewCoordinator>(),
            sp.GetService<IOptions<PlanReviewOptions>>());
    });
}

// Plan review gate (#94). Off by default. When PlanReview:Enabled = true, wires
// the coordinator that inserts a human review pause before plan execution,
// swaps in the plan-aware resume strategy (adopts plan-review rows across
// restarts instead of orphaning), and registers the clarification service
// specialists can use for mid-plan round-trips.
//
// Registered independently of PlanPersistence.Enabled so an operator can
// enable review only when plans persist — the coordinator itself refuses to
// run without the persistence path via the PlanOrchestrator constructor gate.
builder.Services.Configure<PlanReviewOptions>(
    builder.Configuration.GetSection(PlanReviewOptions.SectionName));
PlanReviewOptions planReviewOptsAtRegistration = builder.Configuration
    .GetSection(PlanReviewOptions.SectionName)
    .Get<PlanReviewOptions>() ?? new PlanReviewOptions();
if (planReviewOptsAtRegistration.Enabled)
{
    string reviewCheckpointDir = Path.Combine(
        dataDirectory,
        string.IsNullOrWhiteSpace(planReviewOptsAtRegistration.CheckpointSubdirectory)
            ? "plan-reviews"
            : planReviewOptsAtRegistration.CheckpointSubdirectory);
    Directory.CreateDirectory(reviewCheckpointDir);
    // Framework checkpoint store lives on the same durable data directory as
    // every other SQLite store the API writes. One JSON file per session id.
    // Register the store itself + the CheckpointManager wrapper — both are
    // needed because our PlanReviewCheckpointService calls
    // ICheckpointStore.CreateCheckpointAsync (real write path) and
    // CheckpointManager.GetLatestCheckpointAsync (read helper).
    builder.Services.AddSingleton<Microsoft.Agents.AI.Workflows.Checkpointing.ICheckpointStore<System.Text.Json.JsonElement>>(_ =>
        new Microsoft.Agents.AI.Workflows.Checkpointing.FileSystemJsonCheckpointStore(
            new DirectoryInfo(reviewCheckpointDir)));
    builder.Services.AddSingleton(sp =>
    {
        var store = sp.GetRequiredService<
            Microsoft.Agents.AI.Workflows.Checkpointing.ICheckpointStore<System.Text.Json.JsonElement>>();
        return Microsoft.Agents.AI.Workflows.CheckpointManager.CreateJson(store, customOptions: null);
    });
    builder.Services.AddSingleton<PlanReviewCheckpointService>();

    builder.Services.AddScoped(sp => new PlanReviewCoordinator(
            sp.GetRequiredService<IApprovalGate>(),
            sp.GetRequiredService<IOptions<PlanReviewOptions>>(),
            sp.GetRequiredService<PlanReviewCheckpointService>(),
            sp.GetRequiredService<ILogger<PlanReviewCoordinator>>(),
            sp.GetService<IPlanReviewReplanner>(),
            sp.GetRequiredService<TimeProvider>()));

    // Default replanner delegates to the tenant PlanBuilder — only registered
    // when a planner definition exists. If it is absent the coordinator
    // terminates reject-with-feedback deterministically with ReplanExhausted;
    // never a silent no-op.
    if (plannerDef is not null && planPersistenceOptsAtRegistration.Enabled)
    {
        builder.Services.AddScoped<IPlanReviewReplanner, PlanBuilderReplanner>();
    }

    // Register the concrete first so the completion service can inject the
    // real class (it needs OpenAsync/InterpretAnswer which live on the
    // implementation, not the interface). The interface points at the same
    // singleton.
    builder.Services.AddSingleton<PlanClarifier>();
    builder.Services.AddSingleton<IPlanClarifier>(sp => sp.GetRequiredService<PlanClarifier>());

    // Swap the reconciliation resume strategy for the plan-aware one. Tool rows
    // still orphan terminally; plan-review / clarification rows are adopted so
    // the human decision arriving after a restart proceeds normally.
    builder.Services.RemoveAll<IApprovalResumeStrategy>();
    builder.Services.AddSingleton<IApprovalResumeStrategy, PlanReviewResumeStrategy>();

    // Wave 2: durable completion service + restart recovery + timeout sweep.
    builder.Services.AddSingleton<PlanReviewCompletionService>();
    builder.Services.AddHostedService<PlanReviewRestartRecoveryService>();
    builder.Services.AddHostedService<PlanReviewTimeoutBackgroundService>();
}

// Register ExplainabilityService (singleton for cross-request trace storage)
builder.Services.AddSingleton<RetailPulse.Api.Explainability.ExplainabilityService>();

builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// Install the Content Safety tool-result ambient inspector so the non-Agents
// tool-result seam (Budget/BudgetedAIFunction.cs) can consult it without any
// change to the AgentExecutionPipeline construction under src/RetailPulse.Api/
// Agents/**. The inspector short-circuits internally when Content Safety is
// disabled, so leaving it installed on the disabled path is a no-op.
ContentSafetyToolResultAmbient.Install(
    app.Services.GetRequiredService<ContentSafetyToolResultInspector>());

// Seed RAG knowledge base with sample documents (idempotent)
{
    // Run the startup probe for the configured provider. FailLoud lets a
    // cloud outage propagate and abort startup; FallbackToInMemory swaps to
    // the always-available in-memory instance and logs a prominent warning.
    // The probe is a no-op for the InMemory default.
    DegradingKnowledgeBase kb = app.Services.GetRequiredService<DegradingKnowledgeBase>();
    await kb.ProbeAsync();

    // Seed the sample corpus into the in-memory instance whenever it is the
    // provider that will actually serve requests — either as the configured
    // primary or because degradation swapped it in. Cloud providers manage
    // their own corpora and are not seeded from process start.
    ILogger seedLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("KnowledgeBaseSeeder");
    if (kb.ActiveProviderName == InMemoryKnowledgeBase.ProviderName)
    {
        InMemoryKnowledgeBase inMemory = app.Services.GetRequiredService<InMemoryKnowledgeBase>();
        await KnowledgeBaseSeeder.SeedAsync(inMemory, seedLogger);
    }
    else
    {
        seedLogger.LogInformation(
            "Knowledge provider '{Provider}' is active — skipping in-memory sample corpus seeding.",
            kb.ActiveProviderName);
    }
}

// Select CORS policy based on environment
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors(app.Environment.IsDevelopment() ? "Development" : "Production");
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Anonymous mode central enforcement: read-only restriction (block mutations), request-size
// bound, per-subject/per-IP chat limits, the daily billable-use circuit breaker, and a per-request
// timeout. Runs after authorization so the validated anonymous principal is available; no-op for
// any non-anonymous principal. Registered only when Authentication:Mode=Anonymous.
if (anonymousAuthMode)
{
    app.UseMiddleware<AnonymousGuardMiddleware>();
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Scalar API documentation UI at /api/docs
    app.MapGet("/api/docs", () => Results.Content("""
        <!DOCTYPE html>
        <html>
        <head>
            <title>RetailPulse API Documentation</title>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <link rel="icon" type="image/svg+xml" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><text y='.9em' font-size='90'>📊</text></svg>" />
        </head>
        <body>
            <script id="api-reference" data-url="/openapi/v1.json"></script>
            <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference"></script>
        </body>
        </html>
        """, "text/html")).ExcludeFromDescription();
}

// SignalR hubs — require authorization
app.MapHub<TelemetryHub>("/hubs/telemetry").RequireAuthorization();
app.MapHub<StreamingHub>("/hubs/streaming").RequireAuthorization();

// ── Endpoint registration (extension methods) ───────────────────────────
app.MapChatEndpoints(agentDef);
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

// Durable session endpoints — mapped only when SessionPersistence:Enabled is true so
// no session surface exists when the feature is off. See MapSessionEndpoints and
// SessionPersistenceServiceExtensions for the rationale (mirrors the Anonymous/GitHub
// opt-in endpoint conventions above).
if (app.Services.GetService<ISessionStore>() is not null)
{
    app.MapSessionEndpoints();
}

// Durable plan endpoints (issue #93) — mapped only when PlanPersistence:Enabled
// is true. Mirrors the session-endpoints opt-in convention so no plan surface
// exists when persistence is off.
if (app.Services.GetService<IPlanStore>() is not null)
{
    app.MapPlanEndpoints();
    // Plan review endpoints (#94) — mapped only when the plan store is available
    // AND PlanReview is enabled. Cross-subject decisions collapse to 404.
    if (planReviewOptsAtRegistration.Enabled)
    {
        app.MapPlanReviewEndpoints();
    }
}

// Anonymous mode: map the single unauthenticated bootstrap endpoint that mints short-lived
// session tokens for a future frontend (Sprint 3). Mapped only when Authentication:Mode=Anonymous
// so it exposes no anonymous surface in Entra deployments.
if (anonymousAuthMode)
{
    app.MapAnonymousAuthEndpoints();
}

// GitHub mode: map the three narrowly-anonymous confidential OAuth BFF endpoints (start / callback /
// exchange). Mapped only when Authentication:Mode=GitHub so no GitHub surface exists in Entra
// deployments. The GitHub provider token never reaches the browser; the SPA receives only a one-time
// redemption code and, after exchange, a short-lived Retail Pulse session token.
if (gitHubAuthMode)
{
    app.MapGitHubAuthEndpoints();
}

app.Run();
