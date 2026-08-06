using System.Security.Claims;
using FluentAssertions;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Security;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests for the provider-neutral <see cref="NormalizedPrincipal"/> and the
/// <see cref="EntraPrincipalNormalizer"/> claim mapping. These prove the normalized identity
/// model is populated correctly from Entra claims without changing the authorization contract.
/// </summary>
public sealed class NormalizedPrincipalTests
{
    private const string Oid = "44444444-4444-4444-4444-444444444444";

    private static readonly EntraPrincipalNormalizer Normalizer =
        new(new EntraAuthOptions { TenantId = "t", ClientId = "c" });

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public void Normalize_MapsProviderSubjectDisplayRolesAndScopes()
    {
        ClaimsPrincipal principal = Principal(
            new Claim("oid", Oid),
            new Claim("name", "Ada Lovelace"),
            new Claim("roles", "RetailPulse.User"),
            new Claim("scp", "access_as_user Files.Read"));

        NormalizedPrincipal normalized = Normalizer.Normalize(principal);

        normalized.Provider.Should().Be("Entra");
        normalized.Subject.Should().Be(Oid, "the immutable oid is the stable subject");
        normalized.DisplayName.Should().Be("Ada Lovelace");
        normalized.Roles.Should().Contain("RetailPulse.User");
        normalized.Scopes.Should().Contain("access_as_user");
        normalized.Scopes.Should().Contain("Files.Read");
    }

    [Fact]
    public void Normalize_UsesMsSchemaObjectIdentifier_ForSubject()
    {
        ClaimsPrincipal principal = Principal(
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", Oid));

        NormalizedPrincipal normalized = Normalizer.Normalize(principal);

        normalized.Subject.Should().Be(Oid);
    }

    [Fact]
    public void Normalize_UsesLongSchemaScopeClaim()
    {
        ClaimsPrincipal principal = Principal(
            new Claim("oid", Oid),
            new Claim("http://schemas.microsoft.com/identity/claims/scope", "access_as_user"));

        NormalizedPrincipal normalized = Normalizer.Normalize(principal);

        normalized.Scopes.Should().ContainSingle().Which.Should().Be("access_as_user");
    }

    [Fact]
    public void Normalize_NoIdentityClaims_SubjectFallsBackToAnonymous()
    {
        NormalizedPrincipal normalized = Normalizer.Normalize(Principal());

        normalized.Subject.Should().Be(UserIdentity.AnonymousUserId);
        normalized.DisplayName.Should().BeNull();
        normalized.Roles.Should().BeEmpty();
        normalized.Scopes.Should().BeEmpty();
    }

    [Fact]
    public void Normalize_DeduplicatesRolesAcrossClaimTypes()
    {
        ClaimsPrincipal principal = Principal(
            new Claim("oid", Oid),
            new Claim("roles", "RetailPulse.User"),
            new Claim(ClaimTypes.Role, "RetailPulse.User"));

        NormalizedPrincipal normalized = Normalizer.Normalize(principal);

        normalized.Roles.Should().ContainSingle().Which.Should().Be("RetailPulse.User");
    }

    [Fact]
    public void Normalizer_ReportsEntraMode() => Normalizer.Mode.Should().Be(AuthenticationMode.Entra);

    [Fact]
    public void Normalize_NullPrincipal_Throws()
    {
        Action act = () => Normalizer.Normalize(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
