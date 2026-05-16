using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents;
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
using RetailPulse.Api.Health;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag;
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
string tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml");
var tenantProvider = new FileTenantProvider(tenantConfigPath);
builder.Services.AddSingleton<ITenantProvider>(tenantProvider);

// Add our custom ActivitySource to the OTel pipeline
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("RetailPulse.Agent").AddSource("RetailPulse.Alerts"))
    .WithMetrics(metrics => metrics.AddMeter(RetailPulseMetrics.MeterName));

// SignalR for real-time telemetry
builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

// ── CORS — split policies for Development vs Production ─────────────────
// Development CORS is restricted to known local frontend origins (Vite dev server on 5173,
// alternative dev port 5100). Allowing any origin in dev would let a malicious local site
// hit the API on behalf of the developer. Production origins come from configuration.
string[] corsDevOrigins =
[
    "http://localhost:5173", "https://localhost:5173",
    "http://localhost:5100", "https://localhost:5100"
];
string[] corsProdOrigins = builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>()
    ?? [];

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
                .WithHeaders("Content-Type", "Authorization", "X-Requested-With")
                .AllowCredentials();
        }
        // If no origins configured, policy allows nothing (deny by default)
    });
});

// ── Authentication & Authorization ──────────────────────────────────────
bool requireAuth = builder.Configuration.GetValue("Security:RequireAuth",
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
string promptsPath = Path.Combine(builder.Environment.ContentRootPath, "prompts.yaml");
PromptConfiguration promptConfig = RetailPulseAgent.LoadPrompts(promptsPath);
AgentDefinition agentDef = promptConfig.Agents["retail-pulse"];

TenantConfiguration tenant = tenantProvider.GetTenant();
var promptEngine = new RetailPulse.Api.Prompts.PromptTemplateEngine(tenant);

// Hydrate all agent definitions with tenant placeholders
promptEngine.Hydrate(agentDef);

AgentDefinition demandForecastDef = promptConfig.Agents["demand-forecast"];
promptEngine.Hydrate(demandForecastDef);

// Router prompt doesn't need tenant placeholders (intent classification is domain-generic)
AgentDefinition routerDef = promptConfig.Agents["router"];

AgentDefinition? promoPlanningDef = promptConfig.Agents.TryGetValue("promo-planning", out AgentDefinition? promoDef) ? promoDef : null;
if (promoPlanningDef != null) promptEngine.Hydrate(promoPlanningDef);

AgentDefinition? competitiveIntelDef = promptConfig.Agents.TryGetValue("competitive-intel", out AgentDefinition? compDef) ? compDef : null;
if (competitiveIntelDef != null) promptEngine.Hydrate(competitiveIntelDef);

AgentDefinition? supplyChainDef = promptConfig.Agents.TryGetValue("supply-chain", out AgentDefinition? scDef) ? scDef : null;
if (supplyChainDef != null) promptEngine.Hydrate(supplyChainDef);

// Load council synthesis and vote prompt definitions
AgentDefinition? councilSynthesisDef = promptConfig.Agents.TryGetValue("council-synthesis", out AgentDefinition? synthDef) ? synthDef : null;
AgentDefinition? councilVoteDef = promptConfig.Agents.TryGetValue("council-vote", out AgentDefinition? vDef) ? vDef : null;

AgentDefinition? storeOpsDef = promptConfig.Agents.TryGetValue("store-ops", out AgentDefinition? soDef) ? soDef : null;
if (storeOpsDef != null) promptEngine.Hydrate(storeOpsDef);

AgentDefinition? planogramDef = promptConfig.Agents.TryGetValue("planogram", out AgentDefinition? pgDef) ? pgDef : null;
if (planogramDef != null) promptEngine.Hydrate(planogramDef);

AgentDefinition? marginDef = promptConfig.Agents.TryGetValue("margin", out AgentDefinition? mrgDef) ? mrgDef : null;
if (marginDef != null) promptEngine.Hydrate(marginDef);

// Load scorecard and exec-brief synthesis definitions
AgentDefinition? scorecardSynthesisDef = promptConfig.Agents.TryGetValue("scorecard-synthesis", out AgentDefinition? scSynthDef) ? scSynthDef : null;
AgentDefinition? execBriefDef = promptConfig.Agents.TryGetValue("exec-brief", out AgentDefinition? ebDef) ? ebDef : null;

// Load field-sentiment agent definition
AgentDefinition? fieldSentimentDef = promptConfig.Agents.TryGetValue("field-sentiment", out AgentDefinition? fsDef) ? fsDef : null;
if (fieldSentimentDef != null) promptEngine.Hydrate(fieldSentimentDef);

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
string approvalDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "approvals.db");
builder.Services.AddSingleton<IApprovalGate>(sp =>
    new SqliteApprovalGate(approvalDbPath, sp.GetRequiredService<ILogger<SqliteApprovalGate>>()));

// Approval tool — available to specialist agents for high-impact recommendations
builder.Services.AddScoped(sp =>
    new ApprovalTool(
        sp.GetRequiredService<IApprovalGate>(),
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<ILogger<ApprovalTool>>()));

// Conversation memory — SQLite-backed, per-user, with configurable TTL
// Conversation memory — SQLite-backed with bounded-channel background extraction
string memoryDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "memory.db");
builder.Services.AddConversationMemory(memoryDbPath);

// Proactive alerts — background anomaly detection with SQLite persistence
string alertsDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "alerts.db");
builder.Services.AddProactiveAlerts(alertsDbPath);

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

// RAG Knowledge Base — in-memory BM25-based document store (no Azure dependency)
builder.Services.AddSingleton<InMemoryKnowledgeBase>();
builder.Services.AddSingleton<IKnowledgeBase>(sp => sp.GetRequiredService<InMemoryKnowledgeBase>());
builder.Services.AddSingleton<RagContextProvider>();

// Response cache — in-memory with TTL expiration and LRU eviction
builder.Services.AddSingleton<InMemoryResponseCache>();
builder.Services.AddSingleton<IResponseCache>(sp => sp.GetRequiredService<InMemoryResponseCache>());

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
builder.Services.AddSingleton(sp =>
    new InMemoryAdaptiveCardState(
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<ILogger<InMemoryAdaptiveCardState>>()));
builder.Services.AddSingleton<IAdaptiveCardState>(sp => sp.GetRequiredService<InMemoryAdaptiveCardState>());

// Observability Suite — cost tracking, audit log, conversation export
builder.Services.AddSingleton<InMemoryCostTracker>();
builder.Services.AddSingleton<ICostTracker>(sp => sp.GetRequiredService<InMemoryCostTracker>());
string auditDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "audit.db");
builder.Services.AddSingleton(_ => new DurableAuditLog(auditDbPath));
builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<DurableAuditLog>());
builder.Services.AddSingleton<ConversationExporter>();
builder.Services.AddSingleton<IConversationExport>(sp => sp.GetRequiredService<ConversationExporter>());

// Register IChatClient — Azure OpenAI via APIM AI Gateway.
// In Production we fail fast if the API key is missing rather than silently
// using "demo-key" which would surface as opaque 401s at runtime.
string openAiEndpoint = builder.Configuration["OpenAI:Endpoint"]
    ?? (builder.Environment.IsDevelopment()
        ? "https://bsapim-dev-northcentralus-001.azure-api.net/inference"
        : null)
    ?? throw new InvalidOperationException(
        "Configuration value 'OpenAI:Endpoint' is required outside of Development.");

string? openAiApiKey = builder.Configuration["OpenAI:ApiKey"];
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = builder.Environment.IsDevelopment()
        ? "demo-key"
        : throw new InvalidOperationException(
            "Configuration value 'OpenAI:ApiKey' is required outside of Development.");
}

// NetworkTimeout caps a single HTTP attempt (one LLM roundtrip) to the AI Gateway.
// 30s is generous for a single LLM call — if APIM can't return in 30s, retrying
// won't help and the user shouldn't wait longer. The 60s request-level timeout
// in /api/chat provides the overall ceiling.
// RetryPolicy disabled: retrying a timed-out LLM call just doubles user wait time.
// The request-level timeout will catch transient failures; let the user retry manually.
var azureClientOptions = new Azure.AI.OpenAI.AzureOpenAIClientOptions
{
    NetworkTimeout = TimeSpan.FromSeconds(30),
    RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0)
};

var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
    new Uri(openAiEndpoint),
    new System.ClientModel.ApiKeyCredential(openAiApiKey),
    azureClientOptions);

builder.Services.AddChatClient(
    azureClient.GetChatClient(agentDef.Model).AsIChatClient())
    .UseFunctionInvocation(configure: client =>
        // Cap tool-call iterations to prevent the model from looping tool calls.
        // 1 iteration keeps latency predictable: the model calls tools once and
        // synthesizes results. A second iteration risks hitting the 60s request
        // timeout on slow APIM calls and doubles worst-case latency.
        client.MaximumIterationsPerRequest = 1)
    // EnableSensitiveData logs full prompts, responses, and tool arguments as span
    // attributes — these can contain user PII. Default to OFF in every environment
    // (including Development) and only enable when an operator explicitly opts in via
    // the Telemetry:EnableSensitiveData config flag for short, deliberate debugging.
    .UseOpenTelemetry(configure: c =>
        c.EnableSensitiveData = builder.Configuration.GetValue("Telemetry:EnableSensitiveData", false));

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

// Register the multi-agent routing pipeline
builder.Services.AddAgentRouting(promptConfig, agentDef, foundryEnabled, sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    DepletionStatsTool depletionTool = sp.GetRequiredService<DepletionStatsTool>();
    PortfolioDepletionStatsTool portfolioTool = sp.GetRequiredService<PortfolioDepletionStatsTool>();
    FieldSentimentTool sentimentTool = sp.GetRequiredService<FieldSentimentTool>();
    ShipmentStatsTool shipmentTool = sp.GetRequiredService<ShipmentStatsTool>();
    ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
    VariantMixTool variantMixTool = sp.GetRequiredService<VariantMixTool>();

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
        FoundryShipmentAgent foundryAgent = sp.GetRequiredService<FoundryShipmentAgent>();
        tools.Add(AIFunctionFactory.Create(foundryAgent.AnalyzeShipments));
    }
    else
    {
        LocalShipmentAnalyzer localAnalyzer = sp.GetRequiredService<LocalShipmentAnalyzer>();
        tools.Add(AIFunctionFactory.Create(localAnalyzer.AnalyzeShipments));
    }

    return tools;
},
demandForecastDef: demandForecastDef,
demandToolsFactory: sp =>
{
#pragma warning disable CS0618 // Obsolete demand tool proxies still used in legacy agent pipeline
    HistoricalDemandTool historicalDemandTool = sp.GetRequiredService<HistoricalDemandTool>();
    ForecastTool forecastTool = sp.GetRequiredService<ForecastTool>();
    SeasonalityFactorsTool seasonalityTool = sp.GetRequiredService<SeasonalityFactorsTool>();
    DemandRisksTool demandRisksTool = sp.GetRequiredService<DemandRisksTool>();
#pragma warning restore CS0618
    ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
    ApprovalTool approvalTool = sp.GetRequiredService<ApprovalTool>();
    CachingToolWrapper cachingWrapper = sp.GetRequiredService<CachingToolWrapper>();

    return cachingWrapper.WrapAll(
    [
        AIFunctionFactory.Create(historicalDemandTool.GetHistoricalDemand),
        AIFunctionFactory.Create(forecastTool.GenerateForecast),
        AIFunctionFactory.Create(seasonalityTool.GetSeasonalityFactors),
        AIFunctionFactory.Create(demandRisksTool.IdentifyDemandRisks),
        AIFunctionFactory.Create(chartTool.CreateChart),
        AIFunctionFactory.Create(approvalTool.RequestApproval)
    ]);
},
promoPlanningDef: promoPlanningDef,
promoToolsFactory: sp =>
{
    PromoHistoryTool promoHistoryTool = sp.GetRequiredService<PromoHistoryTool>();
    CalculateLiftTool calculateLiftTool = sp.GetRequiredService<CalculateLiftTool>();
    EvaluateTimingTool evaluateTimingTool = sp.GetRequiredService<EvaluateTimingTool>();
    EstimateROITool estimateROITool = sp.GetRequiredService<EstimateROITool>();
    ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
    ApprovalTool approvalTool = sp.GetRequiredService<ApprovalTool>();
    CachingToolWrapper cachingWrapper = sp.GetRequiredService<CachingToolWrapper>();

    return cachingWrapper.WrapAll(
    [
        AIFunctionFactory.Create(promoHistoryTool.GetPromoHistory),
        AIFunctionFactory.Create(calculateLiftTool.CalculateLift),
        AIFunctionFactory.Create(evaluateTimingTool.EvaluateTiming),
        AIFunctionFactory.Create(estimateROITool.EstimateROI),
        AIFunctionFactory.Create(chartTool.CreateChart),
        AIFunctionFactory.Create(approvalTool.RequestApproval)
    ]);
},
competitiveIntelDef: competitiveIntelDef,
competitiveToolsFactory: sp =>
{
    CompetitorPricingTool competitorPricingTool = sp.GetRequiredService<CompetitorPricingTool>();
    MarketShareTool marketShareTool = sp.GetRequiredService<MarketShareTool>();
    DetectThreatsTool detectThreatsTool = sp.GetRequiredService<DetectThreatsTool>();
    CompetitiveLandscapeTool competitiveLandscapeTool = sp.GetRequiredService<CompetitiveLandscapeTool>();
    ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
    CachingToolWrapper cachingWrapper = sp.GetRequiredService<CachingToolWrapper>();

    return cachingWrapper.WrapAll(
    [
        AIFunctionFactory.Create(competitorPricingTool.GetCompetitorPricing),
        AIFunctionFactory.Create(marketShareTool.GetMarketShare),
        AIFunctionFactory.Create(detectThreatsTool.DetectThreats),
        AIFunctionFactory.Create(competitiveLandscapeTool.GetCompetitiveLandscape),
        AIFunctionFactory.Create(chartTool.CreateChart)
    ]);
},
supplyChainDef: supplyChainDef,
supplyToolsFactory: sp =>
{
    InventoryLevelsTool inventoryTool = sp.GetRequiredService<InventoryLevelsTool>();
    SupplyDisruptionsTool disruptionsTool = sp.GetRequiredService<SupplyDisruptionsTool>();
    FulfillmentRateTool fulfillmentTool = sp.GetRequiredService<FulfillmentRateTool>();
    SupplyHealthTool supplyHealthTool = sp.GetRequiredService<SupplyHealthTool>();
    ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
    CachingToolWrapper cachingWrapper = sp.GetRequiredService<CachingToolWrapper>();

    return cachingWrapper.WrapAll(
    [
        AIFunctionFactory.Create(inventoryTool.GetInventoryLevels),
        AIFunctionFactory.Create(disruptionsTool.GetSupplyDisruptions),
        AIFunctionFactory.Create(fulfillmentTool.GetFulfillmentRate),
        AIFunctionFactory.Create(supplyHealthTool.GetSupplyHealthSummary),
        AIFunctionFactory.Create(chartTool.CreateChart)
    ]);
},
storeOpsDef: storeOpsDef,
storeOpsToolsFactory: sp =>
{
    StorePerformanceTool storePerformanceTool = sp.GetRequiredService<StorePerformanceTool>();
    ShelfLayoutTool shelfLayoutTool = sp.GetRequiredService<ShelfLayoutTool>();
    OptimizePlanogramTool optimizePlanogramTool = sp.GetRequiredService<OptimizePlanogramTool>();
    PredictStockoutTool predictStockoutTool = sp.GetRequiredService<PredictStockoutTool>();
    ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
    CachingToolWrapper cachingWrapper = sp.GetRequiredService<CachingToolWrapper>();

    return cachingWrapper.WrapAll(
    [
        AIFunctionFactory.Create(storePerformanceTool.GetStorePerformance),
        AIFunctionFactory.Create(shelfLayoutTool.GetShelfLayout),
        AIFunctionFactory.Create(optimizePlanogramTool.OptimizePlanogram),
        AIFunctionFactory.Create(predictStockoutTool.PredictStockout),
        AIFunctionFactory.Create(chartTool.CreateChart)
    ]);
},
planogramDef: planogramDef,
planogramToolsFactory: sp =>
{
    ShelfLayoutTool shelfLayoutTool = sp.GetRequiredService<ShelfLayoutTool>();
    OptimizePlanogramTool optimizePlanogramTool = sp.GetRequiredService<OptimizePlanogramTool>();
    PredictStockoutTool predictStockoutTool = sp.GetRequiredService<PredictStockoutTool>();
    ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
    CachingToolWrapper cachingWrapper = sp.GetRequiredService<CachingToolWrapper>();

    return cachingWrapper.WrapAll(
    [
        AIFunctionFactory.Create(shelfLayoutTool.GetShelfLayout),
        AIFunctionFactory.Create(optimizePlanogramTool.OptimizePlanogram),
        AIFunctionFactory.Create(predictStockoutTool.PredictStockout),
        AIFunctionFactory.Create(chartTool.CreateChart)
    ]);
},
marginDef: marginDef,
marginToolsFactory: sp =>
{
    MarginByBrandTool marginByBrandTool = sp.GetRequiredService<MarginByBrandTool>();
    MarginDriversTool marginDriversTool = sp.GetRequiredService<MarginDriversTool>();
    MarginTrendTool marginTrendTool = sp.GetRequiredService<MarginTrendTool>();
    DetectMarginRisksTool detectMarginRisksTool = sp.GetRequiredService<DetectMarginRisksTool>();
    ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
    CachingToolWrapper cachingWrapper = sp.GetRequiredService<CachingToolWrapper>();

    return cachingWrapper.WrapAll(
    [
        AIFunctionFactory.Create(marginByBrandTool.GetMarginByBrand),
        AIFunctionFactory.Create(marginDriversTool.GetMarginDrivers),
        AIFunctionFactory.Create(marginTrendTool.GetMarginTrend),
        AIFunctionFactory.Create(detectMarginRisksTool.DetectMarginRisks),
        AIFunctionFactory.Create(chartTool.CreateChart)
    ]);
});

// Register FieldSentimentAgent — dedicated agent with scoped tools (only sentiment + chart)
if (fieldSentimentDef is not null)
{
    builder.Services.AddScoped(sp =>
    {
        IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
        FieldSentimentTool sentimentTool = sp.GetRequiredService<FieldSentimentTool>();
        ChartDataTool chartTool = sp.GetRequiredService<ChartDataTool>();
        CachingToolWrapper cachingWrapper = sp.GetRequiredService<CachingToolWrapper>();

        IList<AITool> tools = cachingWrapper.WrapAll(
        [
            AIFunctionFactory.Create(sentimentTool.GetFieldSentiment),
            AIFunctionFactory.Create(chartTool.CreateChart)
        ]);

        return new FieldSentimentAgent(pipeline, fieldSentimentDef, tools);
    });
    builder.Services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<FieldSentimentAgent>());
}

// Register ConsensusOrchestrator for Portfolio Health Council
if (councilSynthesisDef is not null && councilVoteDef is not null)
{
    builder.Services.AddScoped<RetailPulse.Contracts.Consensus.IConsensusCouncil>(sp =>
    {
        IEnumerable<ISpecialistAgent> specialists = sp.GetServices<ISpecialistAgent>();
        IChatClient chatClient = sp.GetRequiredService<IChatClient>();
        ILogger<ConsensusOrchestrator> logger = sp.GetRequiredService<ILogger<ConsensusOrchestrator>>();

        return new ConsensusOrchestrator(
            specialists, chatClient, councilSynthesisDef, councilVoteDef, logger);
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

        return new ScorecardOrchestrator(
            specialists, chatClient, scorecardSynthesisDef, logger);
    });
}

// Register ExplainabilityService (singleton for cross-request trace storage)
builder.Services.AddSingleton<RetailPulse.Api.Explainability.ExplainabilityService>();

builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// Seed RAG knowledge base with sample documents (idempotent)
{
    InMemoryKnowledgeBase kb = app.Services.GetRequiredService<InMemoryKnowledgeBase>();
    ILogger seedLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("KnowledgeBaseSeeder");
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
app.MapMemoryEndpoints();
app.MapCacheEndpoints();

app.Run();
