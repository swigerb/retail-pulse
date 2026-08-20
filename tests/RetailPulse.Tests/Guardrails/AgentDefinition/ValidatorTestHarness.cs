using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Tests.Guardrails.ContentSafety;
using AgentDefinition = RetailPulse.Api.Models.AgentDefinition;
using PromptConfiguration = RetailPulse.Api.Models.PromptConfiguration;

namespace RetailPulse.Tests.Guardrails.AgentDefinitions;

/// <summary>
/// Composition helpers for the load-time validator tests. Every test uses
/// the fake evaluator + in-memory audit log so no real Azure client is
/// touched, and the shared registry mirrors the runtime tool set.
/// </summary>
internal static class ValidatorTestHarness
{
    public static AgentToolRegistry RegistryWithDefaults()
    {
        var registry = new AgentToolRegistry();
        foreach (string name in AgentDefinitionValidatorToolCatalog.KnownToolNames)
        {
            registry.Register(name, _ => throw new NotSupportedException(
                "Test registry never resolves — only Contains / RegisteredNames is queried."));
        }
        return registry;
    }

    public static GuardrailsConfig DefaultConfig(
        AgentDefinitionFailurePolicy failurePolicy = AgentDefinitionFailurePolicy.RefuseStartup,
        bool safetyChecksEnabled = true,
        bool contentSafetyEnabled = true,
        ContentSafetyFailPolicy onUnavailable = ContentSafetyFailPolicy.FailClosed)
    {
        return new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = contentSafetyEnabled,
                OnUnavailable = onUnavailable,
                PromptShieldsEnabled = true,
                TimeoutMs = 1500,
            },
            AgentDefinition = new AgentDefinitionPolicy
            {
                OnValidationFailure = failurePolicy,
                AllowedModels = ["gpt-5.4-mini", "none", "gpt-4o"],
                AllowedTools = [],
                PrivilegedTools =
                [
                    new PrivilegedToolGrant
                    {
                        Tool = "RequestApproval",
                        GrantedTo = ["promo-planning"],
                    },
                ],
                SafetyChecksEnabled = safetyChecksEnabled,
                TemperatureBounds = new TemperatureBounds { Min = 0.0, Max = 1.0 },
                MaxSystemPromptLength = 32_000,
                MaxKeywordFastPathLength = 128,
            },
        };
    }

    public static PromptConfiguration BenignConfig()
    {
        var promptConfig = new PromptConfiguration();
        foreach (AgentDefinition def in LegitimateCorpus.Definitions)
        {
            promptConfig.Agents[def.Key] = def.Clone();
        }
        return promptConfig;
    }

    public static (AgentDefinitionValidator Validator, InMemorySuspiciousRequestLog Audit,
        FakeContentSafetyEvaluator ContentSafety, TestLogger<AgentDefinitionValidator> Logger)
        Build(GuardrailsConfig config, FakeContentSafetyEvaluator? evaluator = null)
    {
        evaluator ??= new FakeContentSafetyEvaluator();
        var audit = new InMemorySuspiciousRequestLog(maxEntries: 500);
        var logger = new TestLogger<AgentDefinitionValidator>();
        var validator = new AgentDefinitionValidator(
            config,
            new JailbreakDetector(),
            evaluator,
            audit,
            RegistryWithDefaults(),
            logger);
        return (validator, audit, evaluator, logger);
    }

    public static AgentDefinition MakeAgent(string key, Action<AgentDefinition>? customize = null)
    {
        var def = new AgentDefinition
        {
            Key = key,
            Name = $"Agent-{key}",
            DisplayName = $"Display for {key}",
            Model = "gpt-5.4-mini",
            SystemPrompt = "You are a benign specialist. Do the requested analysis.",
            Temperature = 0.3,
            Role = "specialist",
            Tools = ["CreateChart"],
            Intents = [$"{key}/handle"],
        };
        customize?.Invoke(def);
        return def;
    }

    public static PromptConfiguration Configure(params AgentDefinition[] defs)
    {
        var config = new PromptConfiguration();
        foreach (AgentDefinition def in defs)
        {
            config.Agents[def.Key] = def;
        }
        return config;
    }
}

/// <summary>
/// Minimal <see cref="ILogger{T}"/> implementation that records the level +
/// formatted message for every log event. Used in place of
/// <c>Microsoft.Extensions.Logging.Testing.FakeLogger</c> which is not on the
/// test project's dependency list.
/// </summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
