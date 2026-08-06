using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Moq;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Finding 6 — hub session-ownership binding. <c>JoinSession</c> previously added the caller to a
/// group named by an arbitrary, caller-supplied session id, so an anonymous attacker who knew or
/// guessed a victim's session id could subscribe to the victim's telemetry / streamed tokens.
///
/// These tests drive the real <see cref="TelemetryHub"/> and <see cref="StreamingHub"/> with two
/// distinct anonymous principals and prove the second subject cannot join the first subject's
/// session, while Entra callers keep their unchanged (unbound) behaviour.
/// </summary>
public sealed class AnonymousHubOwnershipTests
{
    private static ClaimsPrincipal Anonymous(string subject) =>
        new(new ClaimsIdentity(
            [
                new Claim(AnonymousCapabilityPolicy.ProviderClaimType, AnonymousCapabilityPolicy.ProviderName),
                new Claim(JwtRegisteredClaimNames.Sub, subject),
            ],
            authenticationType: "AnonymousTest"));

    private static ClaimsPrincipal Entra(string oid) =>
        new(new ClaimsIdentity([new Claim("oid", oid)], authenticationType: "EntraTest"));

    private static (TelemetryHub hub, Mock<IGroupManager> groups) NewTelemetryHub(
        ISessionOwnershipRegistry registry, ClaimsPrincipal user, string connectionId)
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctx = new Mock<HubCallerContext>();
        ctx.SetupGet(c => c.User).Returns(user);
        ctx.SetupGet(c => c.ConnectionId).Returns(connectionId);
        return (new TelemetryHub(registry) { Context = ctx.Object, Groups = groups.Object }, groups);
    }

    private static (StreamingHub hub, Mock<IGroupManager> groups) NewStreamingHub(
        ISessionOwnershipRegistry registry, ClaimsPrincipal user, string connectionId)
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctx = new Mock<HubCallerContext>();
        ctx.SetupGet(c => c.User).Returns(user);
        ctx.SetupGet(c => c.ConnectionId).Returns(connectionId);
        return (new StreamingHub(registry) { Context = ctx.Object, Groups = groups.Object }, groups);
    }

    [Fact]
    public async Task TelemetryHub_SecondAnonymousSubject_CannotJoinFirstSubjectsSession()
    {
        var registry = new SessionOwnershipRegistry();
        const string sessionId = "victim-session-123";

        (TelemetryHub ownerHub, Mock<IGroupManager> ownerGroups) = NewTelemetryHub(registry, Anonymous("anon-A"), "conn-A");
        await ownerHub.JoinSession(sessionId);
        ownerGroups.Verify(g => g.AddToGroupAsync("conn-A", sessionId, It.IsAny<CancellationToken>()), Times.Once);

        (TelemetryHub attackerHub, Mock<IGroupManager> attackerGroups) = NewTelemetryHub(registry, Anonymous("anon-B"), "conn-B");
        Func<Task> join = () => attackerHub.JoinSession(sessionId);

        await join.Should().ThrowAsync<HubException>("a different anonymous subject must not join another subject's session");
        attackerGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StreamingHub_SecondAnonymousSubject_CannotJoinFirstSubjectsSession()
    {
        var registry = new SessionOwnershipRegistry();
        const string sessionId = "victim-session-456";

        (StreamingHub ownerHub, Mock<IGroupManager> ownerGroups) = NewStreamingHub(registry, Anonymous("anon-A"), "conn-A");
        await ownerHub.JoinSession(sessionId);
        ownerGroups.Verify(g => g.AddToGroupAsync("conn-A", $"stream:{sessionId}", It.IsAny<CancellationToken>()), Times.Once);

        (StreamingHub attackerHub, Mock<IGroupManager> attackerGroups) = NewStreamingHub(registry, Anonymous("anon-B"), "conn-B");
        Func<Task> join = () => attackerHub.JoinSession(sessionId);

        await join.Should().ThrowAsync<HubException>("a different anonymous subject must not join another subject's streamed tokens");
        attackerGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Hub_SameAnonymousSubject_CanRejoinOwnSession()
    {
        var registry = new SessionOwnershipRegistry();
        const string sessionId = "own-session";

        (TelemetryHub hub1, _) = NewTelemetryHub(registry, Anonymous("anon-A"), "conn-A1");
        await hub1.JoinSession(sessionId);

        (TelemetryHub hub2, Mock<IGroupManager> groups2) = NewTelemetryHub(registry, Anonymous("anon-A"), "conn-A2");
        await hub2.JoinSession(sessionId);
        groups2.Verify(g => g.AddToGroupAsync("conn-A2", sessionId, It.IsAny<CancellationToken>()), Times.Once,
            "the owning subject may rejoin from another connection");
    }

    [Fact]
    public async Task Hub_EntraCaller_IsUnbound_AndCanJoinAnySession()
    {
        var registry = new SessionOwnershipRegistry();
        const string sessionId = "anon-owned-session";

        // An anonymous subject claims the session first.
        (TelemetryHub anonHub, _) = NewTelemetryHub(registry, Anonymous("anon-A"), "conn-A");
        await anonHub.JoinSession(sessionId);

        // Entra behaviour is unchanged — no ownership binding is enforced.
        (TelemetryHub entraHub, Mock<IGroupManager> entraGroups) = NewTelemetryHub(registry, Entra("entra-oid-1"), "conn-E");
        await entraHub.JoinSession(sessionId);
        entraGroups.Verify(g => g.AddToGroupAsync("conn-E", sessionId, It.IsAny<CancellationToken>()), Times.Once,
            "Entra callers retain their original hub semantics");
    }

    [Fact]
    public async Task TelemetryHub_JoinCard_IsForbidden_ForAnonymous_ButAllowedForEntra()
    {
        var registry = new SessionOwnershipRegistry();

        (TelemetryHub anonHub, Mock<IGroupManager> anonGroups) = NewTelemetryHub(registry, Anonymous("anon-A"), "conn-A");
        Func<Task> anonJoinCard = () => anonHub.JoinCard("card-1");
        await anonJoinCard.Should().ThrowAsync<HubException>("cards/approvals are not part of the anonymous surface");
        anonGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        (TelemetryHub entraHub, Mock<IGroupManager> entraGroups) = NewTelemetryHub(registry, Entra("entra-oid-1"), "conn-E");
        await entraHub.JoinCard("card-1");
        entraGroups.Verify(g => g.AddToGroupAsync("conn-E", "card:card-1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
