namespace RetailPulse.Contracts.Guardrails;

/// <summary>
/// Runtime configuration for the load-time agent-definition safety validator
/// introduced by issue #99. Every prompt/tenant configuration is checked
/// against these rules before <c>AddAgentRouting</c> composes any agent, so a
/// hostile or mis-typed <c>prompts.yaml</c> never reaches a running deployment.
/// </summary>
/// <remarks>
/// Authentication material is intentionally absent — there is no
/// <c>ApiKey</c>, <c>SecretKey</c>, <c>Endpoint</c>, or credential property on
/// this type. The <c>AgentDefinitionPolicyContractTests</c> reflection test
/// enforces the absence of any such member.
/// </remarks>
public class AgentDefinitionPolicy
{
    /// <summary>
    /// How to react when a definition fails validation. <see cref="AgentDefinitionFailurePolicy.RefuseStartup"/>
    /// is the safe default — the host aborts with a single exception that
    /// enumerates every offender, matching the "never silently accept failures"
    /// requirement of issue #99.
    /// </summary>
    public AgentDefinitionFailurePolicy OnValidationFailure { get; set; } =
        AgentDefinitionFailurePolicy.RefuseStartup;

    /// <summary>
    /// Deployment-permitted model names. A definition whose <c>Model</c> is not
    /// in this list is rejected. Populate from operator config — never bake
    /// the list into code — so new models are enabled via configuration rather
    /// than a source change.
    /// </summary>
    public IReadOnlyList<string> AllowedModels { get; set; } = [];

    /// <summary>
    /// Optional deployment-scoped tool allow-list. When populated, every tool
    /// name a definition references must appear here even if it is a
    /// registered <c>AgentToolRegistry</c> name. When empty, the registered
    /// tool set is authoritative and this policy only enforces the
    /// <see cref="PrivilegedTools"/> grants below.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; set; } = [];

    /// <summary>
    /// Explicit grants for privileged / write-capable tools. A definition
    /// cannot self-assert access to a tool named here — its <c>Key</c> must
    /// appear in the tool's <see cref="PrivilegedToolGrant.GrantedTo"/> list.
    /// The shipped default grants <c>RequestApproval</c> to <c>promo-planning</c>
    /// only.
    /// </summary>
    public IReadOnlyList<PrivilegedToolGrant> PrivilegedTools { get; set; } = [];

    /// <summary>
    /// Master switch for the Content Safety second pass on definition text.
    /// Structural + pattern-layer checks always run; when this is <c>false</c>
    /// (or the global Content Safety layer is disabled), the model-based check
    /// is skipped entirely — no <c>EvaluateAsync</c> call is made and no
    /// content-safety audit row is emitted.
    /// </summary>
    public bool SafetyChecksEnabled { get; set; } = true;

    /// <summary>Inclusive temperature bounds enforced on every definition.</summary>
    public TemperatureBounds TemperatureBounds { get; set; } = new();

    /// <summary>Maximum permitted length of <c>SystemPrompt</c> in characters.</summary>
    public int MaxSystemPromptLength { get; set; } = 32_000;

    /// <summary>Maximum permitted length of each <c>KeywordFastPaths</c> entry in characters.</summary>
    public int MaxKeywordFastPathLength { get; set; } = 128;
}

/// <summary>Failure policy applied by the load-time validator.</summary>
public enum AgentDefinitionFailurePolicy
{
    /// <summary>Throw a single aggregated exception and refuse to start the host.</summary>
    RefuseStartup = 0,

    /// <summary>
    /// Remove offending definitions from the loaded configuration, log a loud
    /// warning, and continue startup. Every quarantine is still audited.
    /// </summary>
    QuarantineOffender = 1,
}

/// <summary>
/// Grant of a privileged tool to a specific set of agent keys. Definitions
/// whose <c>Key</c> is not in <see cref="GrantedTo"/> cannot list
/// <see cref="Tool"/> in their <c>tools:</c> array.
/// </summary>
public sealed class PrivilegedToolGrant
{
    /// <summary>Tool name (matches the registered <c>AgentToolRegistry</c> factory key).</summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>Agent keys explicitly permitted to reference <see cref="Tool"/>.</summary>
    public IReadOnlyList<string> GrantedTo { get; set; } = [];
}

/// <summary>Inclusive numeric bounds used for structural temperature validation.</summary>
public sealed class TemperatureBounds
{
    /// <summary>Minimum permitted temperature (inclusive).</summary>
    public double Min { get; set; }

    /// <summary>Maximum permitted temperature (inclusive).</summary>
    public double Max { get; set; } = 1.0;
}
