using Aspire.Hosting.Dotnet;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// App Insights connection string — read from appsettings.json, flows to all services via environment variable
string appInsightsCs = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] ?? "";

IResourceBuilder<DotnetProjectResource> mcpServer = builder.AddDotnetProject("mcpserver", "../RetailPulse.McpServer/RetailPulse.McpServer.csproj")
    .WithEnvironment("APPLICATIONINSIGHTS_CONNECTION_STRING", appInsightsCs);

IResourceBuilder<DotnetProjectResource> api = builder.AddDotnetProject("api", "../RetailPulse.Api/RetailPulse.Api.csproj")
    .WithReference(mcpServer)
    .WaitFor(mcpServer)
    .WithEnvironment("APPLICATIONINSIGHTS_CONNECTION_STRING", appInsightsCs);

IResourceBuilder<DotnetProjectResource> teamsBot = builder.AddDotnetProject("teamsbot", "../RetailPulse.TeamsBot/RetailPulse.TeamsBot.csproj")
    .WithReference(api)
    .WaitFor(api)
    .WithEndpoint("http", e => { e.Port = 5300; e.IsProxied = false; })
    .WithEnvironment("APPLICATIONINSIGHTS_CONNECTION_STRING", appInsightsCs);

// Frontend (Vite) — pass the API endpoint as an env var so vite.config.ts can
// proxy /api and /hubs to whatever URL Aspire assigned the API. This avoids
// hardcoding localhost:5100 inside the React project.
builder.AddJavaScriptApp("frontend", "../RetailPulse.Web", "dev")
    .WithNpm(install: false)
    .WithHttpEndpoint(port: 5173, name: "frontend-http", isProxied: false)
    .WithEnvironment("API_URL", api.GetEndpoint("http"))
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

// Azure deployment: backend services publish to Container Apps, frontend to App Service (configured in azure.yaml)
builder.Build().Run();
