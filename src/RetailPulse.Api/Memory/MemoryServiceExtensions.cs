using RetailPulse.Contracts.Memory;

namespace RetailPulse.Api.Memory;

/// <summary>
/// Extension methods for registering conversation memory services in DI.
/// </summary>
public static class MemoryServiceExtensions
{
    /// <summary>
    /// Registers <see cref="IConversationMemory"/> (SQLite-backed singleton),
    /// <see cref="MemoryExtractionService"/>, <see cref="ConversationMemoryMiddleware"/>,
    /// <see cref="MemoryExtractionChannel"/>, and <see cref="MemoryExtractionBackgroundService"/>.
    /// </summary>
    public static IServiceCollection AddConversationMemory(
        this IServiceCollection services,
        string dbPath)
    {
        // Singleton — SQLite shared cache + SMB-safe rollback journaling
        services.AddSingleton<IConversationMemory>(sp =>
        {
            ILogger<SqliteConversationMemory> logger = sp.GetRequiredService<ILogger<SqliteConversationMemory>>();
            return new SqliteConversationMemory(dbPath, logger);
        });

        // Scoped — one extraction service per request (uses IChatClient which is scoped)
        services.AddScoped<MemoryExtractionService>();

        // Scoped — middleware wraps each agent call
        services.AddScoped<ConversationMemoryMiddleware>();

        // Bounded channel + background service for fire-and-forget memory extraction
        services.AddSingleton<MemoryExtractionChannel>();
        services.AddHostedService<MemoryExtractionBackgroundService>();

        return services;
    }
}
