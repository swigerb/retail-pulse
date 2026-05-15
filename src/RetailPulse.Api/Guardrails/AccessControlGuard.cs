namespace RetailPulse.Api.Guardrails;

/// <summary>
/// Brand/region-scoped access control for multi-tenant guardrails.
/// Checks whether a user's allowed regions include the requested data scope.
/// </summary>
public class AccessControlGuard
{
    private readonly bool _enabled;

    public AccessControlGuard(bool enabled = true)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// Checks if a user may access data for the given region.
    /// Admins (role "admin") always pass. When access control is disabled, all pass.
    /// </summary>
    public AccessControlResult CheckAccess(UserScope user, string requestedRegion)
    {
        return !_enabled
            ? AccessControlResult.Allowed()
            : string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            ? AccessControlResult.Allowed()
            : user.AllowedRegions.Any(r =>
                string.Equals(r, requestedRegion, StringComparison.OrdinalIgnoreCase))
            ? AccessControlResult.Allowed()
            : AccessControlResult.Denied(
            $"You don't have access to {requestedRegion} data. " +
            $"Your access is limited to: {string.Join(", ", user.AllowedRegions)}.");
    }
}

/// <summary>
/// Represents a user's access scope.
/// </summary>
public record UserScope(string UserId, string Role, IReadOnlyList<string> AllowedRegions);

/// <summary>
/// Result of an access control check.
/// </summary>
public record AccessControlResult(bool IsAllowed, string? DenialMessage = null)
{
    public static AccessControlResult Allowed() => new(true);
    public static AccessControlResult Denied(string message) => new(false, message);
}
