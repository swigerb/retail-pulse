using Microsoft.Extensions.AI;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Auth;

/// <summary>
/// Applies Anonymous-mode constraints to a chat/agent execution: it strips write-capable tools so
/// the model can never invoke a mutation, and it caps model output tokens. It is consulted by the
/// <c>AgentExecutionPipeline</c> at every point where the tool set and chat options are assembled.
///
/// The decision is made from the CURRENT request principal (resolved via
/// <see cref="IHttpContextAccessor"/>): only an authenticated Anonymous session is constrained.
/// Any other principal (Entra) is unaffected — this is a strict narrowing that never grants
/// anything. When no policy is registered (non-Anonymous modes) the pipeline treats tools/options
/// as passthrough.
/// </summary>
public interface IAnonymousChatPolicy
{
    /// <summary>True when the current request is an authenticated anonymous session.</summary>
    bool AppliesToCurrentRequest { get; }

    /// <summary>
    /// True when the response cache must be bypassed for the current request. Anonymous sessions
    /// disable the cache entirely (both read and write) so identical prompts from two different
    /// subjects always execute independently and no reply is ever shared across subjects — the
    /// shared cache key excluded the subject, so disabling is the fail-safe choice for Sprint 1.
    /// </summary>
    bool CacheDisabled { get; }

    /// <summary>
    /// True when conversation memory (recall + extraction) must be disabled for the current
    /// request. Anonymous sessions disable memory entirely in Sprint 1 to eliminate any stored
    /// cross-prompt injection surface; there is no durable, accountable identity to key it to.
    /// </summary>
    bool MemoryDisabled { get; }

    /// <summary>
    /// Returns the tool set the current principal may use. For anonymous sessions the
    /// write-capable tools are removed; for everyone else the input set is returned unchanged.
    /// </summary>
    IEnumerable<AITool> FilterTools(IEnumerable<AITool> tools);

    /// <summary>Model output-token cap for the current principal, or null for no anonymous cap.</summary>
    int? MaxOutputTokens { get; }
}

/// <summary>
/// Active Anonymous chat policy. Registered only when <c>Authentication:Mode=Anonymous</c>.
/// </summary>
public sealed class AnonymousChatPolicy : IAnonymousChatPolicy
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AnonymousAuthOptions _options;

    public AnonymousChatPolicy(IHttpContextAccessor httpContextAccessor, AnonymousAuthOptions options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    public bool AppliesToCurrentRequest =>
        AnonymousCapabilityPolicy.IsAnonymousPrincipal(_httpContextAccessor.HttpContext?.User);

    public bool CacheDisabled => AppliesToCurrentRequest;

    public bool MemoryDisabled => AppliesToCurrentRequest;

    public IEnumerable<AITool> FilterTools(IEnumerable<AITool> tools) =>
        !AppliesToCurrentRequest
            ? tools
            : tools.Where(t =>
                !(t is AIFunction fn && AnonymousCapabilityPolicy.WriteCapableToolNames.Contains(fn.Name)));

    public int? MaxOutputTokens => AppliesToCurrentRequest ? _options.MaxOutputTokens : null;
}

/// <summary>
/// Null-safe helpers so the execution pipeline can apply the anonymous policy uniformly whether or
/// not one is registered. When the policy is null (non-Anonymous modes) tools pass through
/// unchanged and no output cap is applied.
/// </summary>
public static class AnonymousChatPolicyExtensions
{
    public static IEnumerable<AITool> ApplyToolFilter(this IAnonymousChatPolicy? policy, IEnumerable<AITool> tools) =>
        policy is null ? tools : policy.FilterTools(tools);

    /// <summary>True when the cache must be bypassed for the current request (Anonymous). Null-safe.</summary>
    public static bool IsCacheDisabled(this IAnonymousChatPolicy? policy) => policy?.CacheDisabled ?? false;

    /// <summary>True when conversation memory must be disabled for the current request (Anonymous). Null-safe.</summary>
    public static bool IsMemoryDisabled(this IAnonymousChatPolicy? policy) => policy?.MemoryDisabled ?? false;

    public static void ApplyOutputCap(this IAnonymousChatPolicy? policy, ChatOptions options)
    {
        int? cap = policy?.MaxOutputTokens;
        if (cap is int max && (options.MaxOutputTokens is null || options.MaxOutputTokens > max))
        {
            options.MaxOutputTokens = max;
        }
    }
}
