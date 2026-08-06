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
/// The resolver never silently degrades to ephemeral storage: it always probes
/// that the resolved directory is writable and throws if it is not. When the
/// directory is left unset in a Production environment it fails fast, because a
/// deployed Production API without a mounted durable path would otherwise lose
/// observability history on every replica churn.
/// </para>
/// </summary>
public static class DataDirectoryResolver
{
    /// <summary>Configuration / environment key that points at the durable data directory.</summary>
    public const string ConfigKey = "RETAIL_PULSE_DATA_DIRECTORY";

    /// <summary>Directory name used for the local-development temp fallback.</summary>
    public const string LocalFallbackFolderName = "retailpulse";

    /// <summary>
    /// Resolve the durable data directory from configuration and hosting
    /// environment, creating and write-probing it. Throws on any unwritable or
    /// missing required path.
    /// </summary>
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        return Resolve(configuration[ConfigKey], environment.IsProduction());
    }

    /// <summary>
    /// Core resolution logic, decoupled from the host for testing.
    /// </summary>
    /// <param name="configuredDirectory">Value of <see cref="ConfigKey"/>, or null/blank when unset.</param>
    /// <param name="isProduction">Whether the app is running in the Production environment.</param>
    public static string Resolve(string? configuredDirectory, bool isProduction)
    {
        string? configured = string.IsNullOrWhiteSpace(configuredDirectory)
            ? null
            : configuredDirectory.Trim();

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
