namespace RetailPulse.Api.Models;

public class PromptConfiguration
{
    public Dictionary<string, AgentDefinition> Agents { get; set; } = new();
}

public class AgentDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o";
    public string SystemPrompt { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
    public List<string> Tools { get; set; } = new();
}
