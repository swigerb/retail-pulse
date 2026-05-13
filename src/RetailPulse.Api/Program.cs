using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
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
using RetailPulse.Api.Observability;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults (OTel, health checks, service discovery)
builder.AddServiceDefaults();

// Load tenant configuration
var tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml");
var tenantProvider = new FileTenantProvider(tenantConfigPath);
builder.Services.AddSingleton<ITenantProvider>(tenantProvider);

// Add our custom ActivitySource to the OTel pipeline
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("RetailPulse.Agent").AddSource("RetailPulse.Alerts"));

// SignalR for real-time telemetry
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// CORS for React frontend — origins are configurable via Cors:AllowedOrigins
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "https://localhost:5173" };
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Load prompts from YAML and resolve tenant placeholders
var promptsPath = Path.Combine(builder.Environment.ContentRootPath, "prompts.yaml");
var promptConfig = RetailPulse.Api.Agents.RetailPulseAgent.LoadPrompts(promptsPath);
var agentDef = promptConfig.Agents["retail-pulse"];

var tenant = tenantProvider.GetTenant();
agentDef.SystemPrompt = agentDef.SystemPrompt
    .Replace("{tenant.company}", tenant.Company)
    .Replace("{tenant.industry}", tenant.Industry)
    .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
    .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
    .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
    .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
    .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));

// Resolve demand-forecast agent prompt with tenant placeholders
var demandForecastDef = promptConfig.Agents["demand-forecast"];
demandForecastDef.SystemPrompt = demandForecastDef.SystemPrompt
    .Replace("{tenant.company}", tenant.Company)
    .Replace("{tenant.industry}", tenant.Industry)
    .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
    .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
    .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
    .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
    .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));

// Router prompt doesn't need tenant placeholders (intent classification is domain-generic)
var routerDef = promptConfig.Agents["router"];

// Resolve promo-planning agent prompt with tenant placeholders
var promoPlanningDef = promptConfig.Agents.TryGetValue("promo-planning", out var promoDef) ? promoDef : null;
if (promoPlanningDef != null)
{
    promoPlanningDef.SystemPrompt = promoPlanningDef.SystemPrompt
        .Replace("{tenant.company}", tenant.Company)
        .Replace("{tenant.industry}", tenant.Industry)
        .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
        .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
        .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
        .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
        .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));
}

// Resolve competitive-intel agent prompt with tenant placeholders
var competitiveIntelDef = promptConfig.Agents.TryGetValue("competitive-intel", out var compDef) ? compDef : null;
if (competitiveIntelDef != null)
{
    competitiveIntelDef.SystemPrompt = competitiveIntelDef.SystemPrompt
        .Replace("{tenant.company}", tenant.Company)
        .Replace("{tenant.industry}", tenant.Industry)
        .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
        .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
        .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
        .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
        .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));
}

// Resolve supply-chain agent prompt with tenant placeholders
var supplyChainDef = promptConfig.Agents.TryGetValue("supply-chain", out var scDef) ? scDef : null;
if (supplyChainDef != null)
{
    supplyChainDef.SystemPrompt = supplyChainDef.SystemPrompt
        .Replace("{tenant.company}", tenant.Company)
        .Replace("{tenant.industry}", tenant.Industry)
        .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
        .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
        .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
        .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
        .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));
}

// Load council synthesis and vote prompt definitions
var councilSynthesisDef = promptConfig.Agents.TryGetValue("council-synthesis", out var synthDef) ? synthDef : null;
var councilVoteDef = promptConfig.Agents.TryGetValue("council-vote", out var vDef) ? vDef : null;

// Resolve store-ops agent prompt with tenant placeholders
var storeOpsDef = promptConfig.Agents.TryGetValue("store-ops", out var soDef) ? soDef : null;
if (storeOpsDef != null)
{
    storeOpsDef.SystemPrompt = storeOpsDef.SystemPrompt
        .Replace("{tenant.company}", tenant.Company)
        .Replace("{tenant.industry}", tenant.Industry)
        .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
        .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
        .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
        .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
        .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));
}

// Resolve planogram agent prompt with tenant placeholders
var planogramDef = promptConfig.Agents.TryGetValue("planogram", out var pgDef) ? pgDef : null;
if (planogramDef != null)
{
    planogramDef.SystemPrompt = planogramDef.SystemPrompt
        .Replace("{tenant.company}", tenant.Company)
        .Replace("{tenant.industry}", tenant.Industry)
        .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
        .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
        .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
        .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
        .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));
}

// Resolve margin agent prompt with tenant placeholders
var marginDef = promptConfig.Agents.TryGetValue("margin", out var mrgDef) ? mrgDef : null;
if (marginDef != null)
{
    marginDef.SystemPrompt = marginDef.SystemPrompt
        .Replace("{tenant.company}", tenant.Company)
        .Replace("{tenant.industry}", tenant.Industry)
        .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
        .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
        .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
        .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
        .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));
}

// Load scorecard and exec-brief synthesis definitions
var scorecardSynthesisDef = promptConfig.Agents.TryGetValue("scorecard-synthesis", out var scSynthDef) ? scSynthDef : null;
var execBriefDef = promptConfig.Agents.TryGetValue("exec-brief", out var ebDef) ? ebDef : null;

// Register HttpClient for MCP server communication. The default URL is a
// dev convenience — production should always set McpServer:BaseUrl.
var mcpBaseUrl = builder.Configuration["McpServer:BaseUrl"]
    ?? (builder.Environment.IsDevelopment() ? "http://localhost:5200" : null)
    ?? throw new InvalidOperationException(
        "Configuration value 'McpServer:BaseUrl' is required outside of Development.");
builder.Services.AddHttpClient("McpServer", client =>
{
    client.BaseAddress = new Uri(mcpBaseUrl);
});

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

// Demand forecasting tools
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
var memoryDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "memory.db");
builder.Services.AddConversationMemory(memoryDbPath);

// Proactive alerts — background anomaly detection with SQLite persistence
var alertsDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "alerts.db");
builder.Services.AddProactiveAlerts(alertsDbPath);

// Distributed tracing — in-memory ring buffer with SignalR push for real-time trace events
builder.Services.AddSingleton<InMemoryTraceCollector>(sp =>
    new InMemoryTraceCollector(
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ITraceCollector>(sp => sp.GetRequiredService<InMemoryTraceCollector>());

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

// ── Card State — thread-safe in-memory with SignalR events ───────────────
builder.Services.AddSingleton<RetailPulse.Api.Cards.InMemoryAdaptiveCardState>();
builder.Services.AddSingleton<RetailPulse.Contracts.Cards.IAdaptiveCardState>(sp =>
    sp.GetRequiredService<RetailPulse.Api.Cards.InMemoryAdaptiveCardState>());

// ── Observability Services — cost tracking, audit log, conversation export ─
builder.Services.AddSingleton<RetailPulse.Api.Observability.InMemoryCostTracker>();
builder.Services.AddSingleton<RetailPulse.Contracts.Observability.ICostTracker>(sp =>
    sp.GetRequiredService<RetailPulse.Api.Observability.InMemoryCostTracker>());

builder.Services.AddSingleton<RetailPulse.Api.Observability.InMemoryAuditLog>();
builder.Services.AddSingleton<RetailPulse.Contracts.Observability.IAuditLog>(sp =>
    sp.GetRequiredService<RetailPulse.Api.Observability.InMemoryAuditLog>());

builder.Services.AddSingleton<RetailPulse.Api.Observability.ConversationExporter>();
builder.Services.AddSingleton<RetailPulse.Contracts.Observability.IConversationExport>(sp =>
    sp.GetRequiredService<RetailPulse.Api.Observability.ConversationExporter>());

// Collaborative Adaptive Cards — in-memory multi-user card state with SignalR sync
builder.Services.AddSingleton<InMemoryAdaptiveCardState>(sp =>
    new InMemoryAdaptiveCardState(
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<ILogger<InMemoryAdaptiveCardState>>()));
builder.Services.AddSingleton<IAdaptiveCardState>(sp => sp.GetRequiredService<InMemoryAdaptiveCardState>());

// Observability Suite — cost tracking, audit log, conversation export
builder.Services.AddSingleton<InMemoryCostTracker>();
builder.Services.AddSingleton<ICostTracker>(sp => sp.GetRequiredService<InMemoryCostTracker>());
builder.Services.AddSingleton<InMemoryAuditLog>();
builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<InMemoryAuditLog>());
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

var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
    new Uri(openAiEndpoint),
    new System.ClientModel.ApiKeyCredential(openAiApiKey));

builder.Services.AddChatClient(
    azureClient.GetChatClient(agentDef.Model).AsIChatClient())
    .UseFunctionInvocation()
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
    var historicalDemandTool = sp.GetRequiredService<HistoricalDemandTool>();
    var forecastTool = sp.GetRequiredService<ForecastTool>();
    var seasonalityTool = sp.GetRequiredService<SeasonalityFactorsTool>();
    var demandRisksTool = sp.GetRequiredService<DemandRisksTool>();
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

app.UseCors();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// SignalR hubs
app.MapHub<TelemetryHub>("/hubs/telemetry");
app.MapHub<StreamingHub>("/hubs/streaming");

// Chat endpoint — routes through guardrails → cache → multi-agent router with memory and tracing
app.MapPost("/api/chat", async (ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, InMemoryTraceCollector traceCollector, GuardrailsMiddleware guardrails, IResponseCache responseCache, ICostTracker costTracker, IAuditLog auditLog, ConversationExporter conversationExporter, ILogger<Program> logger, CancellationToken ct) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Field 'message' is required." });
    }

    try
    {
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var userId = request.User?.ObjectId ?? "anonymous";

        // ── Guardrails: input check ──────────────────────────────────────
        var guardrailResult = await guardrails.CheckInputAsync(request, ct);
        if (guardrailResult.IsBlocked)
        {
            return Results.Ok(new ChatResponse(
                guardrailResult.RefusalMessage!,
                sessionId,
                [],
                null,
                0));
        }

        // ── Cache: check for cached response ─────────────────────────────
        if (CacheHelpers.IsCacheable(request.Message))
        {
            // Tentative agent key for cache — use "general" as prefix until we route
            var cacheKey = CacheHelpers.BuildCacheKey("pre-route", request.Message);
            var cached = await responseCache.GetAsync(cacheKey, ct);
            if (cached is not null)
            {
                logger.LogInformation("Cache hit for session {SessionId}, key {CacheKey}", sessionId, cacheKey[..8]);
                return Results.Ok(new ChatResponse(
                    cached.Response,
                    sessionId,
                    [new AgentSpan("cache.hit", "cache", $"Served from cache (agent: {cached.AgentId})", 0, DateTimeOffset.UtcNow, sessionId)],
                    null,
                    0));
            }
        }

        // Start root trace span: chat_request
        using var chatActivity = AgentTelemetry.StartChatRequest(sessionId, request.Message);
        var traceId = chatActivity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var traceStartTime = DateTimeOffset.UtcNow;

        // Memory recall with tracing
        string? memoryContext = null;
        using (var memoryRecallActivity = AgentTelemetry.StartMemoryRecall(userId))
        {
            var memoryStart = DateTimeOffset.UtcNow;
            memoryContext = await memoryMiddleware.BuildMemoryContextAsync(userId, request.Message, ct);
            var memoryEnd = DateTimeOffset.UtcNow;
            var memoryDurationMs = (memoryEnd - memoryStart).TotalMilliseconds;

            memoryRecallActivity?.SetTag("memory.entries_recalled", memoryContext is not null ? "context_found" : "none");

            traceCollector.CaptureSpan(new TraceSpan(
                SpanId: Guid.NewGuid().ToString("N")[..16],
                TraceId: traceId,
                ParentSpanId: chatActivity?.SpanId.ToString(),
                OperationName: "memory.recall",
                StartTime: memoryStart,
                EndTime: memoryEnd,
                DurationMs: memoryDurationMs,
                Tags: new Dictionary<string, string>
                {
                    ["memory.user_id"] = userId,
                    ["memory.entries_recalled"] = memoryContext is not null ? "context_found" : "none"
                }));
        }

        var enrichedRequest = request;
        if (memoryContext is not null)
        {
            var historyWithMemory = new List<ChatHistoryMessage>
            {
                new("system", memoryContext)
            };
            if (request.History is { Count: > 0 })
                historyWithMemory.AddRange(request.History);

            enrichedRequest = request with { History = historyWithMemory };
        }

        // RAG context injection — search knowledge base for relevant grounding
        var ragProvider = app.Services.GetRequiredService<RagContextProvider>();
        var ragContext = await ragProvider.GetContextAsync(request.Message, ct);
        if (ragContext is not null)
        {
            var historyWithRag = new List<ChatHistoryMessage>(enrichedRequest.History ?? [])
            {
                new("system", ragContext)
            };
            enrichedRequest = enrichedRequest with { History = historyWithRag };
        }

        // Router classification with tracing
        RoutingDecision decision;
        {
            var classifyStart = DateTimeOffset.UtcNow;
            using var classifyActivity = AgentTelemetry.StartRouterClassify(enrichedRequest.Message);

            decision = await router.RouteAsync(
                enrichedRequest.Message,
                enrichedRequest.History,
                enrichedRequest.User,
                tenantId: null,
                ct);

            var classifyEnd = DateTimeOffset.UtcNow;
            classifyActivity?.SetTag("router.intent", decision.Intent);
            classifyActivity?.SetTag("router.confidence", decision.Confidence);

            traceCollector.CaptureSpan(new TraceSpan(
                SpanId: Guid.NewGuid().ToString("N")[..16],
                TraceId: traceId,
                ParentSpanId: chatActivity?.SpanId.ToString(),
                OperationName: "router.classify",
                StartTime: classifyStart,
                EndTime: classifyEnd,
                DurationMs: (classifyEnd - classifyStart).TotalMilliseconds,
                Tags: new Dictionary<string, string>
                {
                    ["router.intent"] = decision.Intent,
                    ["router.confidence"] = decision.Confidence.ToString("F2")
                }));
        }

        // Agent selection with tracing
        ISpecialistAgent? specialist;
        {
            var selectStart = DateTimeOffset.UtcNow;
            using var selectActivity = AgentTelemetry.StartRouterSelectAgent();

            specialist = specialists.FirstOrDefault(s =>
                string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase));

            if (specialist is null)
            {
                logger.LogWarning("No specialist found for key '{AgentKey}' — using General agent", decision.AgentKey);
                specialist = specialists.First(s => s.Key == "general");
            }

            var selectEnd = DateTimeOffset.UtcNow;
            selectActivity?.SetTag("router.selected_agent", specialist.Key);
            selectActivity?.SetTag("router.selected_agent_name", specialist.DisplayName);

            traceCollector.CaptureSpan(new TraceSpan(
                SpanId: Guid.NewGuid().ToString("N")[..16],
                TraceId: traceId,
                ParentSpanId: chatActivity?.SpanId.ToString(),
                OperationName: "router.select_agent",
                StartTime: selectStart,
                EndTime: selectEnd,
                DurationMs: (selectEnd - selectStart).TotalMilliseconds,
                Tags: new Dictionary<string, string>
                {
                    ["router.selected_agent"] = specialist.Key,
                    ["router.selected_agent_name"] = specialist.DisplayName
                }));
        }

        logger.LogInformation(
            "Routing to {AgentKey} ({DisplayName}) — intent: {Intent}, confidence: {Confidence:F2}, traceId: {TraceId}",
            specialist.Key, specialist.DisplayName, decision.Intent, decision.Confidence, traceId);

        // Council interception: if the router classified as council/health, convene the council
        if (decision.DetectedIntents?.Any(i => string.Equals(i, "council/health", StringComparison.OrdinalIgnoreCase)) == true
            || string.Equals(decision.Intent, "council/health", StringComparison.OrdinalIgnoreCase))
        {
            var council = app.Services.GetService<RetailPulse.Contracts.Consensus.IConsensusCouncil>();
            if (council is not null)
            {
                // Extract brand from the message (best-effort: first capitalized word or fallback)
                var brand = ExtractBrand(enrichedRequest.Message, tenant);
                var verdict = await council.ConveneAsync(brand, null, ct);

                var councilReply = $"## Portfolio Health Council — {verdict.Brand}\n\n" +
                    $"**Overall Rating: {verdict.OverallRating}** {(verdict.IsUnanimous ? "(unanimous)" : "(split decision)")}\n\n" +
                    $"{verdict.Synthesis}\n\n" +
                    (verdict.ActionItems.Length > 0
                        ? "### Action Items\n" + string.Join("\n", verdict.ActionItems.Select(a => $"- {a}")) + "\n\n"
                        : "") +
                    (verdict.Disagreements.Length > 0
                        ? "### Disagreements\n" + string.Join("\n", verdict.Disagreements.Select(d => $"- {d}")) + "\n\n"
                        : "") +
                    "### Agent Votes\n" +
                    string.Join("\n", verdict.Votes.Select(v =>
                        $"- **{v.AgentName}**: {v.Rating} (confidence: {v.Confidence:F2}) — {v.Reasoning}"));

                var councilResponse = new ChatResponse(
                    councilReply,
                    enrichedRequest.SessionId ?? sessionId,
                    [],
                    null,
                    (long)verdict.TotalDuration.TotalMilliseconds);

                await memoryMiddleware.ExtractAndStoreAsync(userId, enrichedRequest.Message, councilReply, ct);

                // Auto-create a Voting card from the council verdict
                var cardState = app.Services.GetRequiredService<IAdaptiveCardState>();
                var cardData = new Dictionary<string, object>
                {
                    ["brand"] = verdict.Brand,
                    ["overallRating"] = verdict.OverallRating.ToString(),
                    ["synthesis"] = verdict.Synthesis,
                    ["isUnanimous"] = verdict.IsUnanimous
                };
                var votingCard = await cardState.CreateAsync(
                    new CreateCardRequest(
                        $"Health Assessment: {verdict.Brand}",
                        CardType.Voting,
                        userId,
                        cardData), ct);

                // Seed initial votes from council agent votes
                foreach (var vote in verdict.Votes)
                {
                    await cardState.ActionAsync(votingCard.Id, new CardAction(
                        vote.AgentId, vote.AgentName, CardActionType.Vote,
                        new Dictionary<string, string> { ["vote"] = vote.Rating.ToString() }), ct);
                }

                return Results.Ok(councilResponse);
            }
        }

        // Agent execution with tracing
        ChatResponse response;
        {
            var agentStart = DateTimeOffset.UtcNow;
            using var agentActivity = AgentTelemetry.StartAgentProcess(specialist.Key);

            response = await specialist.HandleAsync(enrichedRequest, ct);

            var agentEnd = DateTimeOffset.UtcNow;
            var toolsCalledCount = response.Spans?.Count(s => s.Type == "tool_call") ?? 0;
            var inputTokens = response.TokenUsage?.InputTokens ?? 0;
            var outputTokens = response.TokenUsage?.OutputTokens ?? 0;

            agentActivity?.SetTag("agent.name", specialist.Key);
            agentActivity?.SetTag("agent.tools_called_count", toolsCalledCount);
            agentActivity?.SetTag("agent.token_input", inputTokens);
            agentActivity?.SetTag("agent.token_output", outputTokens);

            traceCollector.CaptureSpan(new TraceSpan(
                SpanId: Guid.NewGuid().ToString("N")[..16],
                TraceId: traceId,
                ParentSpanId: chatActivity?.SpanId.ToString(),
                OperationName: $"agent.{specialist.Key}.process",
                StartTime: agentStart,
                EndTime: agentEnd,
                DurationMs: (agentEnd - agentStart).TotalMilliseconds,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                Tags: new Dictionary<string, string>
                {
                    ["agent.name"] = specialist.Key,
                    ["agent.tools_called_count"] = toolsCalledCount.ToString(),
                    ["agent.token_input"] = inputTokens.ToString(),
                    ["agent.token_output"] = outputTokens.ToString()
                }));
        }

        // Record individual tool spans from agent response
        if (response.Spans is { Count: > 0 })
        {
            foreach (var span in response.Spans.Where(s => s.Type is "tool_call" or "tool_result"))
            {
                traceCollector.CaptureSpan(new TraceSpan(
                    SpanId: Guid.NewGuid().ToString("N")[..16],
                    TraceId: traceId,
                    ParentSpanId: chatActivity?.SpanId.ToString(),
                    OperationName: $"tool.{span.Name}",
                    StartTime: span.Timestamp,
                    EndTime: span.Timestamp.AddMilliseconds(span.DurationMs),
                    DurationMs: span.DurationMs,
                    Tags: new Dictionary<string, string>
                    {
                        ["tool.name"] = span.Name,
                        ["tool.duration_ms"] = span.DurationMs.ToString("F0"),
                        ["tool.result_size"] = span.Detail?.Length > 0 ? $"{span.Detail.Length} chars" : ""
                    }));
            }
        }

        // Memory store with tracing (fire-and-forget)
        if (decision.Intent != AgentIntent.MemoryManagement)
        {
            var capturedTraceId = traceId;
            var capturedParentSpanId = chatActivity?.SpanId.ToString();
            _ = Task.Run(async () =>
            {
                try
                {
                    var storeStart = DateTimeOffset.UtcNow;
                    using var memoryStoreActivity = AgentTelemetry.StartMemoryStore(userId);
                    await memoryMiddleware.ExtractAndStoreAsync(userId, request.Message, response.Reply, CancellationToken.None);
                    var storeEnd = DateTimeOffset.UtcNow;

                    traceCollector.CaptureSpan(new TraceSpan(
                        SpanId: Guid.NewGuid().ToString("N")[..16],
                        TraceId: capturedTraceId,
                        ParentSpanId: capturedParentSpanId,
                        OperationName: "memory.store",
                        StartTime: storeStart,
                        EndTime: storeEnd,
                        DurationMs: (storeEnd - storeStart).TotalMilliseconds,
                        Tags: new Dictionary<string, string>
                        {
                            ["memory.user_id"] = userId,
                            ["memory.entries_stored"] = "extracted"
                        }));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background memory extraction failed for user {UserId}", userId);
                }
            }, CancellationToken.None);
        }

        // Notify trace completion via SignalR
        _ = traceCollector.NotifyTraceCompletedAsync(traceId);

        // ── Observability: cost tracking + audit log ─────────────────────
        {
            var inputTokens = response.TokenUsage?.InputTokens ?? 0;
            var outputTokens = response.TokenUsage?.OutputTokens ?? 0;
            var agentDuration = response.TotalDurationMs.HasValue
                ? TimeSpan.FromMilliseconds(response.TotalDurationMs.Value)
                : TimeSpan.Zero;

            await costTracker.TrackUsageAsync(new UsageEvent(
                specialist.Key, "gpt-4o", inputTokens, outputTokens,
                response.Spans?.FirstOrDefault(s => s.Type == "tool_call")?.Name,
                DateTime.UtcNow), ct);

            await auditLog.LogAsync(new AuditEntry(
                $"{sessionId}-{Guid.NewGuid():N}"[..32],
                DateTime.UtcNow, userId, specialist.Key,
                $"chat.{decision.Intent}",
                request.Message[..Math.Min(200, request.Message.Length)],
                response.Reply[..Math.Min(200, response.Reply.Length)],
                inputTokens + outputTokens,
                agentDuration), ct);

            // Track messages in conversation exporter for session export
            var toolCalls = response.Spans?
                .Where(s => s.Type == "tool_call")
                .Select(s => s.Name)
                .ToList();

            await conversationExporter.TrackMessageAsync(sessionId, new TrackedMessage
            {
                Role = "user",
                Content = request.Message
            }, ct);

            await conversationExporter.TrackMessageAsync(sessionId, new TrackedMessage
            {
                Role = "assistant",
                Content = response.Reply,
                AgentId = specialist.Key,
                ToolCalls = toolCalls,
                DurationMs = response.TotalDurationMs.HasValue ? (double)response.TotalDurationMs.Value : null
            }, ct);
        }

        // ── Guardrails: output PII redaction ─────────────────────────────
        var filteredReply = await guardrails.FilterOutputAsync(response.Reply, userId, ct);
        if (filteredReply != response.Reply)
        {
            response = response with { Reply = filteredReply };
        }

        // ── Cache: store response for deterministic queries ──────────────
        if (CacheHelpers.IsCacheable(request.Message))
        {
            var cacheKey = CacheHelpers.BuildCacheKey("pre-route", request.Message);
            await responseCache.SetAsync(cacheKey,
                new CachedResponse(response.Reply, specialist.Key, DateTime.UtcNow, cacheKey),
                TimeSpan.FromMinutes(5), ct);
        }

        chatActivity?.SetTag("response.length", response.Reply.Length);
        chatActivity?.SetTag("trace.id", traceId);

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled error from chat agent for session {SessionId}", request.SessionId);

        return Results.Json(
            new
            {
                error = "The AI service is temporarily unavailable. Please try again shortly.",
                code = "service_unavailable"
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("Chat");

// Streaming chat endpoint — SSE/SignalR progressive token delivery
app.MapPost("/api/chat/stream", async (ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, GuardrailsMiddleware guardrails, StreamingMiddleware streaming, ILogger<Program> logger, CancellationToken ct) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Field 'message' is required." });
    }

    try
    {
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var userId = request.User?.ObjectId ?? "anonymous";

        // Guardrails input check
        var guardrailResult = await guardrails.CheckInputAsync(request, ct);
        if (guardrailResult.IsBlocked)
        {
            return Results.Ok(new ChatResponse(
                guardrailResult.RefusalMessage!,
                sessionId,
                [],
                null,
                0));
        }

        // Memory recall
        var memoryContext = await memoryMiddleware.BuildMemoryContextAsync(userId, request.Message, ct);
        var enrichedRequest = request;
        if (memoryContext is not null)
        {
            var historyWithMemory = new List<ChatHistoryMessage> { new("system", memoryContext) };
            if (request.History is { Count: > 0 })
                historyWithMemory.AddRange(request.History);
            enrichedRequest = request with { History = historyWithMemory };
        }

        // Route to specialist
        var decision = await router.RouteAsync(enrichedRequest.Message, enrichedRequest.History, enrichedRequest.User, null, ct);
        var specialist = specialists.FirstOrDefault(s =>
            string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase))
            ?? specialists.First(s => s.Key == "general");

        logger.LogInformation("Streaming route to {AgentKey} — intent: {Intent}", specialist.Key, decision.Intent);

        // Execute agent — stream tokens via SignalR in parallel
        var response = await specialist.HandleAsync(enrichedRequest, ct);

        // Push the full response as streaming tokens via SignalR for clients listening
        await streaming.StreamResponseFallbackAsync(sessionId, specialist.Key, response.Reply, ct);

        // PII redaction on output
        var filteredReply = await guardrails.FilterOutputAsync(response.Reply, userId, ct);
        if (filteredReply != response.Reply)
            response = response with { Reply = filteredReply };

        // Fire-and-forget memory extraction
        _ = Task.Run(async () =>
        {
            try { await memoryMiddleware.ExtractAndStoreAsync(userId, request.Message, response.Reply, CancellationToken.None); }
            catch { /* swallow */ }
        }, CancellationToken.None);

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Streaming chat error for session {SessionId}", request.SessionId);
        return Results.Json(
            new { error = "The AI service is temporarily unavailable.", code = "service_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("ChatStream");

// Health/info endpoint
app.MapGet("/api/info", (IEnumerable<ISpecialistAgent> specialists) => Results.Ok(new
{
    Name = "Retail Pulse API",
    Version = "1.0.0",
    Agent = agentDef.Name,
    Tools = agentDef.Tools,
    Router = "RetailOpsRouter",
    Specialists = specialists.Select(s => new { s.Key, s.DisplayName }).ToList()
}))
.WithName("Info");

// ── Alert endpoints ──────────────────────────────────────────────────────

app.MapGet("/api/alerts/active", async (IAlertService alertService, CancellationToken ct) =>
{
    var alerts = await alertService.GetActiveAlertsAsync(ct);
    return Results.Ok(alerts);
})
.WithName("GetActiveAlerts");

app.MapGet("/api/alerts/history", async (IAlertService alertService, HttpContext http, CancellationToken ct) =>
{
    var userId = http.Request.Query["userId"].FirstOrDefault() ?? "default";
    var limitStr = http.Request.Query["limit"].FirstOrDefault();
    var limit = int.TryParse(limitStr, out var l) ? l : 50;

    var alerts = await alertService.GetHistoryAsync(userId, limit, ct);
    return Results.Ok(alerts);
})
.WithName("GetAlertHistory");

app.MapPost("/api/alerts/{alertId}/snooze", async (string alertId, AlertSnoozeDto body, IAlertService alertService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { error = "userId is required." });

    var duration = body.DurationHours switch
    {
        <= 0 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(body.DurationHours)
    };

    await alertService.SnoozeAsync(body.AlertType ?? alertId, body.UserId, duration, ct);
    return Results.Ok(new { alertId, snoozedFor = duration.ToString(), userId = body.UserId });
})
.WithName("SnoozeAlert");

app.MapPost("/api/alerts/{alertId}/dismiss", async (string alertId, AlertDismissDto body, IAlertService alertService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { error = "userId is required." });

    await alertService.DismissAsync(alertId, body.UserId, ct);
    return Results.Ok(new { alertId, dismissed = true, userId = body.UserId });
})
.WithName("DismissAlert");

// ── Approval endpoints ───────────────────────────────────────────────────

app.MapGet("/api/approvals/pending", async (IApprovalGate gate, HttpContext http, CancellationToken ct) =>
{
    // Use authenticated user's ObjectId when available, otherwise fall back to query param
    var userId = http.Request.Query["userId"].FirstOrDefault() ?? "default";
    var pending = await gate.GetPendingAsync(userId, ct);

    return Results.Ok(pending.Select(r => new
    {
        id = r.RequestId,
        action = r.Context.Action,
        reasoning = r.Context.Reasoning,
        impact = r.Context.Impact,
        urgency = r.Context.Urgency,
        agentId = r.Context.AgentId,
        agentName = r.Context.AgentId,
        requestedAt = r.CreatedAt,
        timeoutAt = r.ExpiresAt,
        status = r.Decision.ToString().ToLowerInvariant(),
        comment = r.Comment
    }));
})
.WithName("GetPendingApprovals");

app.MapGet("/api/approvals/{requestId}", async (string requestId, IApprovalGate gate, CancellationToken ct) =>
{
    try
    {
        var result = await gate.GetResultAsync(requestId, ct);
        return Results.Ok(new
        {
            requestId = result.RequestId,
            decision = result.Decision.ToString().ToLowerInvariant(),
            comment = result.Comment,
            respondedAt = result.RespondedAt
        });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Approval request '{requestId}' not found." });
    }
})
.WithName("GetApprovalStatus");

app.MapPost("/api/approvals/{requestId}/respond", async (string requestId, ApprovalResponseDto body, IApprovalGate gate, Microsoft.AspNetCore.SignalR.IHubContext<TelemetryHub> hubContext, CancellationToken ct) =>
{
    if (!Enum.TryParse<ApprovalDecision>(body.Decision, true, out var decision) || decision == ApprovalDecision.Pending || decision == ApprovalDecision.TimedOut)
    {
        return Results.BadRequest(new { error = "Decision must be 'Approved', 'Rejected', or 'Modified'." });
    }

    try
    {
        await gate.RespondAsync(requestId, decision, body.Comment, ct);

        // Notify connected dashboard clients of the resolution
        await hubContext.Clients.All.SendAsync("approval_resolved", new
        {
            requestId,
            decision = decision.ToString().ToLowerInvariant(),
            comment = body.Comment,
            respondedAt = DateTimeOffset.UtcNow
        });

        return Results.Ok(new { requestId, decision = decision.ToString().ToLowerInvariant(), comment = body.Comment });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Approval request '{requestId}' not found." });
    }
})
.WithName("RespondToApproval");

app.MapGet("/api/approvals/history", async (IApprovalGate gate, CancellationToken ct) =>
{
    var history = await gate.GetHistoryAsync(50, ct);

    return Results.Ok(history.Select(r => new
    {
        id = r.RequestId,
        action = r.Context.Action,
        reasoning = r.Context.Reasoning,
        impact = r.Context.Impact,
        urgency = r.Context.Urgency,
        agentId = r.Context.AgentId,
        agentName = r.Context.AgentId,
        requestedAt = r.CreatedAt,
        timeoutAt = r.ExpiresAt,
        status = r.Decision.ToString().ToLowerInvariant(),
        decidedAt = r.RespondedAt,
        comment = r.Comment
    }));
})
.WithName("GetApprovalHistory");

// ── Trace endpoints ──────────────────────────────────────────────────────

app.MapGet("/api/traces/recent", (ITraceCollector traceCollector, int? count) =>
{
    var traces = traceCollector.GetRecentTraces(count ?? 20);
    return Results.Ok(traces);
})
.WithName("GetRecentTraces");

app.MapGet("/api/traces/{traceId}/summary", (string traceId, ITraceCollector traceCollector) =>
{
    var summary = traceCollector.GetStructuredSummary(traceId);
    return summary is not null
        ? Results.Ok(summary)
        : Results.NotFound(new { error = $"Trace '{traceId}' not found." });
})
.WithName("GetTraceSummary");

app.MapGet("/api/traces/{traceId}/spans", (string traceId, ITraceCollector traceCollector) =>
{
    var spans = traceCollector.GetSpans(traceId);
    return spans is not null
        ? Results.Ok(spans)
        : Results.NotFound(new { error = $"Trace '{traceId}' not found." });
})
.WithName("GetTraceSpans");

// ── Promo planning endpoints ─────────────────────────────────────────────

app.MapGet("/api/promo/calendar", async (HttpContext http, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var brand = http.Request.Query["brand"].FirstOrDefault();
    var region = http.Request.Query["region"].FirstOrDefault();
    var monthsStr = http.Request.Query["months"].FirstOrDefault();
    var months = int.TryParse(monthsStr, out var m) ? m : 6;

    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/promo/calendar?months={months}";
    if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
    if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetPromoCalendar");

app.MapGet("/api/promo/types", async (IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var response = await client.GetAsync("/api/promo/types", ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetPromoTypes");

app.MapPost("/api/taskmodule/promo", async (PromoEvaluationRequest request, IHttpClientFactory httpFactory, IApprovalGate approvalGate, ILogger<Program> logger, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Brand) || string.IsNullOrWhiteSpace(request.Region) ||
        string.IsNullOrWhiteSpace(request.PromoType) || request.Budget <= 0)
    {
        return Results.BadRequest(new { error = "Fields brand, region, promoType, and budget (> 0) are required." });
    }

    if (!DateOnly.TryParse(request.StartDate, out var startDate) || !DateOnly.TryParse(request.EndDate, out var endDate))
    {
        return Results.BadRequest(new { error = "startDate and endDate must be valid ISO dates (yyyy-MM-dd)." });
    }

    var durationWeeks = Math.Max(1, (endDate.DayNumber - startDate.DayNumber) / 7);
    var client = httpFactory.CreateClient("McpServer");

    // Orchestrate: call all promo tools in parallel
    var historyTask = client.GetStringAsync(
        $"/api/promo/history?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&months=12", ct);
    var liftTask = client.GetStringAsync(
        $"/api/promo/calculate-lift?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&spend={request.Budget}", ct);
    var timingTask = client.GetStringAsync(
        $"/api/promo/evaluate-timing?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&startDate={Uri.EscapeDataString(request.StartDate)}&endDate={Uri.EscapeDataString(request.EndDate)}", ct);
    var roiTask = client.GetStringAsync(
        $"/api/promo/estimate-roi?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&spend={request.Budget}&durationWeeks={durationWeeks}", ct);

    await Task.WhenAll(historyTask, liftTask, timingTask, roiTask);

    var historyJson = await historyTask;
    var liftJson = await liftTask;
    var timingJson = await timingTask;
    var roiJson = await roiTask;

    // Parse ROI for approval gate decision
    using var roiDoc = System.Text.Json.JsonDocument.Parse(roiJson);
    var expectedRoi = roiDoc.RootElement.TryGetProperty("expected_roi", out var roiProp) ? roiProp.GetDouble() : 0;

    // Determine recommendation
    var recommendation = expectedRoi switch
    {
        >= 3.0 => "strongly_recommended",
        >= 2.0 => "recommended",
        >= 1.0 => "proceed_with_caution",
        _ => "not_recommended"
    };

    // Build risk factors
    var riskFactors = new List<string>();
    using var timingDoc = System.Text.Json.JsonDocument.Parse(timingJson);
    if (timingDoc.RootElement.TryGetProperty("conflicts", out var conflicts) && conflicts.GetArrayLength() > 0)
        riskFactors.Add($"{conflicts.GetArrayLength()} overlapping campaign(s) detected");
    if (timingDoc.RootElement.TryGetProperty("risks", out var risks))
    {
        foreach (var risk in risks.EnumerateArray())
        {
            if (risk.TryGetProperty("detail", out var detail))
                riskFactors.Add(detail.GetString() ?? "Unknown risk");
        }
    }
    if (expectedRoi < 1.0)
        riskFactors.Add("Expected ROI below breakeven (1.0x)");
    if (request.Budget > 500000)
        riskFactors.Add("High-budget campaign (>$500K) — requires executive approval");

    // Check approval gate trigger
    string? approvalRequestId = null;
    var requiresApproval = request.Budget > 500000 || (expectedRoi < 2.0 && request.Budget > 100000);
    if (requiresApproval)
    {
        var reason = request.Budget > 500000
            ? $"High-budget promo: ${request.Budget:N0} for {request.Brand} in {request.Region}"
            : $"Low-ROI risk: {expectedRoi:F2}x ROI with ${request.Budget:N0} budget for {request.Brand}";

        var approvalRequest = await approvalGate.RequestApprovalAsync(new RetailPulse.Contracts.Approval.ApprovalContext(
            AgentId: "promo-planning",
            UserId: "taskmodule",
            Action: $"Execute {request.PromoType} promotion for {request.Brand} in {request.Region}",
            Impact: $"Budget: ${request.Budget:N0}, Expected ROI: {expectedRoi:F2}x, Duration: {durationWeeks} weeks",
            Urgency: request.Budget > 500000 ? "high" : "medium",
            Reasoning: reason
        ), ct);

        approvalRequestId = approvalRequest.RequestId;
        logger.LogInformation("Promo task module triggered approval gate: {RequestId} for {Brand}/{Region}", approvalRequestId, request.Brand, request.Region);
    }

    return Results.Ok(new
    {
        recommendation,
        brand = request.Brand,
        region = request.Region,
        promo_type = request.PromoType,
        budget = request.Budget,
        period = new { start = request.StartDate, end = request.EndDate, duration_weeks = durationWeeks },
        target_lift = request.TargetLift,
        roi_estimate = System.Text.Json.JsonSerializer.Deserialize<object>(roiJson),
        timing_assessment = System.Text.Json.JsonSerializer.Deserialize<object>(timingJson),
        lift_analysis = System.Text.Json.JsonSerializer.Deserialize<object>(liftJson),
        historical_context = System.Text.Json.JsonSerializer.Deserialize<object>(historyJson),
        risk_factors = riskFactors,
        approval = requiresApproval ? new
        {
            required = true,
            request_id = approvalRequestId,
            reason = request.Budget > 500000 ? "high_budget" : "low_roi_risk"
        } : new
        {
            required = false,
            request_id = (string?)null,
            reason = (string?)null
        }
    });
})
.WithName("PromoTaskModule");

// ── Knowledge Base endpoints ─────────────────────────────────────────────

app.MapPost("/api/knowledge/upload", async (KnowledgeUploadRequest body, IKnowledgeBase kb, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Content))
        return Results.BadRequest(new { error = "Fields 'title' and 'content' are required." });

    var id = await kb.IngestDocumentAsync(body.Title, body.Content, body.Source ?? "upload", ct);
    return Results.Ok(new { documentId = id, title = body.Title, status = "ingested" });
})
.WithName("UploadKnowledge");

app.MapGet("/api/knowledge/documents", async (IKnowledgeBase kb, CancellationToken ct) =>
{
    var docs = await kb.ListDocumentsAsync(ct);
    return Results.Ok(docs);
})
.WithName("ListKnowledgeDocuments");

app.MapDelete("/api/knowledge/documents/{id}", async (string id, IKnowledgeBase kb, CancellationToken ct) =>
{
    await kb.DeleteDocumentAsync(id, ct);
    return Results.Ok(new { documentId = id, status = "deleted" });
})
.WithName("DeleteKnowledgeDocument");

app.MapPost("/api/knowledge/search", async (KnowledgeSearchRequest body, IKnowledgeBase kb, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Query))
        return Results.BadRequest(new { error = "Field 'query' is required." });

    var results = await kb.SearchAsync(body.Query, body.TopK ?? 5, ct);
    return Results.Ok(new { query = body.Query, results });
})
.WithName("SearchKnowledge");

app.MapGet("/api/knowledge/stats", async (IKnowledgeBase kb, CancellationToken ct) =>
{
    var docs = await kb.ListDocumentsAsync(ct);
    var docCount = docs.Count;
    var chunkCount = docs.Sum(d => d.ChunkCount);
    var avgChunks = docCount > 0 ? (double)chunkCount / docCount : 0;

    return Results.Ok(new
    {
        documentCount = docCount,
        chunkCount,
        averageChunksPerDocument = Math.Round(avgChunks, 1)
    });
})
.WithName("KnowledgeStats");

// ── Council endpoints ────────────────────────────────────────────────────

app.MapPost("/api/council/convene", async (CouncilConveneRequest body, ILogger<Program> logger, CancellationToken ct, RetailPulse.Contracts.Consensus.IConsensusCouncil? council = null) =>
{
    if (string.IsNullOrWhiteSpace(body.Brand))
        return Results.BadRequest(new { error = "Field 'brand' is required." });

    if (council is null)
    {
        logger.LogWarning("Council convene requested but IConsensusCouncil is not registered");
        return Results.StatusCode(503);
    }

    logger.LogInformation("Council convening for brand={Brand}, region={Region}",
        body.Brand, body.Region ?? "All");

    var verdict = await council.ConveneAsync(body.Brand, body.Region, ct);

    return Results.Ok(new
    {
        brand = verdict.Brand,
        region = verdict.Region ?? "All Regions",
        overall_rating = verdict.OverallRating.ToString(),
        synthesis = verdict.Synthesis,
        is_unanimous = verdict.IsUnanimous,
        disagreements = verdict.Disagreements,
        action_items = verdict.ActionItems,
        convened_at = verdict.ConvenedAt,
        total_duration_ms = verdict.TotalDuration.TotalMilliseconds,
        votes = verdict.Votes.Select(v => new
        {
            agent_id = v.AgentId,
            agent_name = v.AgentName,
            rating = v.Rating.ToString(),
            reasoning = v.Reasoning,
            confidence = v.Confidence,
            key_metrics = v.KeyMetrics,
            response_time_ms = v.ResponseTime.TotalMilliseconds
        })
    });
})
.WithName("ConveneCouncil");

app.MapGet("/api/council/agents", (IEnumerable<ISpecialistAgent> specialists) =>
{
    var agents = specialists.Select(s => new
    {
        key = s.Key,
        display_name = s.DisplayName,
        supported_intents = s.SupportedIntents,
        domain = s.Key switch
        {
            "demand-forecasting" => "Demand & forecasting analysis",
            "promo-planning" => "Promotion planning & ROI estimation",
            "competitive-intel" => "Competitive intelligence & market share",
            "supply-chain" => "Supply chain health & disruption tracking",
            "memory" => "Conversation memory management",
            "general" => "General retail operations (fallback)",
            _ => "Unknown domain"
        }
    }).ToList();

    return Results.Ok(new { agents, total = agents.Count });
})
.WithName("ListCouncilAgents");

// ── Card endpoints ───────────────────────────────────────────────────────

app.MapPost("/api/cards", async (CreateCardRequest body, IAdaptiveCardState cardState, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Title))
        return Results.BadRequest(new { error = "Field 'title' is required." });

    var card = await cardState.CreateAsync(body, ct);
    return Results.Ok(card);
})
.WithName("CreateCard");

app.MapGet("/api/cards", async (HttpContext http, IAdaptiveCardState cardState, CancellationToken ct) =>
{
    // Parse optional filters
    var typeStr = http.Request.Query["type"].FirstOrDefault();
    var lifecycleStr = http.Request.Query["lifecycle"].FirstOrDefault();

    CardType? typeFilter = Enum.TryParse<CardType>(typeStr, true, out var t) ? t : null;
    CardLifecycle? lifecycleFilter = Enum.TryParse<CardLifecycle>(lifecycleStr, true, out var l) ? l : null;

    // Use ListAsync if the implementation supports it, otherwise fall back to GetActiveAsync
    if (cardState is RetailPulse.Api.Cards.InMemoryAdaptiveCardState impl)
    {
        var cards = await impl.ListAsync(typeFilter, lifecycleFilter, ct);
        return Results.Ok(cards);
    }

    var active = await cardState.GetActiveAsync(ct);
    return Results.Ok(active);
})
.WithName("ListCards");

app.MapGet("/api/cards/{id}", async (string id, IAdaptiveCardState cardState, CancellationToken ct) =>
{
    try
    {
        var card = await cardState.GetAsync(id, ct);
        return Results.Ok(card);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Card '{id}' not found." });
    }
})
.WithName("GetCard");

app.MapPost("/api/cards/{id}/action", async (string id, CardAction body, IAdaptiveCardState cardState, CancellationToken ct) =>
{
    try
    {
        var card = await cardState.ActionAsync(id, body, ct);
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
.WithName("CardAction");

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
.WithName("ArchiveCard");

// ── Observability endpoints ──────────────────────────────────────────────

app.MapGet("/api/observability/costs", async (HttpContext http, ICostTracker costTracker, CancellationToken ct) =>
{
    var periodStr = http.Request.Query["period"].FirstOrDefault() ?? "week";
    var period = Enum.TryParse<CostPeriod>(periodStr, true, out var p) ? p : CostPeriod.Week;
    var summary = await costTracker.GetSummaryAsync(period, ct);
    return Results.Ok(summary);
})
.WithName("GetCostSummary");

app.MapGet("/api/observability/costs/agents", async (HttpContext http, ICostTracker costTracker, CancellationToken ct) =>
{
    var periodStr = http.Request.Query["period"].FirstOrDefault() ?? "week";
    var period = Enum.TryParse<CostPeriod>(periodStr, true, out var p) ? p : CostPeriod.Week;
    var agents = await costTracker.GetByAgentAsync(period, ct);
    return Results.Ok(agents);
})
.WithName("GetCostsByAgent");

app.MapGet("/api/observability/costs/trend", async (HttpContext http, ICostTracker costTracker, CancellationToken ct) =>
{
    var daysStr = http.Request.Query["days"].FirstOrDefault();
    var days = int.TryParse(daysStr, out var d) ? d : 7;
    var trend = await costTracker.GetTrendAsync(days, ct);
    return Results.Ok(trend);
})
.WithName("GetCostTrend");

app.MapGet("/api/observability/audit", async (HttpContext http, IAuditLog auditLog, CancellationToken ct) =>
{
    var query = new AuditQuery(
        AgentId: http.Request.Query["agentId"].FirstOrDefault(),
        UserId: http.Request.Query["userId"].FirstOrDefault(),
        From: DateTime.TryParse(http.Request.Query["from"].FirstOrDefault(), out var from) ? from : null,
        To: DateTime.TryParse(http.Request.Query["to"].FirstOrDefault(), out var to) ? to : null,
        Action: http.Request.Query["action"].FirstOrDefault(),
        Limit: int.TryParse(http.Request.Query["limit"].FirstOrDefault(), out var limit) ? limit : 50
    );

    var entries = await auditLog.QueryAsync(query, ct);
    return Results.Ok(entries);
})
.WithName("GetAuditLog");

app.MapGet("/api/observability/audit/stats", async (IAuditLog auditLog, CancellationToken ct) =>
{
    var stats = await auditLog.GetStatsAsync(ct);
    return Results.Ok(stats);
})
.WithName("GetAuditStats");

app.MapGet("/api/observability/export/sessions", async (IConversationExport exporter, CancellationToken ct) =>
{
    var sessions = await exporter.ListSessionsAsync(ct);
    return Results.Ok(sessions);
})
.WithName("ListExportSessions");

app.MapPost("/api/observability/export/{sessionId}", async (string sessionId, HttpContext http, IConversationExport exporter, CancellationToken ct) =>
{
    var formatStr = http.Request.Query["format"].FirstOrDefault() ?? "markdown";
    var format = string.Equals(formatStr, "json", StringComparison.OrdinalIgnoreCase)
        ? ExportFormat.Json
        : ExportFormat.Markdown;

    try
    {
        var result = await exporter.ExportAsync(sessionId, format, ct);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
    }
})
.WithName("ExportSession");

app.MapGet("/api/supply/health", async (string brand, IHttpClientFactory httpFactory, CancellationToken ct, string? region = null) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/supply/health?brand={Uri.EscapeDataString(brand)}";
    if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetSupplyHealth");

app.MapGet("/api/supply/inventory", async (IHttpClientFactory httpFactory, CancellationToken ct, string? brand = null, string? region = null, string? category = null, string? status = null) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = "/api/supply/inventory?";
    if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
    if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
    if (!string.IsNullOrWhiteSpace(category)) url += $"&category={Uri.EscapeDataString(category)}";
    if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetSupplyInventory");

app.MapGet("/api/supply/disruptions", async (IHttpClientFactory httpFactory, CancellationToken ct, string? brand = null, string? region = null, string? severity = null, bool activeOnly = true) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/supply/disruptions?activeOnly={activeOnly}";
    if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
    if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
    if (!string.IsNullOrWhiteSpace(severity)) url += $"&severity={Uri.EscapeDataString(severity)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetSupplyDisruptions");

app.MapGet("/api/supply/fulfillment", async (IHttpClientFactory httpFactory, CancellationToken ct, string? brand = null, string? region = null, string? period = null, int minPeriods = 6) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/supply/fulfillment?minPeriods={minPeriods}";
    if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
    if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
    if (!string.IsNullOrWhiteSpace(period)) url += $"&period={Uri.EscapeDataString(period)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetSupplyFulfillment");

// ── Store operations endpoints ───────────────────────────────────────────

app.MapGet("/api/stores/performance", async (IHttpClientFactory httpFactory, CancellationToken ct, string? region = null) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = "/api/stores/performance?";
    if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetStorePerformance");

app.MapGet("/api/stores/{storeId}/planogram/{aisleId}", async (string storeId, string aisleId, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/stores/{Uri.EscapeDataString(storeId)}/planogram/{Uri.EscapeDataString(aisleId)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetShelfLayout");

app.MapPost("/api/stores/{storeId}/planogram/{aisleId}/optimize", async (string storeId, string aisleId, IHttpClientFactory httpFactory, CancellationToken ct, string? brandFocus = null) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/stores/{Uri.EscapeDataString(storeId)}/planogram/{Uri.EscapeDataString(aisleId)}/optimize?";
    if (!string.IsNullOrWhiteSpace(brandFocus)) url += $"&brandFocus={Uri.EscapeDataString(brandFocus)}";

    var response = await client.PostAsync(url, null, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("OptimizePlanogram");

app.MapGet("/api/stores/{storeId}/stockout-risk", async (string storeId, IHttpClientFactory httpFactory, CancellationToken ct, string? skuId = null) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/stores/{Uri.EscapeDataString(storeId)}/stockout-risk?";
    if (!string.IsNullOrWhiteSpace(skuId)) url += $"&skuId={Uri.EscapeDataString(skuId)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("PredictStockout");

// ── Margin endpoints ─────────────────────────────────────────────────────

app.MapGet("/api/margin/{brandId}", async (string brandId, IHttpClientFactory httpFactory, CancellationToken ct, string? period = null) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/margin/{Uri.EscapeDataString(brandId)}?";
    if (!string.IsNullOrWhiteSpace(period)) url += $"&period={Uri.EscapeDataString(period)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetMarginByBrand");

app.MapGet("/api/margin/drivers/{brandId}", async (string brandId, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/margin/drivers/{Uri.EscapeDataString(brandId)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetMarginDrivers");

app.MapGet("/api/margin/trend/{brandId}", async (string brandId, IHttpClientFactory httpFactory, CancellationToken ct, int quarters = 4) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/margin/trend/{Uri.EscapeDataString(brandId)}?quarters={quarters}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetMarginTrend");

app.MapGet("/api/margin/risks", async (IHttpClientFactory httpFactory, CancellationToken ct, string? brandId = null) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var url = "/api/margin/risks?";
    if (!string.IsNullOrWhiteSpace(brandId)) url += $"&brandId={Uri.EscapeDataString(brandId)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("DetectMarginRisks");

// ── Scorecard endpoints ──────────────────────────────────────────────────

app.MapPost("/api/scorecard", async (ScorecardRequest body, RetailPulse.Api.Scorecard.ScorecardOrchestrator? scorecard, CancellationToken ct) =>
{
    if (scorecard is null)
        return Results.StatusCode(503);

    if (body.Brands is null || body.Brands.Length == 0)
        return Results.BadRequest(new { error = "At least one brand is required." });

    var result = await scorecard.GenerateAsync(body.Brands, body.Region, ct);
    return Results.Ok(result);
})
.WithName("GenerateScorecard");

// ── Explainability endpoints ─────────────────────────────────────────────

app.MapGet("/api/explain/{traceId}", (string traceId, RetailPulse.Api.Explainability.ExplainabilityService explainability) =>
{
    var trace = explainability.GetTrace(traceId);
    if (trace is null)
        return Results.NotFound(new { error = $"Trace '{traceId}' not found." });

    return Results.Ok(new
    {
        traceId,
        trace.SessionId,
        trace.Query,
        trace.ToolCallCount,
        trace.TotalDurationMs,
        trace.StartedAt,
        dataSources = trace.DataSources,
        reasoningChain = trace.ReasoningChain,
        explanation = explainability.BuildExplanation(traceId)
    });
})
.WithName("GetExplanation");

app.MapGet("/api/explain/session/{sessionId}", (string sessionId, RetailPulse.Api.Explainability.ExplainabilityService explainability) =>
{
    var traces = explainability.GetSessionTraces(sessionId);
    return Results.Ok(traces.Select(t => new
    {
        traceId = $"{t.SessionId}-{t.StartedAt:yyyyMMddHHmmss}",
        t.Query,
        t.ToolCallCount,
        t.TotalDurationMs,
        t.StartedAt
    }));
})
.WithName("GetSessionTraces");

// ── Message Extension endpoints ──────────────────────────────────────────

app.MapPost("/api/message-extension/query", async (MessageExtensionRequest body, IKnowledgeBase kb, IEnumerable<ISpecialistAgent> specialists, ILogger<Program> logger, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Text))
        return Results.BadRequest(new { error = "Field 'text' is required." });

    // Search knowledge base for relevant context
    var searchResults = await kb.SearchAsync(body.Text, 5, ct);
    var citations = searchResults
        .Where(r => r.Score >= 0.3)
        .Select(r => new
        {
            source = r.Title,
            chunk = r.ChunkIndex >= 0 ? $"Chunk {r.ChunkIndex}" : r.Title,
            relevance = Math.Round(r.Score, 2)
        })
        .ToList();

    // Build grounded context for the agent
    var contextBuilder = new System.Text.StringBuilder();
    if (searchResults.Count > 0)
    {
        contextBuilder.AppendLine("--- Reference Context (from knowledge base) ---");
        foreach (var result in searchResults.Take(3))
        {
            contextBuilder.AppendLine($"[Source: {result.Title}, chunk {result.ChunkIndex}]");
            contextBuilder.AppendLine(result.Chunk);
            contextBuilder.AppendLine();
        }
        contextBuilder.AppendLine("--- End Reference Context ---");
    }

    // Route to GeneralAgent with RAG context
    var generalAgent = specialists.FirstOrDefault(s => s.Key == "general");
    if (generalAgent is null)
        return Results.StatusCode(503);

    var ragHistory = new List<ChatHistoryMessage>();
    if (contextBuilder.Length > 0)
        ragHistory.Add(new ChatHistoryMessage("system", contextBuilder.ToString()));
    if (!string.IsNullOrWhiteSpace(body.Context))
        ragHistory.Add(new ChatHistoryMessage("system", $"Teams channel context: {body.Context}"));

    var chatRequest = new ChatRequest(
        body.Text,
        SessionId: null,
        User: null,
        History: ragHistory
    );

    try
    {
        var response = await generalAgent.HandleAsync(chatRequest, ct);

        var confidence = citations.Count switch
        {
            >= 3 => "high",
            >= 1 => "medium",
            _ => "low"
        };

        return Results.Ok(new
        {
            answer = response.Reply,
            citations,
            confidence,
            agentUsed = generalAgent.DisplayName
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Message extension query failed for text: {Text}", body.Text[..Math.Min(50, body.Text.Length)]);
        return Results.StatusCode(503);
    }
})
.WithName("MessageExtensionQuery");

app.MapGet("/api/message-extension/manifest", () =>
{
    var manifest = new
    {
        schema = "https://developer.microsoft.com/json-schemas/teams/v1.16/MicrosoftTeams.schema.json",
        manifestVersion = "1.16",
        version = "1.0.0",
        id = "retail-pulse-message-extension",
        name = new { @short = "Retail Pulse Lookup", full = "Retail Pulse Knowledge Base Lookup" },
        description = new
        {
            @short = "Search retail knowledge base from Teams messages",
            full = "Select text in a Teams message and look up relevant retail insights, best practices, and data from the Retail Pulse knowledge base."
        },
        composeExtensions = new[]
        {
            new
            {
                botId = "{{BOT_ID}}",
                commands = new[]
                {
                    new
                    {
                        id = "searchKnowledge",
                        type = "query",
                        title = "Search Knowledge Base",
                        description = "Look up retail insights and best practices",
                        initialRun = false,
                        parameters = new[]
                        {
                            new
                            {
                                name = "query",
                                title = "Search Query",
                                description = "Text to search for in the knowledge base",
                                inputType = "text"
                            }
                        }
                    }
                }
            }
        }
    };

    return Results.Ok(manifest);
})
.WithName("MessageExtensionManifest");

// ── Cache endpoints ───────────────────────────────────────────────────────

app.MapGet("/api/cache/stats", async (IResponseCache cache, CancellationToken ct) =>
{
    var stats = await cache.GetStatsAsync(ct);
    return Results.Ok(new
    {
        totalEntries = stats.TotalEntries,
        hits = stats.Hits,
        misses = stats.Misses,
        hitRate = Math.Round(stats.HitRate, 4),
        memoryBytes = stats.MemoryBytes
    });
})
.WithName("GetCacheStats");

app.MapDelete("/api/cache", async (IResponseCache cache, CancellationToken ct) =>
{
    await cache.InvalidateAsync(null, ct);
    return Results.Ok(new { status = "cleared" });
})
.WithName("ClearCache");

app.MapDelete("/api/cache/{key}", async (string key, IResponseCache cache, CancellationToken ct) =>
{
    await cache.InvalidateAsync(key, ct);
    return Results.Ok(new { key, status = "invalidated" });
})
.WithName("InvalidateCacheKey");

// ── Guardrails endpoints ─────────────────────────────────────────────────

app.MapGet("/api/guardrails/log", async (ISuspiciousRequestLog log, HttpContext http, CancellationToken ct) =>
{
    var countStr = http.Request.Query["count"].FirstOrDefault();
    var count = int.TryParse(countStr, out var c) ? c : 50;

    var recent = await log.GetRecentAsync(count, ct);
    return Results.Ok(recent.Select(r => new
    {
        id = r.Id,
        timestamp = r.Timestamp,
        requestText = r.RequestText,
        detectionType = r.DetectionType,
        userContext = r.UserContext,
        action = r.Action
    }));
})
.WithName("GetGuardrailsLog");

app.MapGet("/api/guardrails/stats", async (ISuspiciousRequestLog log, CancellationToken ct) =>
{
    var stats = await log.GetStatsAsync(ct);
    return Results.Ok(new
    {
        totalBlocked = stats.TotalBlocked,
        jailbreakAttempts = stats.JailbreakAttempts,
        piiDetections = stats.PiiDetections,
        accessDenials = stats.AccessDenials,
        since = stats.Since
    });
})
.WithName("GetGuardrailsStats");

app.MapGet("/api/guardrails/config", (GuardrailsConfig config) =>
{
    return Results.Ok(new
    {
        piiDetectionEnabled = config.PiiDetectionEnabled,
        jailbreakDetectionEnabled = config.JailbreakDetectionEnabled,
        autoRedactPii = config.AutoRedactPii,
        maxInputLength = config.MaxInputLength,
        piiPatterns = RetailPulse.Api.Guardrails.GuardrailPatterns.PiiPatterns.Select(p => p.Name).ToList(),
        jailbreakPatterns = RetailPulse.Api.Guardrails.GuardrailPatterns.JailbreakPatterns.Select(p => p.Name).ToList()
    });
})
.WithName("GetGuardrailsConfig");

app.MapPut("/api/guardrails/config", (GuardrailsConfigUpdateDto body, GuardrailsConfig config) =>
{
    if (body.PiiDetectionEnabled.HasValue)
        config.PiiDetectionEnabled = body.PiiDetectionEnabled.Value;
    if (body.JailbreakDetectionEnabled.HasValue)
        config.JailbreakDetectionEnabled = body.JailbreakDetectionEnabled.Value;
    if (body.AutoRedactPii.HasValue)
        config.AutoRedactPii = body.AutoRedactPii.Value;
    if (body.MaxInputLength.HasValue)
        config.MaxInputLength = body.MaxInputLength.Value;

    return Results.Ok(new
    {
        piiDetectionEnabled = config.PiiDetectionEnabled,
        jailbreakDetectionEnabled = config.JailbreakDetectionEnabled,
        autoRedactPii = config.AutoRedactPii,
        maxInputLength = config.MaxInputLength,
        status = "updated"
    });
})
.WithName("UpdateGuardrailsConfig");

// ── Escalation endpoint ─────────────────────────────────────────────────
app.MapPost("/api/escalate", async (ChatRequest request, RetailPulse.Api.Escalation.EscalationOrchestrator escalation, IAgentRouter router, ILogger<Program> logger, CancellationToken ct) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "Field 'message' is required." });

    var decision = await router.RouteAsync(request.Message, request.History, request.User, null, ct);
    var result = await escalation.EscalateAsync(request, decision, ct);

    return Results.Ok(new
    {
        reply = result.Reply,
        level = result.Level,
        agentsConsulted = result.AgentsConsulted,
        durationMs = result.DurationMs,
        needsHumanReview = result.NeedsHumanReview,
        escalationReason = result.EscalationReason
    });
})
.WithName("Escalate");

app.Run();

// ── Helpers ──────────────────────────────────────────────────────────────

/// <summary>
/// Best-effort extraction of a brand name from a user message by matching
/// against the known brands in tenant configuration.
/// </summary>
static string ExtractBrand(string message, RetailPulse.Contracts.TenantConfiguration tenant)
{
    foreach (var brand in tenant.Brands)
    {
        if (message.Contains(brand.Name, StringComparison.OrdinalIgnoreCase))
            return brand.Name;
    }
    // Fallback: return the first brand if none matched
    return tenant.Brands.FirstOrDefault()?.Name ?? "Unknown";
}

// ── DTOs ─────────────────────────────────────────────────────────────────

/// <summary>
/// Request body for the POST /api/approvals/{requestId}/respond endpoint.
/// </summary>
record ApprovalResponseDto(string Decision, string? Comment = null);

/// <summary>
/// Request body for the POST /api/alerts/{alertId}/snooze endpoint.
/// </summary>
record AlertSnoozeDto(string UserId, string? AlertType = null, double DurationHours = 1);

/// <summary>
/// Request body for the POST /api/alerts/{alertId}/dismiss endpoint.
/// </summary>
record AlertDismissDto(string UserId);

/// <summary>
/// Request body for POST /api/knowledge/upload.
/// </summary>
record KnowledgeUploadRequest(string Title, string Content, string? Source = null);

/// <summary>
/// Request body for POST /api/knowledge/search.
/// </summary>
record KnowledgeSearchRequest(string Query, int? TopK = 5);

/// <summary>
/// Request body for POST /api/message-extension/query.
/// </summary>
record MessageExtensionRequest(string Text, string? Context = null);

/// <summary>
/// Request body for the POST /api/taskmodule/promo endpoint.
/// </summary>
record PromoEvaluationRequest(
    string Brand,
    string Region,
    string PromoType,
    double Budget,
    string StartDate,
    string EndDate,
    double? TargetLift = null
);

/// <summary>
/// Request body for POST /api/council/convene.
/// </summary>
record CouncilConveneRequest(string Brand, string? Region = null);

/// <summary>
/// Request body for PUT /api/guardrails/config.
/// </summary>
record GuardrailsConfigUpdateDto(
    bool? PiiDetectionEnabled = null,
    bool? JailbreakDetectionEnabled = null,
    bool? AutoRedactPii = null,
    int? MaxInputLength = null);

/// <summary>
/// Request body for POST /api/scorecard.
/// </summary>
record ScorecardRequest(string[] Brands, string? Region = null);
