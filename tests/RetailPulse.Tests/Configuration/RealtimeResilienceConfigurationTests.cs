using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Hubs;

namespace RetailPulse.Tests.Configuration;

/// <summary>
/// Backend contract for issue #92: RealtimeResilience + ChatTimeout config
/// bind through the standard <see cref="IOptions{TOptions}"/> pattern, defaults
/// preserve the pre-#92 single-shot behavior, and the plan ceiling is
/// distinct so long-running plans don't force us to globally raise 90s.
/// </summary>
public sealed class RealtimeResilienceConfigurationTests
{
    private static IServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<RealtimeResilienceOptions>(
            configuration.GetSection(RealtimeResilienceOptions.SectionName));
        services.Configure<ChatTimeoutOptions>(
            configuration.GetSection(ChatTimeoutOptions.SectionName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_PreserveSingleShot90s_AndProvideDistinctPlanCeiling()
    {
        IServiceProvider sp = BuildServices([]);

        ChatTimeoutOptions timeouts = sp.GetRequiredService<IOptions<ChatTimeoutOptions>>().Value;

        timeouts.SingleShot.Should().Be(TimeSpan.FromSeconds(90),
            "the default single-shot ceiling must match the pre-#92 hard-coded value");
        timeouts.Plan.Should().BeGreaterThan(timeouts.SingleShot,
            "the plan ceiling exists so long-running plans don't have to widen the fast-path wall");
    }

    [Fact]
    public void Defaults_ProvideConservativeHeartbeat_AndSafeClientTimeoutRatio()
    {
        IServiceProvider sp = BuildServices([]);

        RealtimeResilienceOptions opts = sp.GetRequiredService<IOptions<RealtimeResilienceOptions>>().Value;

        opts.KeepAliveInterval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(15),
            "keepalive must stay under the shortest plausible intermediary idle timeout");
        opts.ApplicationHeartbeatInterval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
        opts.ApplicationHeartbeatEnabled.Should().BeTrue();
        opts.IsClientTimeoutSafe.Should().BeTrue(
            "SignalR guidance is ClientTimeoutInterval >= 2x KeepAliveInterval");
    }

    [Fact]
    public void ConfigSection_Binds_OverridingDefaults()
    {
        IServiceProvider sp = BuildServices(new Dictionary<string, string?>
        {
            ["RealtimeResilience:KeepAliveInterval"] = "00:00:10",
            ["RealtimeResilience:ClientTimeoutInterval"] = "00:00:25",
            ["RealtimeResilience:HandshakeTimeout"] = "00:00:12",
            ["RealtimeResilience:ApplicationHeartbeatInterval"] = "00:00:11",
            ["RealtimeResilience:ApplicationHeartbeatEnabled"] = "false",
            ["ChatTimeout:SingleShot"] = "00:01:00",
            ["ChatTimeout:Plan"] = "00:04:00",
        });

        RealtimeResilienceOptions rt = sp.GetRequiredService<IOptions<RealtimeResilienceOptions>>().Value;
        rt.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(10));
        rt.ClientTimeoutInterval.Should().Be(TimeSpan.FromSeconds(25));
        rt.HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(12));
        rt.ApplicationHeartbeatInterval.Should().Be(TimeSpan.FromSeconds(11));
        rt.ApplicationHeartbeatEnabled.Should().BeFalse();

        ChatTimeoutOptions timeouts = sp.GetRequiredService<IOptions<ChatTimeoutOptions>>().Value;
        timeouts.SingleShot.Should().Be(TimeSpan.FromMinutes(1));
        timeouts.Plan.Should().Be(TimeSpan.FromMinutes(4));
        timeouts.Plan.Should().BeGreaterThan(timeouts.SingleShot,
            "the two ceilings must remain distinct even under operator override");
    }
}
