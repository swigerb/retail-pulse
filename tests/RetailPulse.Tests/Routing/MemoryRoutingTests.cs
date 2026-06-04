using System.Reflection;
using FluentAssertions;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Routing;

public class MemoryRoutingTests
{
    private static readonly MethodInfo TryKeywordClassifyMethod =
        typeof(RetailOpsRouter).GetMethod("TryKeywordClassify", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [InlineData("Remember that ClearDesk is trending positive")]
    [InlineData("Remember this for next time: margins are up")]
    [InlineData("Remember ClearDesk is trending modestly positive in the Northeast this quarter")]
    [InlineData("Remember that ClearDesk depletions are trending in the Northeast this quarter")]
    public void TryKeywordClassify_StorePhrases_RouteToMemoryManagement(string message)
    {
        object? classification = TryKeywordClassify(message);

        classification.Should().NotBeNull();
        GetIntent(classification).Should().Be(AgentIntent.MemoryManagement);
    }

    [Theory]
    [InlineData("What do you remember about ClearDesk?")]
    [InlineData("I'm focused on the Spirits category, especially premium tequila positioning")]
    public void TryKeywordClassify_RecallAndPreferencePhrases_DoNotRouteToMemoryManagement(string message)
    {
        object? classification = TryKeywordClassify(message);

        classification.Should().BeNull();
    }

    [Theory]
    [InlineData("Forget everything")]
    [InlineData("Clear my history")]
    [InlineData("Clear my data")]
    [InlineData("Start fresh")]
    [InlineData("Reset my context")]
    [InlineData("Forget what I told you")]
    [InlineData("What do you know about me?")]
    public void TryKeywordClassify_ClearPhrases_RouteToMemoryManagement(string message)
    {
        object? classification = TryKeywordClassify(message);

        classification.Should().NotBeNull();
        GetIntent(classification).Should().Be(AgentIntent.MemoryManagement);
    }

    private static object? TryKeywordClassify(string message)
        => TryKeywordClassifyMethod.Invoke(null, [message]);

    private static string? GetIntent(object? classification)
        => classification?.GetType().GetProperty("Intent")?.GetValue(classification) as string;
}
