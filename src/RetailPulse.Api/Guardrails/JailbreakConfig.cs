namespace RetailPulse.Api.Guardrails;

/// <summary>
/// Configuration for jailbreak detection patterns.
/// Decoupled from the broader GuardrailsConfig in Middleware.
/// </summary>
public record JailbreakConfig(string[] Patterns);
