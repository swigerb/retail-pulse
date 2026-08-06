using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Sprint 1 mandated simplification: the SignalR hubs are NO LONGER part of the anonymous surface.
/// A valid anonymous token is denied (403) on BOTH hubs at both the negotiate and connection
/// endpoints — proven by the deny-by-default guard in
/// <see cref="AnonymousAuthenticationTests"/> and by the compiled endpoint-graph policy in
/// <see cref="EndpointAuthorizationCoverageTests"/>. Because anonymous callers can never reach a
/// hub method, the former anonymous hub session-ownership expectations are intentionally gone.
///
/// What remains here is a guardrail proving the mandate's other half: <b>Entra hub behaviour is
/// unchanged.</b> An Entra caller keeps its original (unbound) semantics on the real
/// <see cref="TelemetryHub"/>, so the scope reduction for anonymous did not regress the
/// authenticated real-time surface.
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
    public async Task TelemetryHub_EntraCaller_IsUnbound_AndCanJoinAnySession()
    {
        var registry = new SessionOwnershipRegistry();
        const string sessionId = "some-session";

        // Entra behaviour is unchanged by the anonymous scope reduction — no ownership binding is
        // enforced and the caller is added to the requested group as before.
        (TelemetryHub entraHub, Mock<IGroupManager> entraGroups) = NewTelemetryHub(registry, Entra("entra-oid-1"), "conn-E");
        await entraHub.JoinSession(sessionId);
        entraGroups.Verify(g => g.AddToGroupAsync("conn-E", sessionId, It.IsAny<CancellationToken>()), Times.Once,
            "Entra callers retain their original hub semantics");
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
