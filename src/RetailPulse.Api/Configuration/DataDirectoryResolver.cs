using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace RetailPulse.Api.Configuration;

/// <summary>
/// Resolves the single writable directory that every durable SQLite store
/// (audit, cost, memory, approvals, alerts) is opened under.
/// <para>
/// Deployed Azure Container Apps mount an Azure Files share and set
/// <see cref="ConfigKey"/> (<c>RETAIL_PULSE_DATA_DIRECTORY</c>) to the mount path
/// (e.g. <c>/mnt/retailpulse-data</c>) so history survives replica replacement
/// and scale-to-zero. Local development leaves it unset and falls back to a
/// per-machine temp directory.
/// </para>
/// <para>
/// Durability is enforced by an <b>explicit, environment-agnostic</b> switch,
/// <see cref="RequireDurableStorageKey"/> (<c>RETAIL_PULSE_REQUIRE_DURABLE_STORAGE</c>),
/// which deployed infrastructure (Bicep/container env) sets to <c>true</c>
/// alongside the mount. When that flag is truthy the resolver <b>fails startup</b>
/// if the durable path is absent, empty, or unwritable — regardless of
/// <c>ASPNETCORE_ENVIRONMENT</c>. This means the deployed API stays safe even
/// though it currently runs with <c>ASPNETCORE_ENVIRONMENT=Development</c>, and it
/// does not silently regress if future config drift flips the environment. A
/// malformed flag value is rejected rather than silently treated as "not
/// required".
/// </para>
/// <para>
/// The resolver never silently degrades to ephemeral storage: it always probes
/// that the resolved directory is writable and throws if it is not. When the
/// directory is left unset in a Production environment it also fails fast (belt
/// and braces with the explicit flag), because a deployed Production API without
/// a mounted durable path would otherwise lose observability history on every
/// replica churn. Local development with the flag absent/false may use temp.
/// </para>
/// </summary>
public static class DataDirectoryResolver
{
    /// <summary>Configuration / environment key that points at the durable data directory.</summary>
    public const string ConfigKey = "RETAIL_PULSE_DATA_DIRECTORY";

    /// <summary>
    /// Environment-agnostic switch (<c>RETAIL_PULSE_REQUIRE_DURABLE_STORAGE</c>)
    /// that, when truthy, makes a writable durable data directory a hard startup
    /// requirement irrespective of the hosting environment. Set by Bicep/container
    /// env alongside the Azure Files mount.
    /// </summary>
    public const string RequireDurableStorageKey = "RETAIL_PULSE_REQUIRE_DURABLE_STORAGE";

    /// <summary>Directory name used for the local-development temp fallback.</summary>
    public const string LocalFallbackFolderName = "retailpulse";

    /// <summary>
    /// Resolve the durable data directory from configuration and hosting
    /// environment, creating and write-probing it. Throws on any unwritable or
    /// missing required path, and on a malformed require-durable-storage flag.
    /// </summary>
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        bool requireDurable = ParseRequireDurableStorage(configuration[RequireDurableStorageKey]);
        return Resolve(configuration[ConfigKey], environment.IsProduction(), requireDurable);
    }

    /// <summary>
    /// Backward-compatible overload with the require-durable-storage flag defaulted
    /// to <c>false</c> (i.e. behaviour driven solely by the configured path and the
    /// Production check).
    /// </summary>
    public static string Resolve(string? configuredDirectory, bool isProduction) =>
        Resolve(configuredDirectory, isProduction, requireDurableStorage: false);

    /// <summary>
    /// Core resolution logic, decoupled from the host for testing.
    /// </summary>
    /// <param name="configuredDirectory">Value of <see cref="ConfigKey"/>, or null/blank when unset.</param>
    /// <param name="isProduction">Whether the app is running in the Production environment.</param>
    /// <param name="requireDurableStorage">
    /// Whether durable storage is explicitly required (the parsed value of
    /// <see cref="RequireDurableStorageKey"/>). When <c>true</c>, a missing/empty
    /// or unwritable path fails startup regardless of <paramref name="isProduction"/>.
    /// </param>
    public static string Resolve(string? configuredDirectory, bool isProduction, bool requireDurableStorage)
    {
        string? configured = string.IsNullOrWhiteSpace(configuredDirectory)
            ? null
            : configuredDirectory.Trim();

        if (requireDurableStorage)
        {
            // Explicit, environment-agnostic requirement: durable storage MUST be
            // present and writable. Never fall back to temp, even in Development.
            if (configured is null)
            {
                throw new InvalidOperationException(
                    $"Durable storage is required ('{RequireDurableStorageKey}' is set) but no data directory " +
                    $"is configured. Set '{ConfigKey}' to a mounted, writable path (for example the Azure Files " +
                    "mount at /mnt/retailpulse-data). Refusing to fall back to ephemeral temporary storage, which " +
                    "would lose audit, cost, memory, approval, and alert history on every replica replacement or " +
                    "scale-to-zero cycle.");
            }

            EnsureWritable(configured, isDurableRequired: true);
            return configured;
        }

        bool isDurableRequired;
        string directory;

        if (configured is not null)
        {
            // Explicitly configured (deployed ACA points this at the Azure Files
            // mount). Treat it as a hard requirement.
            directory = configured;
            isDurableRequired = true;
        }
        else if (isProduction)
        {
            // Production without a configured durable path is a misconfiguration:
            // refuse to boot rather than persist to a temp dir that a fresh
            // replica or scale-to-zero cycle would wipe.
            throw new InvalidOperationException(
                $"A durable data directory is required in Production. Set '{ConfigKey}' to a mounted, " +
                "writable path (for example the Azure Files mount at /mnt/retailpulse-data). Refusing to " +
                "fall back to ephemeral temporary storage, which would lose audit, cost, memory, approval, " +
                "and alert history on every replica replacement or scale-to-zero cycle.");
        }
        else
        {
            // Local development / test: a per-machine temp directory is fine.
            directory = Path.Combine(Path.GetTempPath(), LocalFallbackFolderName);
            isDurableRequired = false;
        }

        EnsureWritable(directory, isDurableRequired);
        return directory;
    }

    /// <summary>
    /// Parse the <see cref="RequireDurableStorageKey"/> flag. Absent/blank is
    /// <c>false</c>. Accepts case-insensitive <c>true</c>/<c>false</c> and
    /// <c>1</c>/<c>0</c>. Any other non-blank value is rejected with an
    /// <see cref="InvalidOperationException"/> rather than silently downgraded to
    /// <c>false</c> — a typo must not quietly disable the durability guarantee.
    /// </summary>
    public static bool ParseRequireDurableStorage(string? rawValue)
    {
        string value = rawValue?.Trim() ?? string.Empty;

        return value.ToLowerInvariant() switch
        {
            "" => false,
            "true" => true,
            "1" => true,
            "false" => false,
            "0" => false,
            _ => throw new InvalidOperationException(
                $"'{RequireDurableStorageKey}' has a malformed value '{rawValue}'. Use 'true' or 'false' " +
                "(case-insensitive; '1'/'0' also accepted). Refusing to guess, because silently treating an " +
                "unrecognized value as 'false' would disable the durable-storage requirement and risk losing " +
                "observability history on the mounted share."),
        };
    }

    private static void EnsureWritable(string directory, bool isDurableRequired)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, $".rp-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            string detail = isDurableRequired
                ? $"The durable data directory '{directory}' (from '{ConfigKey}') is not writable. In a deployed " +
                  "Container App this usually means the Azure Files volume failed to mount. Refusing to start " +
                  "with ephemeral storage so observability history is not silently lost."
                : $"The local data directory '{directory}' is not writable.";
            throw new InvalidOperationException(detail, ex);
        }
    }
}
