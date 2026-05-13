using RetailPulse.Contracts.Memory;

namespace RetailPulse.Api.Memory;

/// <summary>
/// Extension methods for registering conversation memory services in DI.
/// </summary>
public static class MemoryServiceExtensions
{
    /// <summary>
    /// Registers <see cref="IConversationMemory"/> (SQLite-backed singleton),
    /// <see cref="MemoryExtractionService"/>, and <see cref="ConversationMemoryMiddleware"/>.
    /// </summary>
    public static IServiceCollection AddConversationMemory(
        this IServiceCollection services,
        string dbPath)
    {
        // Singleton — SQLite WAL mode handles concurrent access
        services.AddSingleton<IConversationMemory>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SqliteConversationMemory>>();
            return new SqliteConversationMemory(dbPath, logger);
        });

        // Scoped — one extraction service per request (uses IChatClient which is scoped)
        services.AddScoped<MemoryExtractionService>();

        // Scoped — middleware wraps each agent call
        services.AddScoped<ConversationMemoryMiddleware>();

        return services;
    }
}
