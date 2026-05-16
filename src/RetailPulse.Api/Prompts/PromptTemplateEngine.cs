using RetailPulse.Api.Models;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Prompts;

/// <summary>
/// Centralizes tenant placeholder substitution for agent prompt definitions.
/// Replaces repetitive .Replace() chains with a single Hydrate() call.
/// </summary>
public sealed class PromptTemplateEngine
{
    private readonly Dictionary<string, string> _replacements;

    public PromptTemplateEngine(TenantConfiguration tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        _replacements = new Dictionary<string, string>
        {
            ["{tenant.company}"] = tenant.Company,
            ["{tenant.industry}"] = tenant.Industry,
            ["{tenant.distribution_model}"] = tenant.Distribution?.Model ?? "Three-Tier",
            ["{tenant.primary_color}"] = tenant.Theme?.PrimaryColor ?? "#1A73E8",
            ["{tenant.accent_color}"] = tenant.Theme?.AccentColor ?? "#FFC107",
            ["{tenant.brands}"] = string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")),
            ["{tenant.regions}"] = string.Join(", ", tenant.Regions)
        };
    }

    /// <summary>
    /// Hydrates all {tenant.*} placeholders in the agent definition's SystemPrompt.
    /// Modifies the definition in place and returns it for fluent usage.
    /// </summary>
    public AgentDefinition Hydrate(AgentDefinition agentDef)
    {
        ArgumentNullException.ThrowIfNull(agentDef);

        if (string.IsNullOrEmpty(agentDef.SystemPrompt))
            return agentDef;

        agentDef.SystemPrompt = HydrateTemplate(agentDef.SystemPrompt);
        return agentDef;
    }

    /// <summary>
    /// Hydrates all {tenant.*} placeholders in the given template string.
    /// </summary>
    public string HydrateTemplate(string template)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        foreach ((string? placeholder, string? value) in _replacements)
        {
            template = template.Replace(placeholder, value);
        }

        return template;
    }
}
