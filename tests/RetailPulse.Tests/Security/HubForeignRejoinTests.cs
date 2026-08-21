using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RetailPulse.Api.Hubs;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Security guardrail for issue #92: a hostile client must NOT be able to
/// rejoin another subject's session group after a reconnect. Ownership is
/// enforced for BOTH authenticated and anonymous callers on the telemetry AND
/// the streaming hub, and the same registry is consulted by both — a rejoin
/// with a foreign sessionId is refused with <see cref="HubException"/>.
/// </summary>
public sealed class HubForeignRejoinTests
{
    private static ClaimsPrincipal Entra(string oid) =>
        new(new ClaimsIdentity([new Claim("oid", oid)], authenticationType: "EntraTest"));

    private static ClaimsPrincipal Anonymous(string sub)
    {
        // Mirror the Anonymous provider shape (see AnonymousCapabilityPolicy):
        // an authenticated identity with only the "sub" claim.
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", sub),
                new Claim("provider", "Anonymous"),
            ],
            authenticationType: "Anonymous");
        return new ClaimsPrincipal(identity);
    }

    private static Mock<HubCallerContext> Ctx(ClaimsPrincipal user, string connectionId)
    {
        var ctx = new Mock<HubCallerContext>();
        ctx.SetupGet(c => c.User).Returns(user);
        ctx.SetupGet(c => c.ConnectionId).Returns(connectionId);
        return ctx;
    }

    private static Mock<IGroupManager> Groups()
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return groups;
    }

    [Fact]
    public async Task TelemetryHub_AuthenticatedAttacker_ForeignSession_IsRefused()
    {
        var registry = new SessionOwnershipRegistry();
        const string victimSession = "victim-session-123";

        // Victim (Entra "oid-A") legitimately joins their session first.
        var victimHub = new TelemetryHub(registry)
        {
            Context = Ctx(Entra("oid-A"), "victim-conn").Object,
            Groups = Groups().Object,
        };
        await victimHub.JoinSession(victimSession);

        // Attacker (Entra "oid-B") reconnects and attempts to rejoin the
        // victim's session id. Must be refused with HubException.
        Mock<IGroupManager> attackerGroups = Groups();
        var attackerHub = new TelemetryHub(registry)
        {
            Context = Ctx(Entra("oid-B"), "attacker-conn").Object,
            Groups = attackerGroups.Object,
        };

        Func<Task> act = () => attackerHub.JoinSession(victimSession);
        await act.Should().ThrowAsync<HubException>();

        attackerGroups.Verify(
            g => g.AddToGroupAsync("attacker-conn", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "attacker must never be added to any group when the join is refused");
        registry.OwnerOf(victimSession).Should().Be("oid-A");
    }

    [Fact]
    public async Task StreamingHub_AuthenticatedAttacker_ForeignSession_IsRefused()
    {
        var registry = new SessionOwnershipRegistry();
        const string victimSession = "victim-stream-abc";

        var victimHub = new StreamingHub(registry)
        {
            Context = Ctx(Entra("oid-A"), "victim-conn").Object,
            Groups = Groups().Object,
        };
        await victimHub.JoinSession(victimSession);

        Mock<IGroupManager> attackerGroups = Groups();
        var attackerHub = new StreamingHub(registry)
        {
            Context = Ctx(Entra("oid-B"), "attacker-conn").Object,
            Groups = attackerGroups.Object,
        };

        Func<Task> act = () => attackerHub.JoinSession(victimSession);
        await act.Should().ThrowAsync<HubException>();

        attackerGroups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TelemetryHub_AnonymousHardening_Preserved()
    {
        var registry = new SessionOwnershipRegistry();
        const string victimSession = "anon-victim";

        var victimHub = new TelemetryHub(registry)
        {
            Context = Ctx(Anonymous("anon-sub-A"), "victim-conn").Object,
            Groups = Groups().Object,
        };
        await victimHub.JoinSession(victimSession);

        var attackerHub = new TelemetryHub(registry)
        {
            Context = Ctx(Anonymous("anon-sub-B"), "attacker-conn").Object,
            Groups = Groups().Object,
        };

        Func<Task> act = () => attackerHub.JoinSession(victimSession);
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task TelemetryHub_LegitimateReconnect_ByOwner_Succeeds()
    {
        var registry = new SessionOwnershipRegistry();
        const string sessionId = "owned-session";

        Mock<IGroupManager> firstGroups = Groups();
        var firstConnect = new TelemetryHub(registry)
        {
            Context = Ctx(Entra("oid-owner"), "conn-1").Object,
            Groups = firstGroups.Object,
        };
        await firstConnect.JoinSession(sessionId);

        // Reconnect: fresh connection id, same subject.
        Mock<IGroupManager> reconnectGroups = Groups();
        var reconnect = new TelemetryHub(registry)
        {
            Context = Ctx(Entra("oid-owner"), "conn-2").Object,
            Groups = reconnectGroups.Object,
        };
        await reconnect.JoinSession(sessionId);

        reconnectGroups.Verify(
            g => g.AddToGroupAsync("conn-2", sessionId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
