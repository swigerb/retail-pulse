using Microsoft.Extensions.AI;

namespace RetailPulse.Api.Caching;

/// <summary>
/// Wraps AITool instances with transparent caching.
/// On invocation: checks cache first, falls through on miss, stores result after successful call.
/// </summary>
public class CachingToolWrapper
{
    private readonly ToolResultCache _cache;
    private readonly ILogger<CachingToolWrapper> _logger;

    // Tools that should never be cached (side effects)
    private static readonly HashSet<string> _excludedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreateChart",
        "RequestApproval",
        "AnalyzeShipments",
        "OptimizePlanogram",
    };

    public CachingToolWrapper(ToolResultCache cache, ILogger<CachingToolWrapper> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Wraps a single AITool with caching if eligible.
    /// Tools with side effects are returned unwrapped.
    /// </summary>
    public AITool Wrap(AITool tool)
    {
        if (tool is not AIFunction func || _excludedTools.Contains(func.Name))
            return tool;

        return new CachedAIFunction(func, _cache, _logger);
    }

    /// <summary>
    /// Wraps all eligible tools in a collection.
    /// </summary>
    public IList<AITool> WrapAll(IEnumerable<AITool> tools) =>
        tools.Select(Wrap).ToList();
}

/// <summary>
/// AIFunction wrapper that checks the tool result cache before invoking the underlying function.
/// </summary>
internal sealed class CachedAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly ToolResultCache _cache;
    private readonly ILogger _logger;

    public CachedAIFunction(AIFunction inner, ToolResultCache cache, ILogger logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var argDict = ExtractArguments(arguments);

        // Check cache
        var cached = _cache.TryGet(_inner.Name, argDict);
        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for tool {Tool} — returning cached result", _inner.Name);
            return cached;
        }

        // Cache miss — invoke the real tool
        var result = await _inner.InvokeAsync(arguments, cancellationToken);
        var resultStr = result?.ToString() ?? string.Empty;

        // Store in cache (validation happens inside Set)
        _cache.Set(_inner.Name, argDict, resultStr);

        return result;
    }

    private static IDictionary<string, object?> ExtractArguments(AIFunctionArguments args)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in args)
        {
            dict[kvp.Key] = kvp.Value;
        }
        return dict;
    }
}
