var builder = DistributedApplication.CreateBuilder(args);

// App Insights connection string — read from appsettings.json, flows to all services via environment variable
var appInsightsCs = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("APPLICATIONINSIGHTS_CONNECTION_STRING is not configured. Add it to appsettings.json or set it as an environment variable.");

var mcpServer = builder.AddProject<Projects.RetailPulse_McpServer>("mcpserver")
    .WithEnvironment("APPLICATIONINSIGHTS_CONNECTION_STRING", appInsightsCs);

var api = builder.AddProject<Projects.RetailPulse_Api>("api")
    .WithReference(mcpServer)
    .WithEnvironment("APPLICATIONINSIGHTS_CONNECTION_STRING", appInsightsCs);

var teamsBot = builder.AddProject<Projects.RetailPulse_TeamsBot>("teamsbot")
    .WithReference(api)
    .WaitFor(api)
    .WithEndpoint("http", e => { e.Port = 5300; e.IsProxied = false; })
    .WithEnvironment("APPLICATIONINSIGHTS_CONNECTION_STRING", appInsightsCs);

builder.AddNpmApp("frontend", "../RetailPulse.Web", "dev")
    .WithHttpEndpoint(port: 5173, name: "frontend-http", isProxied: false)
    .WithExternalHttpEndpoints();

builder.Build().Run();
