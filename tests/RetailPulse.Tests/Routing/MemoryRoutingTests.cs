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
    [InlineData("What do you remember about ClearDesk?")]
    public void TryKeywordClassify_StoreAndRecallPhrases_DoNotRouteToMemoryManagement(string message)
    {
        object? classification = TryKeywordClassify(message);

        classification.Should().BeNull();
    }

    [Theory]
    [InlineData("Forget everything")]
    [InlineData("Clear my history")]
    [InlineData("Start fresh")]
    [InlineData("Reset my context")]
    [InlineData("Forget what I told you")]
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
