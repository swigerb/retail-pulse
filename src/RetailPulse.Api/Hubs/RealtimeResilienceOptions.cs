namespace RetailPulse.Api.Hubs;

/// <summary>
/// Real-time channel resilience knobs (issue #92). Governs both the SignalR
/// transport-level timers (keep-alive / client-timeout / handshake) and the
/// application-level heartbeat emitted by <see cref="HubHeartbeatBackgroundService"/>.
///
/// <para>The application heartbeat is defence-in-depth on top of the SignalR
/// keep-alive: transport pings satisfy proxies that reset connections mid-flight,
/// but they are opaque to the browser layer and cannot be asserted from a unit
/// test. An application-level heartbeat is an observable event on the same hub
/// clients already subscribe to, so the frontend can render a "connected /
/// stalled" indicator without probing transport internals.</para>
///
/// <para>Defaults target the shortest plausible intermediary idle timeout we
/// see in front of Container Apps (Azure Front Door / APIM at 240s idle,
/// corporate proxies frequently at 60s). A 15s keep-alive stays well under any
/// of those and matches the SignalR guidance that the client timeout be at
/// least double the keep-alive interval.</para>
/// </summary>
public sealed class RealtimeResilienceOptions
{
    public const string SectionName = "RealtimeResilience";

    /// <summary>
    /// Server -> client transport ping cadence. Bound to
    /// <c>HubOptions.KeepAliveInterval</c>. Default 15s.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Server-side client-timeout — how long the server waits without a
    /// client message before disconnecting the client. SignalR guidance is
    /// >= 2x KeepAliveInterval so a single missed ping does not sever the
    /// connection. Default 30s.
    /// </summary>
    public TimeSpan ClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Handshake timeout for the initial protocol negotiation. Default 15s.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Application-level heartbeat cadence. Distinct from the transport
    /// keep-alive so the frontend can render a "we're still here" signal on
    /// the hub itself and tests can assert its cadence. Default 15s.
    /// </summary>
    public TimeSpan ApplicationHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Application heartbeat master switch. Turn off to disable the hosted
    /// heartbeat emitter without dropping the transport-level keep-alive.
    /// Default true.
    /// </summary>
    public bool ApplicationHeartbeatEnabled { get; set; } = true;

    /// <summary>
    /// Validates the SignalR guidance <c>ClientTimeoutInterval &gt;= 2 * KeepAliveInterval</c>.
    /// Returned so <see cref="Program"/> can log a startup warning without
    /// blocking boot on operator misconfiguration.
    /// </summary>
    public bool IsClientTimeoutSafe =>
        ClientTimeoutInterval >= KeepAliveInterval + KeepAliveInterval;
}
