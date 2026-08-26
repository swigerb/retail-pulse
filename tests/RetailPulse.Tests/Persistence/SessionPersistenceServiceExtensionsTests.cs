using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Persistence;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Persistence;

/// <summary>
/// Config-switch tests for the durable session persistence feature. Proves the
/// disabled-by-config acceptance criterion from issue #90: when
/// <c>SessionPersistence:Enabled</c> is false, no <see cref="ISessionStore"/> is
/// registered, no <see cref="SessionCleanupBackgroundService"/> is hosted, and no
/// database file is created — the chat pipeline behaves exactly as it did before
/// the feature was added.
/// </summary>
public sealed class SessionPersistenceServiceExtensionsTests
{
    private static IServiceCollection BuildServices(IDictionary<string, string?> settings, string dbPath)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSessionPersistence(configuration, dbPath);
        return services;
    }

    [Fact]
    public void Disabled_DoesNotRegisterStore_OrCleanupService_OrTouchDisk()
    {
        string dbPath = SqliteTestCleanup.NewDbPath("session_disabled");

        IServiceCollection services = BuildServices(
            new Dictionary<string, string?> { ["SessionPersistence:Enabled"] = "false" },
            dbPath);

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetService<ISessionStore>().Should().BeNull(
            "the feature switch is off, so no store singleton must be resolvable — a stray consumer must fail loudly, not silently write to disk");

        provider.GetServices<IHostedService>()
            .OfType<SessionCleanupBackgroundService>()
            .Should().BeEmpty("the cleanup service must not be scheduled when persistence is off");

        File.Exists(dbPath).Should().BeFalse(
            "the disabled path must never create a database file — Wave 1 behaviour is bit-for-bit preserved");
    }

    [Fact]
    public void Enabled_RegistersStore_AndCleanupService()
    {
        string dbPath = SqliteTestCleanup.NewDbPath("session_enabled");

        try
        {
            IServiceCollection services = BuildServices(
                new Dictionary<string, string?> { ["SessionPersistence:Enabled"] = "true" },
                dbPath);

            using ServiceProvider provider = services.BuildServiceProvider();

            provider.GetService<ISessionStore>().Should().NotBeNull(
                "the feature switch is on, so the SQLite-backed store must be resolvable");

            provider.GetServices<IHostedService>()
                .OfType<SessionCleanupBackgroundService>()
                .Should().ContainSingle("retention must be enforced by a single background sweep when persistence is on");
        }
        finally
        {
            SqliteTestCleanup.ReleaseAndDelete(dbPath);
        }
    }
}
