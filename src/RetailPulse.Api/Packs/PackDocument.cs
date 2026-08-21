using RetailPulse.Contracts;

namespace RetailPulse.Api.Packs;

/// <summary>
/// In-memory shape of a pack's <c>pack.yaml</c>. The metadata block sits
/// alongside the tenant configuration so a single file is the source of
/// truth for who the pack is (metadata) and what business it represents
/// (tenant).
/// </summary>
public sealed class PackDocument
{
    public PackMetadata Metadata { get; init; } = new();
    public TenantConfiguration Tenant { get; init; } = new();
}
