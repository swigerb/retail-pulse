namespace RetailPulse.Api.Security;

internal static class CorsOriginResolver
{
    private static readonly string[] _localOrigins =
    [
        "http://localhost:5173",
        "https://localhost:5173",
        "http://localhost:5100",
        "https://localhost:5100"
    ];

    internal static string[] ForDevelopment(IEnumerable<string> configuredOrigins)
    {
        var origins = new HashSet<string>(_localOrigins, StringComparer.OrdinalIgnoreCase);
        origins.UnionWith(configuredOrigins.Where(origin => !string.IsNullOrWhiteSpace(origin)));
        return [.. origins];
    }
}
