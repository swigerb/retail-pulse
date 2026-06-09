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
    public void Resolve_PrefersBodyObjectId_OverClaim()
    {
        ClaimsPrincipal principal = PrincipalWithOid("00000000-0000-0000-0000-000000000000");

        string id = UserIdentity.Resolve(principal, bodyObjectId: "user-from-body");

        id.Should().Be("user-from-body");
    }

    [Fact]
    public void Resolve_FallsBackToOidClaim_WhenBodyMissing()
    {
        ClaimsPrincipal principal = PrincipalWithOid("00000000-0000-0000-0000-000000000000");

        string id = UserIdentity.Resolve(principal, bodyObjectId: null);

        id.Should().Be("00000000-0000-0000-0000-000000000000");
    }

    [Fact]
    public void Resolve_FallsBackToOidClaim_WhenBodyIsWhitespace()
    {
        ClaimsPrincipal principal = PrincipalWithOid("real-oid");

        string id = UserIdentity.Resolve(principal, bodyObjectId: "   ");

        id.Should().Be("real-oid");
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
