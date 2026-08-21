namespace RetailPulse.Api.Packs;

/// <summary>
/// Locates the on-disk <c>packs/</c> root across the three host contexts
/// the platform runs in:
/// <list type="bullet">
///   <item>dev (<c>dotnet run</c>) — the API's <see cref="IHostEnvironment.ContentRootPath"/>
///     is the project directory, so <c>Packs:Root</c> is the sibling repo
///     directory two levels up from the project when it isn't copied in.</item>
///   <item>test — the test host's base directory is under <c>bin/</c>; the
///     walk-up locates the <c>RetailPulse.slnx</c> marker at the repo root.</item>
///   <item>published host — the pack files are copied next to the app via
///     the project's <c>Content</c> item so <c>ContentRootPath\packs</c>
///     is the primary location and no walk-up is required.</item>
/// </list>
/// The resolver enumerates the candidates in that order and returns the
/// first directory that both exists and contains the active pack. It
/// throws a diagnostic <see cref="DirectoryNotFoundException"/> that
/// lists every candidate when nothing matches, so a misconfigured host
/// never surfaces as a silent empty pack list.
/// </summary>
public static class PackPathResolver
{
    /// <summary>
    /// Resolve the packs root directory. Absolute <paramref name="configuredRoot"/>
    /// values short-circuit the walk-up entirely; relative values are
    /// resolved against every candidate in priority order.
    /// </summary>
    /// <param name="contentRootPath">Host content root (<c>builder.Environment.ContentRootPath</c>).</param>
    /// <param name="configuredRoot">The <c>Packs:Root</c> setting.</param>
    /// <param name="activePack">The <c>Packs:Active</c> setting — used to
    /// disambiguate candidate roots when more than one directory exists.</param>
    public static string Resolve(string contentRootPath, string configuredRoot, string activePack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(activePack);

        if (Path.IsPathRooted(configuredRoot))
        {
            return Directory.Exists(configuredRoot)
                ? Path.GetFullPath(configuredRoot)
                : throw new DirectoryNotFoundException(
                    $"Configured Packs:Root '{configuredRoot}' does not exist.");
        }

        var candidates = new List<string>();
        AddCandidate(candidates, Path.Combine(contentRootPath, configuredRoot));

        // Walk up looking for the repo root marker (RetailPulse.slnx) so
        // the API can find packs when running from bin/... in dev/test
        // without every consumer having to configure an absolute path.
        DirectoryInfo? dir = new(contentRootPath);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            AddCandidate(candidates, Path.Combine(dir.FullName, configuredRoot));
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
            {
                AddCandidate(candidates, Path.Combine(dir.FullName, configuredRoot));
                break;
            }
        }

        // Prefer a candidate that contains the requested active pack; fall
        // back to any existing candidate so a fresh clone with only the
        // default pack still surfaces a helpful "unknown pack" from the
        // loader instead of a directory-not-found here.
        foreach (string candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, activePack)))
            {
                return Path.GetFullPath(candidate);
            }
        }
        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        string tried = candidates.Count == 0
            ? "<no candidates>"
            : string.Join(Environment.NewLine + "  - ", candidates.Select(Path.GetFullPath));
        throw new DirectoryNotFoundException(
            $"Could not locate a Packs:Root containing pack '{activePack}'. " +
            $"Set Packs:Root to an absolute path, or ensure the packs directory ships with the deployment. " +
            $"Tried:{Environment.NewLine}  - {tried}");
    }

    private static void AddCandidate(List<string> candidates, string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return;
        }

        if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(full);
        }
    }
}
