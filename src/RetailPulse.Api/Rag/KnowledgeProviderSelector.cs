using Microsoft.Extensions.Options;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag;

/// <summary>
/// Deterministic, fail-fast resolution of the active knowledge provider from
/// configuration. Selection is intentionally explicit — the selector never
/// auto-detects a provider from ambient signals.
///
/// Resolution rules:
/// <list type="bullet">
///   <item>A missing or blank <see cref="KnowledgeProviderOptions.Mode"/> resolves
///     to <see cref="KnowledgeProviderMode.InMemory"/>. This is the documented
///     default and preserves the zero-cloud-dependency laptop demo.</item>
///   <item>A recognized mode (case-insensitive) is honored as-is.</item>
///   <item>An unknown / malformed value (including a bare number) throws with
///     an actionable message. Startup fails.</item>
///   <item>A missing or blank <see cref="KnowledgeProviderOptions.Degradation"/>
///     resolves to <see cref="KnowledgeDegradationMode.FailLoud"/>. Cloud
///     providers configured without a policy fail loudly rather than silently
///     dropping back — the operator opts in to fallback explicitly.</item>
/// </list>
/// This class only <i>resolves</i> and <i>materializes</i> the provider. The
/// startup health probe and query-time degradation live in
/// <see cref="DegradingKnowledgeBase"/>.
/// </summary>
public sealed class KnowledgeProviderSelector
{
    private readonly IOptions<KnowledgeProviderOptions> _options;
    private readonly KnowledgeProviderRegistry _registry;

    public KnowledgeProviderSelector(
        IOptions<KnowledgeProviderOptions> options,
        KnowledgeProviderRegistry registry)
    {
        _options = options;
        _registry = registry;
    }

    /// <summary>
    /// Resolves the configured provider mode. See class-level docs for defaults
    /// and error behavior.
    /// </summary>
    public KnowledgeProviderMode ResolveMode()
    {
        return ParseMode(_options.Value.Mode);
    }

    /// <summary>
    /// Resolves the configured degradation policy. See class-level docs for
    /// defaults and error behavior.
    /// </summary>
    public KnowledgeDegradationMode ResolveDegradation()
    {
        return ParseDegradation(_options.Value.Degradation);
    }

    /// <summary>
    /// Materializes the primary provider from the registry using the resolved
    /// mode. Delegates all failure modes (unknown mode, unregistered mode) to
    /// deterministic exceptions the operator can act on.
    /// </summary>
    public IKnowledgeBase CreatePrimary(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        KnowledgeProviderMode mode = ResolveMode();
        return _registry.Create(mode, services);
    }

    internal static KnowledgeProviderMode ParseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return KnowledgeProviderMode.InMemory;

        string trimmed = raw.Trim();

        // Reject bare numeric input so the mode is always a documented,
        // readable name and can never be selected by an accidental integer.
        return int.TryParse(trimmed, out _) ||
            !Enum.TryParse(trimmed, ignoreCase: true, out KnowledgeProviderMode mode) ||
            !Enum.IsDefined(mode)
            ? throw new InvalidOperationException(
                $"{KnowledgeProviderOptions.ModeKey} '{raw}' is not a recognized knowledge provider mode. " +
                $"Valid modes are: {string.Join(", ", Enum.GetNames<KnowledgeProviderMode>())}. " +
                "Leave the value blank to use the default in-memory provider.")
            : mode;
    }

    internal static KnowledgeDegradationMode ParseDegradation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return KnowledgeDegradationMode.FailLoud;

        string trimmed = raw.Trim();

        return int.TryParse(trimmed, out _) ||
            !Enum.TryParse(trimmed, ignoreCase: true, out KnowledgeDegradationMode mode) ||
            !Enum.IsDefined(mode)
            ? throw new InvalidOperationException(
                $"{KnowledgeProviderOptions.DegradationKey} '{raw}' is not a recognized degradation policy. " +
                $"Valid policies are: {string.Join(", ", Enum.GetNames<KnowledgeDegradationMode>())}. " +
                "Leave the value blank to use the default (FailLoud) — the platform NEVER silently returns " +
                "an empty result when the configured provider is unreachable.")
            : mode;
    }
}
