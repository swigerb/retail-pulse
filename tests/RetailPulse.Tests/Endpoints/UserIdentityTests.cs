using System.Security.Claims;
using FluentAssertions;
using RetailPulse.Api.Auth;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Regression net for the Memory Panel "0 memories" bug (Costco, 2026-06-04).
/// Write paths and read paths MUST resolve userId identically — these tests
/// pin the resolution priority so future endpoints can't quietly diverge.
/// </summary>
public class UserIdentityTests
{
    [Fact]
    public void Resolve_PrefersOidClaim_OverBodyObjectId()
    {
        ClaimsPrincipal principal = PrincipalWithOid("claim-oid");

        string id = UserIdentity.Resolve(principal, bodyObjectId: "spoofed-user-from-body");

        id.Should().Be("claim-oid",
            "authenticated claim must take priority to prevent request-body spoofing");
    }

    [Fact]
    public void Resolve_UsesBodyObjectId_WhenNoClaim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Alice")])); // No oid claim

        string id = UserIdentity.Resolve(principal, bodyObjectId: "body-fallback");

        id.Should().Be("body-fallback");
    }

    [Fact]
    public void Resolve_PrefersOidClaim_EvenWhenBodyIsNonWhitespace()
    {
        ClaimsPrincipal principal = PrincipalWithOid("real-oid");

        string id = UserIdentity.Resolve(principal, bodyObjectId: "non-empty-body");

        id.Should().Be("real-oid",
            "claim takes priority even when body is present");
    }

    [Fact]
    public void Resolve_AcceptsMicrosoftSchemaObjectIdentifier()
    {
        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(
            "http://schemas.microsoft.com/identity/claims/objectidentifier",
            "schema-oid"));

        string id = UserIdentity.Resolve(new ClaimsPrincipal(identity));

        id.Should().Be("schema-oid");
    }

    [Fact]
    public void Resolve_ReturnsAnonymous_WhenNothingAvailable()
    {
        string id = UserIdentity.Resolve(principal: null, bodyObjectId: null);

        id.Should().Be(UserIdentity.AnonymousUserId);
        id.Should().Be("anonymous");
    }

    [Fact]
    public void Resolve_ReturnsAnonymous_WhenPrincipalHasNoOidClaim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Alice")]));

        string id = UserIdentity.Resolve(principal);

        id.Should().Be(UserIdentity.AnonymousUserId);
    }

    [Fact]
    public void Resolve_AgreesBetweenWriteAndReadPaths_UnderDevAuth()
    {
        // Simulates the exact bug: dev-mode auth stamps oid=00000000-…, the
        // FE omits the User block on the request body. Both the chat write
        // path (passing request.User?.ObjectId == null) and the memory read
        // path (passing null) must resolve to the same string.
        ClaimsPrincipal devPrincipal = PrincipalWithOid("00000000-0000-0000-0000-000000000000");

        string writePath = UserIdentity.Resolve(devPrincipal, bodyObjectId: null);
        string readPath = UserIdentity.Resolve(devPrincipal, bodyObjectId: null);

        writePath.Should().Be(readPath,
            "memory writes and reads must land on the same userId or the panel will show 0 entries");
        writePath.Should().Be("00000000-0000-0000-0000-000000000000");
    }

    private static ClaimsPrincipal PrincipalWithOid(string oid)
    {
        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim("oid", oid));
        return new ClaimsPrincipal(identity);
    }
}
