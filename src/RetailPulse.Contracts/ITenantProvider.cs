namespace RetailPulse.Contracts;

/// <summary>
/// Provides access to the current tenant configuration.
/// </summary>
public interface ITenantProvider
{
    /// <summary>
    /// Returns the loaded <see cref="TenantConfiguration"/> for the current tenant.
    /// </summary>
    TenantConfiguration GetTenant();
}
