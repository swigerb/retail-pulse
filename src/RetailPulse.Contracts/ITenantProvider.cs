namespace RetailPulse.Contracts;

public interface ITenantProvider
{
    TenantConfiguration GetTenant();
}
