using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Security;
using RetailPulse.Api.Tools;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Alerts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Caching;
using RetailPulse.Contracts.Cards;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Health;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Resilience;
using RetailPulse.Api.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults (OTel, health checks, service discovery)
builder.AddServiceDefaults();

// In-memory cache (used by MCP response caching handler, etc.)
builder.Services.AddMemoryCache();

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
builder.Services.Configure<ObservabilityOptions>(builder.Configuration.GetSection(ObservabilityOptions.SectionName));

// Load tenant configuration
var tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml");
var tenantProvider = new FileTenantProvider(tenantConfigPath);
builder.Services.AddSingleton<ITenantProvider>(tenantProvider);

// Add our custom ActivitySource to the OTel pipeline
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("RetailPulse.Agent").AddSource("RetailPulse.Alerts"))
    .WithMetrics(metrics => metrics.AddMeter(RetailPulseMetrics.MeterName));

// SignalR for real-time telemetry
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// ── CORS — split policies for Development vs Production ─────────────────
var corsDevOrigins = new[] { "http://localhost:5173", "https://localhost:5173" };
var corsProdOrigins = builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Development", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });

    options.AddPolicy("Production", policy =>
    {
        if (corsProdOrigins.Length > 0)
        {
            policy.WithOrigins(corsProdOrigins)
                .WithMethods("GET", "POST", "PUT", "DELETE")
                .WithHeaders("Content-Type", "Authorization", "X-Requested-With")
                .AllowCredentials();
        }
        // If no origins configured, policy allows nothing (deny by default)
    });
});

// ── Authentication & Authorization ──────────────────────────────────────
var requireAuth = builder.Configuration.GetValue("Security:RequireAuth",
    !builder.Environment.IsDevelopment());

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication(DevelopmentAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(
            DevelopmentAuthHandler.SchemeName, _ => { })
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            // JWT scheme available but not default in dev — allows testing with real tokens
            options.Authority = builder.Configuration["Security:JwtAuthority"];
            options.TokenValidationParameters.ValidateAudience = false;
        });
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = builder.Configuration["Security:JwtAuthority"];
            options.Audience = builder.Configuration["Security:JwtAudience"];
        });
}

builder.Services.AddAuthorization();

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
var promptsPath = Path.Combine(builder.Environment.ContentRootPath, "prompts.yaml");
var promptConfig = RetailPulse.Api.Agents.RetailPulseAgent.LoadPrompts(promptsPath);
var agentDef = promptConfig.Agents["retail-pulse"];

var tenant = tenantProvider.GetTenant();
var promptEngine = new RetailPulse.Api.Prompts.PromptTemplateEngine(tenant);

// Hydrate all agent definitions with tenant placeholders
promptEngine.Hydrate(agentDef);

var demandForecastDef = promptConfig.Agents["demand-forecast"];
promptEngine.Hydrate(demandForecastDef);

// Router prompt doesn't need tenant placeholders (intent classification is domain-generic)
var routerDef = promptConfig.Agents["router"];

var promoPlanningDef = promptConfig.Agents.TryGetValue("promo-planning", out var promoDef) ? promoDef : null;
if (promoPlanningDef != null) promptEngine.Hydrate(promoPlanningDef);

var competitiveIntelDef = promptConfig.Agents.TryGetValue("competitive-intel", out var compDef) ? compDef : null;
if (competitiveIntelDef != null) promptEngine.Hydrate(competitiveIntelDef);

var supplyChainDef = promptConfig.Agents.TryGetValue("supply-chain", out var scDef) ? scDef : null;
if (supplyChainDef != null) promptEngine.Hydrate(supplyChainDef);

// Load council synthesis and vote prompt definitions
var councilSynthesisDef = promptConfig.Agents.TryGetValue("council-synthesis", out var synthDef) ? synthDef : null;
var councilVoteDef = promptConfig.Agents.TryGetValue("council-vote", out var vDef) ? vDef : null;

var storeOpsDef = promptConfig.Agents.TryGetValue("store-ops", out var soDef) ? soDef : null;
if (storeOpsDef != null) promptEngine.Hydrate(storeOpsDef);

var planogramDef = promptConfig.Agents.TryGetValue("planogram", out var pgDef) ? pgDef : null;
if (planogramDef != null) promptEngine.Hydrate(planogramDef);

var marginDef = promptConfig.Agents.TryGetValue("margin", out var mrgDef) ? mrgDef : null;
if (marginDef != null) promptEngine.Hydrate(marginDef);

// Load scorecard and exec-brief synthesis definitions
var scorecardSynthesisDef = promptConfig.Agents.TryGetValue("scorecard-synthesis", out var scSynthDef) ? scSynthDef : null;
var execBriefDef = promptConfig.Agents.TryGetValue("exec-brief", out var ebDef) ? ebDef : null;

// Load field-sentiment agent definition
var fieldSentimentDef = promptConfig.Agents.TryGetValue("field-sentiment", out var fsDef) ? fsDef : null;
if (fieldSentimentDef != null) promptEngine.Hydrate(fieldSentimentDef);

// Register HttpClient for MCP server communication. The default URL is a
// dev convenience — production should always set McpServer:BaseUrl.
var mcpBaseUrl = builder.Configuration["McpServer:BaseUrl"]
    ?? (builder.Environment.IsDevelopment() ? "http://localhost:5200" : null)
    ?? throw new InvalidOperationException(
        "Configuration value 'McpServer:BaseUrl' is required outside of Development.");
builder.Services.AddTransient<McpResponseCachingHandler>();
builder.Services.AddHttpClient("McpServer", client =>
{
    client.BaseAddress = new Uri(mcpBaseUrl);
}).AddHttpMessageHandler<McpResponseCachingHandler>()
  .AddMcpResilienceHandler();

// Register tools
builder.Services.AddScoped<DepletionStatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DepletionStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DepletionStatsTool>>());
});
builder.Services.AddScoped<PortfolioDepletionStatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new PortfolioDepletionStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<PortfolioDepletionStatsTool>>());
});
builder.Services.AddScoped<FieldSentimentTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new FieldSentimentTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<FieldSentimentTool>>());
});
builder.Services.AddScoped<ShipmentStatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ShipmentStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<ShipmentStatsTool>>());
});

// Chart data tool (always available)
builder.Services.AddScoped<ChartDataTool>();

builder.Services.AddScoped<VariantMixTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new VariantMixTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<VariantMixTool>>());
});

// Demand forecasting tools (deprecated — kept for backward compat during v2 transition)
#pragma warning disable CS0618 // Obsolete demand tool proxies still registered for legacy agent pipeline
builder.Services.AddScoped<HistoricalDemandTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new HistoricalDemandTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<HistoricalDemandTool>>());
});
builder.Services.AddScoped<ForecastTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ForecastTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<ForecastTool>>());
});
builder.Services.AddScoped<SeasonalityFactorsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new SeasonalityFactorsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<SeasonalityFactorsTool>>());
});
builder.Services.AddScoped<DemandRisksTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DemandRisksTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DemandRisksTool>>());
});
#pragma warning restore CS0618

// Promo planning tools
builder.Services.AddScoped<PromoHistoryTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new PromoHistoryTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<PromoHistoryTool>>());
});
builder.Services.AddScoped<CalculateLiftTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new CalculateLiftTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<CalculateLiftTool>>());
});
builder.Services.AddScoped<EvaluateTimingTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new EvaluateTimingTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<EvaluateTimingTool>>());
});
builder.Services.AddScoped<EstimateROITool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new EstimateROITool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<EstimateROITool>>());
});

// Competitive intelligence tools
builder.Services.AddScoped<CompetitorPricingTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new CompetitorPricingTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<CompetitorPricingTool>>());
});
builder.Services.AddScoped<MarketShareTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new MarketShareTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<MarketShareTool>>());
});
builder.Services.AddScoped<DetectThreatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DetectThreatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DetectThreatsTool>>());
});
builder.Services.AddScoped<CompetitiveLandscapeTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new CompetitiveLandscapeTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<CompetitiveLandscapeTool>>());
});

// Supply chain tools
builder.Services.AddScoped<InventoryLevelsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new InventoryLevelsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<InventoryLevelsTool>>());
});
builder.Services.AddScoped<SupplyDisruptionsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new SupplyDisruptionsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<SupplyDisruptionsTool>>());
});
builder.Services.AddScoped<FulfillmentRateTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new FulfillmentRateTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<FulfillmentRateTool>>());
});
builder.Services.AddScoped<SupplyHealthTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new SupplyHealthTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<SupplyHealthTool>>());
});

// Store operations tools
builder.Services.AddScoped<StorePerformanceTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new StorePerformanceTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<StorePerformanceTool>>());
});
builder.Services.AddScoped<ShelfLayoutTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ShelfLayoutTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<ShelfLayoutTool>>());
});
builder.Services.AddScoped<OptimizePlanogramTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new OptimizePlanogramTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<OptimizePlanogramTool>>());
});
builder.Services.AddScoped<PredictStockoutTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new PredictStockoutTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<PredictStockoutTool>>());
});

// Margin analysis tools
builder.Services.AddScoped<MarginByBrandTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new MarginByBrandTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<MarginByBrandTool>>());
});
builder.Services.AddScoped<MarginDriversTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new MarginDriversTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<MarginDriversTool>>());
});
builder.Services.AddScoped<MarginTrendTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new MarginTrendTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<MarginTrendTool>>());
});
builder.Services.AddScoped<DetectMarginRisksTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DetectMarginRisksTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DetectMarginRisksTool>>());
});

// Human-in-the-loop approval gate (SQLite-backed, singleton for shared state)
var approvalDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "approvals.db");
builder.Services.AddSingleton<IApprovalGate>(sp =>
    new SqliteApprovalGate(approvalDbPath, sp.GetRequiredService<ILogger<SqliteApprovalGate>>()));

// Approval tool — available to specialist agents for high-impact recommendations
builder.Services.AddScoped<ApprovalTool>(sp =>
    new ApprovalTool(
        sp.GetRequiredService<IApprovalGate>(),
        sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<ILogger<ApprovalTool>>()));

// Conversation memory — SQLite-backed, per-user, with configurable TTL
// Conversation memory — SQLite-backed with bounded-channel background extraction
var memoryDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "memory.db");
builder.Services.AddConversationMemory(memoryDbPath);

// Proactive alerts — background anomaly detection with SQLite persistence
var alertsDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "alerts.db");
builder.Services.AddProactiveAlerts(alertsDbPath);

// Distributed tracing — in-memory ring buffer with bounded-channel SignalR push
builder.Services.AddSingleton<RetailPulse.Api.Tracing.TelemetryPushChannel>();
builder.Services.AddSingleton<InMemoryTraceCollector>(sp =>
    new InMemoryTraceCollector(
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<RetailPulse.Api.Tracing.TelemetryPushChannel>()));
builder.Services.AddSingleton<ITraceCollector>(sp => sp.GetRequiredService<InMemoryTraceCollector>());
builder.Services.AddHostedService<RetailPulse.Api.Tracing.TelemetryPushBackgroundService>();

// ── Pre-demo cache warming (populates MCP response cache on startup) ────
builder.Services.AddHostedService<RetailPulse.Api.Startup.CacheWarmingService>();

// RAG Knowledge Base — in-memory BM25-based document store (no Azure dependency)
builder.Services.AddSingleton<InMemoryKnowledgeBase>();
builder.Services.AddSingleton<IKnowledgeBase>(sp => sp.GetRequiredService<InMemoryKnowledgeBase>());
builder.Services.AddSingleton<RagContextProvider>();

// Response cache — in-memory with TTL expiration and LRU eviction
builder.Services.AddSingleton<RetailPulse.Api.Caching.InMemoryResponseCache>();
builder.Services.AddSingleton<IResponseCache>(sp => sp.GetRequiredService<RetailPulse.Api.Caching.InMemoryResponseCache>());

// Guardrails — suspicious request log (ring buffer) and runtime config
builder.Services.AddSingleton<RetailPulse.Api.Guardrails.InMemorySuspiciousRequestLog>();
builder.Services.AddSingleton<ISuspiciousRequestLog>(sp => sp.GetRequiredService<RetailPulse.Api.Guardrails.InMemorySuspiciousRequestLog>());
builder.Services.AddSingleton<GuardrailsConfig>();

// Guardrails middleware — input filtering (jailbreak, injection) + output PII redaction
builder.Services.AddScoped<GuardrailsMiddleware>();

// Streaming middleware — progressive token delivery via SignalR
builder.Services.AddScoped<StreamingMiddleware>();

// ── Observability Services — cost tracking, audit log, conversation export ─
// (Registered below alongside Adaptive Card state)

// Collaborative Adaptive Cards — in-memory multi-user card state with SignalR sync
builder.Services.AddSingleton<InMemoryAdaptiveCardState>(sp =>
    new InMemoryAdaptiveCardState(
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<ILogger<InMemoryAdaptiveCardState>>()));
builder.Services.AddSingleton<IAdaptiveCardState>(sp => sp.GetRequiredService<InMemoryAdaptiveCardState>());

// Observability Suite — cost tracking, audit log, conversation export
builder.Services.AddSingleton<InMemoryCostTracker>();
builder.Services.AddSingleton<ICostTracker>(sp => sp.GetRequiredService<InMemoryCostTracker>());
var auditDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "audit.db");
builder.Services.AddSingleton<DurableAuditLog>(_ => new DurableAuditLog(auditDbPath));
builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<DurableAuditLog>());
builder.Services.AddSingleton<ConversationExporter>();
builder.Services.AddSingleton<IConversationExport>(sp => sp.GetRequiredService<ConversationExporter>());

// Register IChatClient — Azure OpenAI via APIM AI Gateway.
// In Production we fail fast if the API key is missing rather than silently
// using "demo-key" which would surface as opaque 401s at runtime.
var openAiEndpoint = builder.Configuration["OpenAI:Endpoint"]
    ?? (builder.Environment.IsDevelopment()
        ? "https://bsapim-dev-northcentralus-001.azure-api.net/inference"
        : null)
    ?? throw new InvalidOperationException(
        "Configuration value 'OpenAI:Endpoint' is required outside of Development.");

var openAiApiKey = builder.Configuration["OpenAI:ApiKey"];
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    if (builder.Environment.IsDevelopment())
    {
        openAiApiKey = "demo-key";
    }
    else
    {
        throw new InvalidOperationException(
            "Configuration value 'OpenAI:ApiKey' is required outside of Development.");
    }
}

// NetworkTimeout caps a single HTTP attempt (one LLM roundtrip) to the AI Gateway.
// With function invocation, there are multiple sequential LLM calls per request:
// first to decide which tools to call, then to synthesize results. Each call must
// complete within this window. 90s accommodates large-context synthesis calls while
// the 120s request-level timeout in /api/chat still provides the overall ceiling.
var azureClientOptions = new Azure.AI.OpenAI.AzureOpenAIClientOptions
{
    NetworkTimeout = TimeSpan.FromSeconds(90)
};

var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
    new Uri(openAiEndpoint),
    new System.ClientModel.ApiKeyCredential(openAiApiKey),
    azureClientOptions);

builder.Services.AddChatClient(
    azureClient.GetChatClient(agentDef.Model).AsIChatClient())
    .UseFunctionInvocation(configure: client =>
    {
        // Cap tool-call iterations to prevent infinite loops where the model
        // keeps requesting tools without producing a final answer. 2 rounds
        // is optimal: most queries complete in 1 iteration, complex analyses
        // get a second pass. Reduces avg response time ~15% vs 3 iterations.
        client.MaximumIterationsPerRequest = 2;
    })
    // EnableSensitiveData logs prompts, responses, and tool arguments which
    // can include user PII. Only enable in Development.
    .UseOpenTelemetry(configure: c => c.EnableSensitiveData = builder.Environment.IsDevelopment());

// Foundry agent — optional, controlled by FoundryAgent:Enabled config (default: false)
var foundryEnabled = builder.Configuration.GetValue<bool>("FoundryAgent:Enabled", false);

if (foundryEnabled)
{
    var foundryProjectEndpoint = builder.Configuration["FoundryAgent:ProjectEndpoint"]
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

// Register the multi-agent routing pipeline
builder.Services.AddAgentRouting(promptConfig, agentDef, foundryEnabled, sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var depletionTool = sp.GetRequiredService<DepletionStatsTool>();
    var portfolioTool = sp.GetRequiredService<PortfolioDepletionStatsTool>();
    var sentimentTool = sp.GetRequiredService<FieldSentimentTool>();
    var shipmentTool = sp.GetRequiredService<ShipmentStatsTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var variantMixTool = sp.GetRequiredService<VariantMixTool>();

    var tools = new List<AITool>
    {
        AIFunctionFactory.Create(depletionTool.GetDepletionStats),
        AIFunctionFactory.Create(portfolioTool.GetPortfolioDepletionStats),
        AIFunctionFactory.Create(sentimentTool.GetFieldSentiment),
        AIFunctionFactory.Create(shipmentTool.GetShipmentStats),
        AIFunctionFactory.Create(variantMixTool.GetVariantMix),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };

    // Add either Foundry or local shipment analyzer
    if (foundryEnabled)
    {
        var foundryAgent = sp.GetRequiredService<FoundryShipmentAgent>();
        tools.Add(AIFunctionFactory.Create(foundryAgent.AnalyzeShipments));
    }
    else
    {
        var localAnalyzer = sp.GetRequiredService<LocalShipmentAnalyzer>();
        tools.Add(AIFunctionFactory.Create(localAnalyzer.AnalyzeShipments));
    }

    return tools;
},
demandForecastDef: demandForecastDef,
demandToolsFactory: sp =>
{
#pragma warning disable CS0618 // Obsolete demand tool proxies still used in legacy agent pipeline
    var historicalDemandTool = sp.GetRequiredService<HistoricalDemandTool>();
    var forecastTool = sp.GetRequiredService<ForecastTool>();
    var seasonalityTool = sp.GetRequiredService<SeasonalityFactorsTool>();
    var demandRisksTool = sp.GetRequiredService<DemandRisksTool>();
#pragma warning restore CS0618
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var approvalTool = sp.GetRequiredService<ApprovalTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(historicalDemandTool.GetHistoricalDemand),
        AIFunctionFactory.Create(forecastTool.GenerateForecast),
        AIFunctionFactory.Create(seasonalityTool.GetSeasonalityFactors),
        AIFunctionFactory.Create(demandRisksTool.IdentifyDemandRisks),
        AIFunctionFactory.Create(chartTool.CreateChart),
        AIFunctionFactory.Create(approvalTool.RequestApproval)
    };
},
promoPlanningDef: promoPlanningDef,
promoToolsFactory: sp =>
{
    var promoHistoryTool = sp.GetRequiredService<PromoHistoryTool>();
    var calculateLiftTool = sp.GetRequiredService<CalculateLiftTool>();
    var evaluateTimingTool = sp.GetRequiredService<EvaluateTimingTool>();
    var estimateROITool = sp.GetRequiredService<EstimateROITool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var approvalTool = sp.GetRequiredService<ApprovalTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(promoHistoryTool.GetPromoHistory),
        AIFunctionFactory.Create(calculateLiftTool.CalculateLift),
        AIFunctionFactory.Create(evaluateTimingTool.EvaluateTiming),
        AIFunctionFactory.Create(estimateROITool.EstimateROI),
        AIFunctionFactory.Create(chartTool.CreateChart),
        AIFunctionFactory.Create(approvalTool.RequestApproval)
    };
},
competitiveIntelDef: competitiveIntelDef,
competitiveToolsFactory: sp =>
{
    var competitorPricingTool = sp.GetRequiredService<CompetitorPricingTool>();
    var marketShareTool = sp.GetRequiredService<MarketShareTool>();
    var detectThreatsTool = sp.GetRequiredService<DetectThreatsTool>();
    var competitiveLandscapeTool = sp.GetRequiredService<CompetitiveLandscapeTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(competitorPricingTool.GetCompetitorPricing),
        AIFunctionFactory.Create(marketShareTool.GetMarketShare),
        AIFunctionFactory.Create(detectThreatsTool.DetectThreats),
        AIFunctionFactory.Create(competitiveLandscapeTool.GetCompetitiveLandscape),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };
},
supplyChainDef: supplyChainDef,
supplyToolsFactory: sp =>
{
    var inventoryTool = sp.GetRequiredService<InventoryLevelsTool>();
    var disruptionsTool = sp.GetRequiredService<SupplyDisruptionsTool>();
    var fulfillmentTool = sp.GetRequiredService<FulfillmentRateTool>();
    var supplyHealthTool = sp.GetRequiredService<SupplyHealthTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(inventoryTool.GetInventoryLevels),
        AIFunctionFactory.Create(disruptionsTool.GetSupplyDisruptions),
        AIFunctionFactory.Create(fulfillmentTool.GetFulfillmentRate),
        AIFunctionFactory.Create(supplyHealthTool.GetSupplyHealthSummary),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };
},
storeOpsDef: storeOpsDef,
storeOpsToolsFactory: sp =>
{
    var storePerformanceTool = sp.GetRequiredService<StorePerformanceTool>();
    var shelfLayoutTool = sp.GetRequiredService<ShelfLayoutTool>();
    var optimizePlanogramTool = sp.GetRequiredService<OptimizePlanogramTool>();
    var predictStockoutTool = sp.GetRequiredService<PredictStockoutTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(storePerformanceTool.GetStorePerformance),
        AIFunctionFactory.Create(shelfLayoutTool.GetShelfLayout),
        AIFunctionFactory.Create(optimizePlanogramTool.OptimizePlanogram),
        AIFunctionFactory.Create(predictStockoutTool.PredictStockout),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };
},
planogramDef: planogramDef,
planogramToolsFactory: sp =>
{
    var shelfLayoutTool = sp.GetRequiredService<ShelfLayoutTool>();
    var optimizePlanogramTool = sp.GetRequiredService<OptimizePlanogramTool>();
    var predictStockoutTool = sp.GetRequiredService<PredictStockoutTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(shelfLayoutTool.GetShelfLayout),
        AIFunctionFactory.Create(optimizePlanogramTool.OptimizePlanogram),
        AIFunctionFactory.Create(predictStockoutTool.PredictStockout),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };
},
marginDef: marginDef,
marginToolsFactory: sp =>
{
    var marginByBrandTool = sp.GetRequiredService<MarginByBrandTool>();
    var marginDriversTool = sp.GetRequiredService<MarginDriversTool>();
    var marginTrendTool = sp.GetRequiredService<MarginTrendTool>();
    var detectMarginRisksTool = sp.GetRequiredService<DetectMarginRisksTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(marginByBrandTool.GetMarginByBrand),
        AIFunctionFactory.Create(marginDriversTool.GetMarginDrivers),
        AIFunctionFactory.Create(marginTrendTool.GetMarginTrend),
        AIFunctionFactory.Create(detectMarginRisksTool.DetectMarginRisks),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };
});

// Register FieldSentimentAgent — dedicated agent with scoped tools (only sentiment + chart)
if (fieldSentimentDef is not null)
{
    builder.Services.AddScoped<FieldSentimentAgent>(sp =>
    {
        var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
        var sentimentTool = sp.GetRequiredService<FieldSentimentTool>();
        var chartTool = sp.GetRequiredService<ChartDataTool>();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(sentimentTool.GetFieldSentiment),
            AIFunctionFactory.Create(chartTool.CreateChart)
        };

        return new FieldSentimentAgent(pipeline, fieldSentimentDef, tools);
    });
    builder.Services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<FieldSentimentAgent>());
}

// Register ConsensusOrchestrator for Portfolio Health Council
if (councilSynthesisDef is not null && councilVoteDef is not null)
{
    builder.Services.AddScoped<RetailPulse.Contracts.Consensus.IConsensusCouncil>(sp =>
    {
        var specialists = sp.GetServices<ISpecialistAgent>();
        var chatClient = sp.GetRequiredService<IChatClient>();
        var logger = sp.GetRequiredService<ILogger<RetailPulse.Api.Consensus.ConsensusOrchestrator>>();

        return new RetailPulse.Api.Consensus.ConsensusOrchestrator(
            specialists, chatClient, councilSynthesisDef, councilVoteDef, logger);
    });
}

// Register EscalationOrchestrator for L1→L2→L3 escalation
var escalationSynthDef = councilSynthesisDef ?? scorecardSynthesisDef;
if (escalationSynthDef is not null)
{
    builder.Services.AddScoped<RetailPulse.Api.Escalation.EscalationOrchestrator>(sp =>
    {
        var specialists = sp.GetServices<ISpecialistAgent>();
        var chatClient = sp.GetRequiredService<IChatClient>();
        var logger = sp.GetRequiredService<ILogger<RetailPulse.Api.Escalation.EscalationOrchestrator>>();

        return new RetailPulse.Api.Escalation.EscalationOrchestrator(
            specialists, chatClient, escalationSynthDef, logger);
    });
}

// Register ScorecardOrchestrator for portfolio scoring
if (scorecardSynthesisDef is not null)
{
    builder.Services.AddScoped<RetailPulse.Api.Scorecard.ScorecardOrchestrator>(sp =>
    {
        var specialists = sp.GetServices<ISpecialistAgent>();
        var chatClient = sp.GetRequiredService<IChatClient>();
        var logger = sp.GetRequiredService<ILogger<RetailPulse.Api.Scorecard.ScorecardOrchestrator>>();

        return new RetailPulse.Api.Scorecard.ScorecardOrchestrator(
            specialists, chatClient, scorecardSynthesisDef, logger);
    });
}

// Register ExplainabilityService (singleton for cross-request trace storage)
builder.Services.AddSingleton<RetailPulse.Api.Explainability.ExplainabilityService>();

// Keep legacy RetailPulseAgent registration for backward compatibility
builder.Services.AddScoped<RetailPulse.Api.Agents.RetailPulseAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<TelemetryHub>>();
    var depletionTool = sp.GetRequiredService<DepletionStatsTool>();
    var portfolioTool = sp.GetRequiredService<PortfolioDepletionStatsTool>();
    var sentimentTool = sp.GetRequiredService<FieldSentimentTool>();
    var shipmentTool = sp.GetRequiredService<ShipmentStatsTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var variantMixTool = sp.GetRequiredService<VariantMixTool>();
    var logger = sp.GetRequiredService<ILogger<RetailPulse.Api.Agents.RetailPulseAgent>>();

    var tools = new List<AITool>
    {
        AIFunctionFactory.Create(depletionTool.GetDepletionStats),
        AIFunctionFactory.Create(portfolioTool.GetPortfolioDepletionStats),
        AIFunctionFactory.Create(sentimentTool.GetFieldSentiment),
        AIFunctionFactory.Create(shipmentTool.GetShipmentStats),
        AIFunctionFactory.Create(variantMixTool.GetVariantMix),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };

    if (foundryEnabled)
    {
        var foundryAgent = sp.GetRequiredService<FoundryShipmentAgent>();
        tools.Add(AIFunctionFactory.Create(foundryAgent.AnalyzeShipments));
    }
    else
    {
        var localAnalyzer = sp.GetRequiredService<LocalShipmentAnalyzer>();
        tools.Add(AIFunctionFactory.Create(localAnalyzer.AnalyzeShipments));
    }

    var configuration = sp.GetRequiredService<IConfiguration>();

    return new RetailPulse.Api.Agents.RetailPulseAgent(chatClient, agentDef, hubContext, tools, logger, configuration);
});

builder.Services.AddOpenApi();

var app = builder.Build();

// Seed RAG knowledge base with sample documents (idempotent)
{
    var kb = app.Services.GetRequiredService<InMemoryKnowledgeBase>();
    var seedLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("KnowledgeBaseSeeder");
    await KnowledgeBaseSeeder.SeedAsync(kb, seedLogger);
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

app.Run();
