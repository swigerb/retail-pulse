using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace RetailPulse.Api.Configuration;

/// <summary>
/// Resolves the single writable directory that every durable SQLite store
/// (audit, cost, memory, approvals, alerts) is opened under.
/// <para>
/// <b>Deployment note (governance):</b> the deployed synthetic demo does <b>not</b>
/// mount a durable Azure volume. This tenant's policy forces new storage accounts
/// to <c>allowSharedKeyAccess=false</c>/<c>publicNetworkAccess=Disabled</c>, which
/// breaks the account-key Azure Files CIFS mount that was previously used, so the
/// mount was removed (see <c>docs/deployment-azd.md</c>). The deployed API runs
/// <c>ASPNETCORE_ENVIRONMENT=Development</c> with <see cref="ConfigKey"/> unset and
/// therefore uses a per-machine temp directory; observability history lives only
/// within the current replica and resets on replacement. Local development behaves
/// the same way.
/// </para>
/// <para>
/// The resolver still supports a durable path for any future policy-compatible
/// backing. Durability can be required <b>explicitly</b> via
/// <see cref="RequireDurableStorageKey"/> (<c>RETAIL_PULSE_REQUIRE_DURABLE_STORAGE</c>),
/// which, when truthy, makes a writable path a hard startup requirement regardless
/// of <c>ASPNETCORE_ENVIRONMENT</c>. A malformed flag value is rejected rather than
/// silently treated as "not required". Neither the flag nor <see cref="ConfigKey"/>
/// is set on the current demo.
/// </para>
/// <para>
/// The resolver never silently degrades to ephemeral storage when durability is
/// required: it always probes that the resolved directory is writable and throws if
/// it is not. When <see cref="ConfigKey"/> is left unset in a Production environment
/// it also fails fast, because a deployed Production API without a durable path
/// would otherwise lose observability history on every replica churn. This
/// Production fail-closed behavior is retained for coordination with the pending
/// auth PR, which flips the deployed API to Production and must supply a
/// policy-compatible durable path (or explicitly relax the requirement). Local
/// development with the flag absent/false and no Production environment may use temp.
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
                    $"is configured. Set '{ConfigKey}' to a mounted, writable, policy-compatible durable path. " +
                    "Refusing to fall back to ephemeral temporary storage, which " +
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
            // Explicitly configured durable path (retained for a future
            // policy-compatible durable backing). Treat it as a hard requirement.
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
                "writable, policy-compatible durable path. Refusing to " +
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
                  "Container App this usually means the configured durable volume failed to mount or is not " +
                  "reachable. Refusing to start with ephemeral storage so observability history is not silently lost."
                : $"The local data directory '{directory}' is not writable.";
            throw new InvalidOperationException(detail, ex);
        }
    }
}
