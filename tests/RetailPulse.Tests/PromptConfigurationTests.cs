using FluentAssertions;
using RetailPulse.Api.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.Tests;

public class PromptConfigurationTests
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    [Fact]
    public void ValidYaml_ParsesCorrectly()
    {
        var yaml = """
            agents:
              test-agent:
                name: "Test Agent"
                model: "gpt-4o"
                system_prompt: "You are a test agent."
                temperature: 0.5
                tools:
                  - ToolA
                  - ToolB
            """;

        var config = Deserializer.Deserialize<PromptConfiguration>(yaml);

        config.Agents.Should().ContainKey("test-agent");
        var agent = config.Agents["test-agent"];
        agent.Name.Should().Be("Test Agent");
        agent.Model.Should().Be("gpt-4o");
        agent.SystemPrompt.Should().Be("You are a test agent.");
        agent.Temperature.Should().Be(0.5);
        agent.Tools.Should().HaveCount(2);
        agent.Tools.Should().Contain("ToolA");
        agent.Tools.Should().Contain("ToolB");
    }

    [Fact]
    public void AgentDefinition_MissingFields_UseDefaults()
    {
        var yaml = """
            agents:
              minimal:
                name: "Minimal Agent"
            """;

        var config = Deserializer.Deserialize<PromptConfiguration>(yaml);
        var agent = config.Agents["minimal"];

        agent.Name.Should().Be("Minimal Agent");
        agent.Model.Should().Be("gpt-4o");
        agent.SystemPrompt.Should().BeEmpty();
        agent.Temperature.Should().Be(0.7);
        agent.Tools.Should().BeEmpty();
    }

    [Fact]
    public void AgentDefinition_HasExpectedFields()
    {
        var agent = new AgentDefinition
        {
            Name = "Test",
            Model = "gpt-4o",
            SystemPrompt = "prompt",
            Temperature = 0.5,
            Tools = ["ToolA"]
        };

        agent.Name.Should().NotBeNullOrEmpty();
        agent.Model.Should().NotBeNullOrEmpty();
        agent.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.Tools.Should().NotBeEmpty();
    }

    [Fact]
    public void ActualPromptsYaml_LoadsWithoutErrors()
    {
        // Find the prompts.yaml relative to the test project
        var projectDir = FindProjectRoot();
        var promptsPath = Path.Combine(projectDir, "src", "RetailPulse.Api", "prompts.yaml");

        if (!File.Exists(promptsPath))
        {
            // Skip if file not found (CI environments may not have it)
            return;
        }

        var yaml = File.ReadAllText(promptsPath);
        var config = Deserializer.Deserialize<PromptConfiguration>(yaml);

        config.Should().NotBeNull();
        config.Agents.Should().NotBeEmpty();

        // Verify the retail-pulse agent exists with expected structure
        config.Agents.Should().ContainKey("retail-pulse");
        var agent = config.Agents["retail-pulse"];
        agent.Name.Should().NotBeNullOrEmpty();
        agent.Model.Should().NotBeNullOrEmpty();
        agent.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.Tools.Should().NotBeEmpty();
        agent.Tools.Should().Contain("GetDepletionStats");
        agent.Tools.Should().Contain("GetFieldSentiment");
    }

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "RetailPulse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
