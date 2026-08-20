using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailPulse.Api.Persistence;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration for the durable plan store (issue #93). Mirrors
/// <see cref="SessionPersistenceServiceExtensions.AddSessionPersistence"/>:
/// binds options, and when <see cref="PlanPersistenceOptions.Enabled"/> is
/// true, registers the SQLite plan store singleton plus the retention
/// background sweeper. When disabled, no database is created and the plan
/// store is absent from the container — the chat pipeline consequently falls
/// back to the pre-#93 single-specialist path.
/// </summary>
public static class PlanPersistenceServiceExtensions
{
    public static IServiceCollection AddPlanPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string dbPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        services.Configure<PlanPersistenceOptions>(
            configuration.GetSection(PlanPersistenceOptions.SectionName));

        PlanPersistenceOptions opts = configuration
            .GetSection(PlanPersistenceOptions.SectionName)
            .Get<PlanPersistenceOptions>() ?? new PlanPersistenceOptions();

        if (!opts.Enabled)
            return services;

        services.AddSingleton<IPlanStore>(sp =>
            new SqlitePlanStore(
                dbPath,
                sp.GetRequiredService<ILogger<SqlitePlanStore>>()));

        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<PlanCleanupBackgroundService>();

        return services;
    }
}
