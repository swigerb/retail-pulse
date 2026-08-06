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
/// mount was removed (see <c>docs/deployment-azd.md</c>). The deployed API now runs
/// <c>ASPNETCORE_ENVIRONMENT=Production</c> under Entra auth, but because there is
/// no durable path it would otherwise fail closed (below). The demo therefore sets
/// <see cref="AllowEphemeralStorageKey"/> (<c>RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE</c>)
/// to <c>true</c>: an <b>explicit, honest opt-out</b> that permits a per-replica temp
/// directory in Production <i>without</i> claiming durability. Observability history
/// then lives only within the current replica and resets on replacement/redeploy.
/// Local development behaves the same way without any flag.
/// </para>
/// <para>
/// The resolver still supports a durable path for any future policy-compatible
/// backing. Durability can be required <b>explicitly</b> via
/// <see cref="RequireDurableStorageKey"/> (<c>RETAIL_PULSE_REQUIRE_DURABLE_STORAGE</c>),
/// which, when truthy, makes a writable path a hard startup requirement regardless
/// of <c>ASPNETCORE_ENVIRONMENT</c> and regardless of
/// <see cref="AllowEphemeralStorageKey"/>. A malformed flag value is rejected rather
/// than silently treated as "not required".
/// </para>
/// <para>
/// The resolver never silently degrades to ephemeral storage when durability is
/// required: it always probes that the resolved directory is writable and throws if
/// it is not, and it never honours the ephemeral opt-out while
/// <see cref="RequireDurableStorageKey"/> is truthy. When <see cref="ConfigKey"/> is
/// left unset in a Production environment it fails fast <b>unless</b> the operator has
/// explicitly opted in to ephemeral storage via <see cref="AllowEphemeralStorageKey"/>,
/// because a deployed Production API without a durable path would otherwise lose
/// observability history on every replica churn without anyone acknowledging it.
/// Local development with the flags absent/false and no Production environment uses temp.
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

    /// <summary>
    /// Explicit opt-out (<c>RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE</c>) that, when
    /// truthy, permits a writable per-replica temp directory in <b>Production</b>
    /// without a configured durable path — instead of failing closed. It makes the
    /// non-durability an acknowledged, deliberate choice (used by the synthetic demo,
    /// which now runs Production under Entra auth with no policy-compatible durable
    /// backing). It is ignored while <see cref="RequireDurableStorageKey"/> is truthy
    /// or when a durable path is configured, so it can never silently weaken a real
    /// durability requirement. A malformed value is rejected rather than guessed.
    /// </summary>
    public const string AllowEphemeralStorageKey = "RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE";

    /// <summary>Directory name used for the local-development temp fallback.</summary>
    public const string LocalFallbackFolderName = "retailpulse";

    /// <summary>
    /// Resolve the durable data directory from configuration and hosting
    /// environment, creating and write-probing it. Throws on any unwritable or
    /// missing required path, and on a malformed require-durable-storage or
    /// allow-ephemeral-storage flag.
    /// </summary>
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        bool requireDurable = ParseRequireDurableStorage(configuration[RequireDurableStorageKey]);
        bool allowEphemeral = ParseAllowEphemeralStorage(configuration[AllowEphemeralStorageKey]);
        return Resolve(configuration[ConfigKey], environment.IsProduction(), requireDurable, allowEphemeral);
    }

    /// <summary>
    /// Backward-compatible overload with the require-durable-storage flag defaulted
    /// to <c>false</c> (i.e. behaviour driven solely by the configured path and the
    /// Production check).
    /// </summary>
    public static string Resolve(string? configuredDirectory, bool isProduction) =>
        Resolve(configuredDirectory, isProduction, requireDurableStorage: false, allowEphemeralStorage: false);

    /// <summary>
    /// Backward-compatible overload with the allow-ephemeral-storage opt-out defaulted
    /// to <c>false</c>. Production without a durable path still fails closed here.
    /// </summary>
    public static string Resolve(string? configuredDirectory, bool isProduction, bool requireDurableStorage) =>
        Resolve(configuredDirectory, isProduction, requireDurableStorage, allowEphemeralStorage: false);

    /// <summary>
    /// Core resolution logic, decoupled from the host for testing.
    /// </summary>
    /// <param name="configuredDirectory">Value of <see cref="ConfigKey"/>, or null/blank when unset.</param>
    /// <param name="isProduction">Whether the app is running in the Production environment.</param>
    /// <param name="requireDurableStorage">
    /// Whether durable storage is explicitly required (the parsed value of
    /// <see cref="RequireDurableStorageKey"/>). When <c>true</c>, a missing/empty
    /// or unwritable path fails startup regardless of <paramref name="isProduction"/>
    /// and regardless of <paramref name="allowEphemeralStorage"/>.
    /// </param>
    /// <param name="allowEphemeralStorage">
    /// Whether the operator has explicitly opted in to ephemeral storage (the parsed
    /// value of <see cref="AllowEphemeralStorageKey"/>). When <c>true</c> it permits a
    /// per-replica temp directory in Production instead of failing closed. It is
    /// ignored when <paramref name="requireDurableStorage"/> is <c>true</c> or when a
    /// durable path is configured.
    /// </param>
    public static string Resolve(string? configuredDirectory, bool isProduction, bool requireDurableStorage, bool allowEphemeralStorage)
    {
        string? configured = string.IsNullOrWhiteSpace(configuredDirectory)
            ? null
            : configuredDirectory.Trim();

        if (requireDurableStorage)
        {
            // Explicit, environment-agnostic requirement: durable storage MUST be
            // present and writable. Never fall back to temp, even in Development, and
            // never honour the ephemeral opt-out — a real requirement wins.
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
        else if (isProduction && !allowEphemeralStorage)
        {
            // Production without a configured durable path and without an explicit
            // ephemeral opt-out is a misconfiguration: refuse to boot rather than
            // persist to a temp dir that a fresh replica or scale-to-zero cycle would
            // wipe. Operators either supply a durable path or acknowledge the loss by
            // setting AllowEphemeralStorageKey.
            throw new InvalidOperationException(
                $"A durable data directory is required in Production. Set '{ConfigKey}' to a mounted, " +
                "writable, policy-compatible durable path, or explicitly acknowledge non-durable storage by " +
                $"setting '{AllowEphemeralStorageKey}'=true. Refusing to " +
                "fall back to ephemeral temporary storage, which would lose audit, cost, memory, approval, " +
                "and alert history on every replica replacement or scale-to-zero cycle.");
        }
        else
        {
            // Local development / test, OR Production with an explicit ephemeral
            // opt-out (AllowEphemeralStorageKey): a per-machine temp directory is
            // acceptable. It is honestly non-durable — history resets on replica churn.
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
    public static bool ParseRequireDurableStorage(string? rawValue) =>
        ParseBooleanFlag(
            rawValue,
            RequireDurableStorageKey,
            "would disable the durable-storage requirement and risk losing " +
            "observability history on the mounted share.");

    /// <summary>
    /// Parse the <see cref="AllowEphemeralStorageKey"/> opt-out. Absent/blank is
    /// <c>false</c>. Accepts case-insensitive <c>true</c>/<c>false</c> and
    /// <c>1</c>/<c>0</c>. Any other non-blank value is rejected rather than silently
    /// treated as <c>true</c> — a typo must not accidentally permit ephemeral storage
    /// in Production.
    /// </summary>
    public static bool ParseAllowEphemeralStorage(string? rawValue) =>
        ParseBooleanFlag(
            rawValue,
            AllowEphemeralStorageKey,
            "would accidentally permit non-durable ephemeral storage in Production, " +
            "silently discarding observability history on every replica replacement.");

    private static bool ParseBooleanFlag(string? rawValue, string key, string risk)
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
                $"'{key}' has a malformed value '{rawValue}'. Use 'true' or 'false' " +
                "(case-insensitive; '1'/'0' also accepted). Refusing to guess, because silently treating an " +
                $"unrecognized value as its default {risk}"),
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
