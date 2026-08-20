using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailPulse.Api.Persistence;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration for the durable session/turn store. Mirrors the shape of
/// <c>AddConversationMemory</c> / <c>AddProactiveAlerts</c>: one entry point, wires the
/// options section, the singleton SQLite store, and the cleanup hosted service.
/// </summary>
public static class SessionPersistenceServiceExtensions
{
    /// <summary>
    /// Bind <see cref="SessionPersistenceOptions"/> and, when
    /// <see cref="SessionPersistenceOptions.Enabled"/> is true, register the SQLite store
    /// singleton and the retention cleanup background service. When it is false, only
    /// the options binding is registered — no database is created and no store singleton
    /// exists in the container, so a bug in a downstream consumer that resolves
    /// <c>ISessionStore</c> without checking the flag will fail loudly instead of
    /// silently writing to a file the operator never asked to exist.
    /// </summary>
    public static IServiceCollection AddSessionPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string dbPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        services.Configure<SessionPersistenceOptions>(
            configuration.GetSection(SessionPersistenceOptions.SectionName));

        SessionPersistenceOptions opts = configuration
            .GetSection(SessionPersistenceOptions.SectionName)
            .Get<SessionPersistenceOptions>() ?? new SessionPersistenceOptions();

        if (!opts.Enabled)
            return services;

        services.AddSingleton<ISessionStore>(sp =>
            new SqliteSessionStore(
                dbPath,
                sp.GetRequiredService<ILogger<SqliteSessionStore>>()));

        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<SessionCleanupBackgroundService>();

        return services;
    }
}
