using RetailPulse.Contracts;

namespace RetailPulse.Api.Packs;

/// <summary>
/// <see cref="ITenantProvider"/> backed by a <see cref="LoadedPack"/>.
/// Every downstream consumer that used to read <c>tenant.yaml</c> now
/// reads the tenant declared inside the active pack, so a pack switch
/// automatically flows into every tenant-shaped surface (prompt
/// hydration, MCP data seeding, tenant-facing endpoints).
/// </summary>
public sealed class PackTenantProvider : ITenantProvider
{
    private readonly TenantConfiguration _tenant;

    public PackTenantProvider(LoadedPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _tenant = pack.Tenant;
    }

    public TenantConfiguration GetTenant() => _tenant;
}
