using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RetailPulse.Api.Hubs;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Hub session-ownership guardrails. As of issue #92 the ownership registry is
/// consulted for BOTH anonymous and authenticated callers so a hostile client
/// that reconnects and attempts to rejoin another subject's session group is
/// refused. First-writer-wins for a server-minted sessionId; every subsequent
/// join (including reconnects) must match the recorded owner.
///
/// <para>Card subscriptions remain deny-by-default for anonymous callers — the
/// card/approval surface is not part of the anonymous scope.</para>
/// </summary>
public sealed class AnonymousHubOwnershipTests
{
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

    [Fact]
    public async Task TelemetryHub_EntraCaller_FirstJoin_ClaimsOwnership()
    {
        var registry = new SessionOwnershipRegistry();
        const string sessionId = "some-session";

        // Ownership is now enforced for authenticated callers too (issue #92).
        // The first Entra join for a fresh sessionId claims ownership and
        // succeeds; a subsequent rejoin by the same subject also succeeds.
        (TelemetryHub entraHub, Mock<IGroupManager> entraGroups) = NewTelemetryHub(registry, Entra("entra-oid-1"), "conn-E");
        await entraHub.JoinSession(sessionId);
        entraGroups.Verify(g => g.AddToGroupAsync("conn-E", sessionId, It.IsAny<CancellationToken>()), Times.Once);

        // Rejoin from the same subject is allowed.
        await entraHub.JoinSession(sessionId);
        entraGroups.Verify(g => g.AddToGroupAsync("conn-E", sessionId, It.IsAny<CancellationToken>()), Times.Exactly(2));

        registry.OwnerOf(sessionId).Should().Be("entra-oid-1");
    }

    [Fact]
    public async Task TelemetryHub_EntraCaller_CanJoinCard()
    {
        var registry = new SessionOwnershipRegistry();

        (TelemetryHub entraHub, Mock<IGroupManager> entraGroups) = NewTelemetryHub(registry, Entra("entra-oid-1"), "conn-E");
        await entraHub.JoinCard("card-1");
        entraGroups.Verify(g => g.AddToGroupAsync("conn-E", "card:card-1", It.IsAny<CancellationToken>()), Times.Once,
            "Entra callers retain their original card-subscription semantics");
    }
}
