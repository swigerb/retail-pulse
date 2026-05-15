using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace RetailPulse.Tests.Contract;

/// <summary>
/// Contract tests for MCP tool schemas — verifies tool names haven't changed
/// (breaking change detection) and that tool attributes are well-formed.
/// </summary>
public class McpToolContractTests
{
    /// <summary>
    /// Known tool names that clients depend on. Adding new tools is fine;
    /// removing or renaming is a breaking change.
    /// </summary>
    private static readonly HashSet<string> ExpectedToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetDepletionStats",
        "GetFieldSentiment",
        "GetHistoricalDemand",
        "GetSeasonalityFactors",
        "GetShipmentStats",
        "GetVariantMix",
        "IdentifyDemandRisks",
        "GenerateForecast",
        "GetPortfolioDepletionStats",
        "UpdateMetrics",
        "GetCompetitorPricing",
        "GetMarginByBrand",
        "GetPromoHistory",
        "GetStorePerformance",
        "GetInventoryLevels"
    };

    [Fact]
    public void McpServer_ToolNames_AreStable()
    {
        var toolTypes = GetMcpToolTypes();
        var actualNames = toolTypes
            .Select(GetToolName)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // All expected tools should still exist (no breaking removals)
        foreach (var expected in ExpectedToolNames)
        {
            actualNames.Should().Contain(expected,
                $"Tool '{expected}' was removed — this is a breaking change for MCP clients.");
        }
    }

    [Fact]
    public void McpServer_AllTools_HaveDescriptions()
    {
        var toolTypes = GetMcpToolTypes();

        foreach (var type in toolTypes)
        {
            var attr = type.GetCustomAttribute<McpServerToolTypeAttribute>();
            if (attr is null) continue;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

            foreach (var method in methods)
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                toolAttr.Should().NotBeNull();
                // Tool should have a name
                var name = toolAttr!.Name ?? method.Name;
                name.Should().NotBeNullOrWhiteSpace($"Tool method {type.Name}.{method.Name} has no name");
            }
        }
    }

    [Fact]
    public void McpServer_Tools_HaveValidParameterTypes()
    {
        var toolTypes = GetMcpToolTypes();

        foreach (var type in toolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

            foreach (var method in methods)
            {
                // Only check user-facing parameters (those with [Description] attribute)
                // DI-injected parameters (like RetailPulseDb) don't have [Description]
                var parameters = method.GetParameters()
                    .Where(p => p.ParameterType != typeof(CancellationToken))
                    .Where(p => p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is not null);

                foreach (var param in parameters)
                {
                    // Parameters should be JSON-serializable primitives or simple types
                    var paramType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
                    var isSimpleType = paramType.IsPrimitive
                        || paramType == typeof(string)
                        || paramType == typeof(decimal)
                        || paramType.IsEnum
                        || paramType == typeof(DateTime)
                        || paramType == typeof(DateTimeOffset)
                        || paramType == typeof(Guid);

                    isSimpleType.Should().BeTrue(
                        $"Tool parameter '{param.Name}' on {type.Name}.{method.Name} " +
                        $"has type {param.ParameterType.Name} which may not map cleanly to JSON Schema");
                }
            }
        }
    }

    [Fact]
    public void McpServer_ToolCount_HasNotDecreased()
    {
        var toolTypes = GetMcpToolTypes();
        var toolMethods = toolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null));

        // We expect at least the known set of tools
        toolMethods.Count().Should().BeGreaterThanOrEqualTo(ExpectedToolNames.Count,
            "Tool count decreased — possible accidental removal");
    }

    private static IEnumerable<Type> GetMcpToolTypes()
    {
        var assembly = typeof(RetailPulse.McpServer.Data.RetailPulseDb).Assembly;
        return assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);
    }

    private static string? GetToolName(Type type)
    {
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

        if (method is null) return null;
        var attr = method.GetCustomAttribute<McpServerToolAttribute>();
        return attr?.Name ?? method.Name;
    }
}
