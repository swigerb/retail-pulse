using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;

var projectEndpoint = args.Length > 0 ? args[0] : "https://bs-dev-swedencentral-aoai.services.ai.azure.com/api/projects/bs-dev-swedencentral-aoai-project";
var modelName = args.Length > 1 ? args[1] : "gpt-5.4-mini";

Console.WriteLine($"Connecting to Foundry project: {projectEndpoint}");
Console.WriteLine($"Model deployment: {modelName}");

var projectClient = new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential());

var instructions = """
You are the Retail Pulse Shipment Analysis Specialist, a Foundry-deployed agent that
analyzes Three-Tier Distribution dynamics for the configured tenant brand portfolio.

Your expertise is in detecting and explaining:
- Pipeline Clogs: When shipments (sell-in) outpace sell-through (consumer demand)
- Supply Constraints: When demand exceeds available supply
- Healthy Pipelines: When shipments and depletions are aligned
- Growth Opportunities: Where distribution expansion is creating healthy pipeline fill

When analyzing shipment data, always:
1. Calculate the shipment-to-depletion gap (cases shipped vs cases depleted)
2. Identify the anomaly type and risk level
3. Provide specific actionable recommendations for the brand team
4. Reference the Three-Tier system (Manufacturer -> Distributor -> Retailer -> Consumer)

Be concise but specific. Use bullet points for recommendations.
Always flag pipeline clogs as urgent business risks.
""";

const string agentName = "retail-pulse-shipment-specialist";

// Check if agent already exists
Console.WriteLine($"Checking for existing agent '{agentName}'...");
try
{
    var existing = await projectClient.AgentAdministrationClient.GetAgentAsync(agentName);
    Console.WriteLine($"Agent '{agentName}' already exists (id: {existing.Value.Id}). Deleting old version...");
    // List and delete all versions
    await foreach (var version in projectClient.AgentAdministrationClient.GetAgentVersionsAsync(agentName))
    {
        Console.WriteLine($"  Deleting version {version.Version}...");
        await projectClient.AgentAdministrationClient.DeleteAgentVersionAsync(agentName, version.Version);
    }
}
catch (System.ClientModel.ClientResultException ex) when (ex.Status == 404)
{
    Console.WriteLine("No existing agent found. Creating new.");
}

Console.WriteLine("Creating agent version...");

var agentDefinition = new DeclarativeAgentDefinition(model: modelName)
{
    Instructions = instructions
};

var agentVersion = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
    agentName: agentName,
    options: new ProjectsAgentVersionCreationOptions(agentDefinition));

Console.WriteLine($"Agent created successfully!");
Console.WriteLine($"Agent Name: {agentVersion.Value.Name}");
Console.WriteLine($"Agent Version: {agentVersion.Value.Version}");
Console.WriteLine($"Agent ID: {agentVersion.Value.Id}");
Console.WriteLine();
Console.WriteLine($"To configure Retail Pulse, run:");
Console.WriteLine($"  dotnet user-secrets set \"FoundryAgent:ShipmentAgentName\" \"{agentName}\" --project src/RetailPulse.Api");
Console.WriteLine();
Console.WriteLine("The agent should now be visible in the Foundry portal under the 'Agents' tab.");
