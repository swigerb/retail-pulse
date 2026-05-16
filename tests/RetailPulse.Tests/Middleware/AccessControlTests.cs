using FluentAssertions;
using RetailPulse.Api.Guardrails;

namespace RetailPulse.Tests.Middleware;

/// <summary>
/// Tests for AccessControlGuard — brand/region-scoped access control.
/// Covers: allowed region access, denied region access, admin override,
/// disabled access control, friendly denial messages.
/// </summary>
public class AccessControlTests
{
    private static UserScope MakeUser(string role, params string[] regions)
        => new("user-1", role, regions);

    #region Allowed Access

    [Fact]
    public void CheckAccess_UserWithRegionAccess_Allowed()
    {
        var guard = new AccessControlGuard(enabled: true);
        UserScope user = MakeUser("analyst", "Southeast", "Northeast");

        AccessControlResult result = guard.CheckAccess(user, "Southeast");

        result.IsAllowed.Should().BeTrue();
        result.DenialMessage.Should().BeNull();
    }

    [Fact]
    public void CheckAccess_UserWithMultipleRegions_AllAllowed()
    {
        var guard = new AccessControlGuard(enabled: true);
        UserScope user = MakeUser("analyst", "Southeast", "Northeast", "Midwest");

        guard.CheckAccess(user, "Southeast").IsAllowed.Should().BeTrue();
        guard.CheckAccess(user, "Northeast").IsAllowed.Should().BeTrue();
        guard.CheckAccess(user, "Midwest").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void CheckAccess_CaseInsensitive_Allowed()
    {
        var guard = new AccessControlGuard(enabled: true);
        UserScope user = MakeUser("analyst", "Southeast");

        guard.CheckAccess(user, "southeast").IsAllowed.Should().BeTrue();
        guard.CheckAccess(user, "SOUTHEAST").IsAllowed.Should().BeTrue();
    }

    #endregion

    #region Denied Access

    [Fact]
    public void CheckAccess_UserWithoutRegionAccess_Denied()
    {
        var guard = new AccessControlGuard(enabled: true);
        UserScope user = MakeUser("analyst", "Southeast");

        AccessControlResult result = guard.CheckAccess(user, "Northwest");

        result.IsAllowed.Should().BeFalse();
        result.DenialMessage.Should().NotBeNull();
    }

    [Fact]
    public void CheckAccess_DenialMessage_IsFriendly()
    {
        var guard = new AccessControlGuard(enabled: true);
        UserScope user = MakeUser("analyst", "Southeast");

        AccessControlResult result = guard.CheckAccess(user, "Northwest");

        result.DenialMessage.Should().Contain("Northwest");
        result.DenialMessage.Should().Contain("Southeast");
        result.DenialMessage.Should().NotContain("error");
        result.DenialMessage.Should().NotContain("403");
        result.DenialMessage.Should().NotContain("unauthorized");
    }

    [Fact]
    public void CheckAccess_UserWithNoRegions_AllDenied()
    {
        var guard = new AccessControlGuard(enabled: true);
        UserScope user = MakeUser("analyst");

        AccessControlResult result = guard.CheckAccess(user, "Southeast");
        result.IsAllowed.Should().BeFalse();
    }

    #endregion

    #region Admin Override

    [Fact]
    public void CheckAccess_Admin_AlwaysAllowed()
    {
        var guard = new AccessControlGuard(enabled: true);
        UserScope admin = MakeUser("admin"); // no explicit regions

        guard.CheckAccess(admin, "Southeast").IsAllowed.Should().BeTrue();
        guard.CheckAccess(admin, "Northwest").IsAllowed.Should().BeTrue();
        guard.CheckAccess(admin, "National").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void CheckAccess_AdminRole_CaseInsensitive()
    {
        var guard = new AccessControlGuard(enabled: true);
        UserScope admin = MakeUser("Admin");

        guard.CheckAccess(admin, "Southeast").IsAllowed.Should().BeTrue();
    }

    #endregion

    #region Disabled Access Control

    [Fact]
    public void CheckAccess_Disabled_AllQueriesAllowed()
    {
        var guard = new AccessControlGuard(enabled: false);
        UserScope user = MakeUser("analyst", "Southeast");

        // User only has Southeast, but access control is disabled
        guard.CheckAccess(user, "Northwest").IsAllowed.Should().BeTrue();
        guard.CheckAccess(user, "National").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void CheckAccess_Disabled_NoRegions_StillAllowed()
    {
        var guard = new AccessControlGuard(enabled: false);
        UserScope user = MakeUser("analyst"); // no regions at all

        guard.CheckAccess(user, "Southeast").IsAllowed.Should().BeTrue();
    }

    #endregion
}
