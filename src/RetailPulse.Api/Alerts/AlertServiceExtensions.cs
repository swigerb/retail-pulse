using RetailPulse.Api.Alerts;
using RetailPulse.Contracts.Alerts;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration helpers for the proactive alerts subsystem.
/// </summary>
public static class AlertServiceExtensions
{
    /// <summary>
    /// Register the proactive alert service (IHostedService + IAlertService) backed by SQLite.
    /// </summary>
    public static IServiceCollection AddProactiveAlerts(this IServiceCollection services, string dbPath)
    {
        // Singleton alert store — WAL mode handles concurrency
        services.AddSingleton<SqliteAlertService>(sp =>
            new SqliteAlertService(dbPath, sp.GetRequiredService<ILogger<SqliteAlertService>>()));

        services.AddSingleton<IAlertService>(sp => sp.GetRequiredService<SqliteAlertService>());

        // Background hosted service for periodic anomaly detection
        services.AddHostedService<ProactiveAlertService>();

        return services;
    }
}
