using FluentAssertions;
using RetailPulse.Api.Configuration;

namespace RetailPulse.Tests.Configuration;

/// <summary>
/// Tests for <see cref="DataDirectoryResolver"/> — the single place that decides
/// where every durable SQLite store is opened. It must fail fast in Production
/// when no durable path is configured (never silently fall back to ephemeral
/// temp storage) and must fall back safely to a temp directory in local
/// Development.
/// </summary>
public sealed class DataDirectoryResolverTests : IDisposable
{
    private readonly List<string> _createdDirs = [];

    public void Dispose()
    {
        foreach (string dir in _createdDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private string Track(string dir)
    {
        _createdDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void Development_WithoutConfiguredDirectory_FallsBackToTemp()
    {
        string resolved = DataDirectoryResolver.Resolve(configuredDirectory: null, isProduction: false);

        resolved.Should().Be(
            Path.Combine(Path.GetTempPath(), DataDirectoryResolver.LocalFallbackFolderName),
            "local development defaults safely to a per-machine temp directory");
        Directory.Exists(resolved).Should().BeTrue("the resolver must create and verify the directory");
    }

    [Fact]
    public void Production_WithoutConfiguredDirectory_FailsFast()
    {
        Action act = () => DataDirectoryResolver.Resolve(configuredDirectory: null, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*required in Production*")
            .Which.Message.Should().Contain(DataDirectoryResolver.ConfigKey,
                "the failure must name the setting the operator has to provide");
    }

    [Fact]
    public void ConfiguredWritableDirectory_IsUsedAndCreated_InProduction()
    {
        string target = Track(Path.Combine(Path.GetTempPath(), $"rp-datadir-{Guid.NewGuid():N}", "nested"));

        string resolved = DataDirectoryResolver.Resolve(target, isProduction: true);

        resolved.Should().Be(target);
        Directory.Exists(resolved).Should().BeTrue("the mounted durable path must be created and write-probed");
    }

    [Fact]
    public void ConfiguredDirectory_TakesPrecedence_EvenOutsideProduction()
    {
        string target = Track(Path.Combine(Path.GetTempPath(), $"rp-datadir-{Guid.NewGuid():N}"));

        // Mirrors deployed ACA, which runs ASPNETCORE_ENVIRONMENT=Development but
        // sets RETAIL_PULSE_DATA_DIRECTORY to the Azure Files mount. The explicit
        // path must win over the temp fallback so deployed history is durable.
        string resolved = DataDirectoryResolver.Resolve(target, isProduction: false);

        resolved.Should().Be(target);
    }

    [Fact]
    public void ConfiguredUnwritableDirectory_FailsFast_NoSilentFallback()
    {
        // A path whose parent is a file (not a directory) cannot be created — this
        // deterministically simulates a failed Azure Files mount.
        string blocker = Track(Path.Combine(Path.GetTempPath(), $"rp-blocker-{Guid.NewGuid():N}"));
        File.WriteAllText(blocker, "not a directory");
        string unwritable = Path.Combine(blocker, "cannot", "exist");

        Action act = () => DataDirectoryResolver.Resolve(unwritable, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not writable*")
            .Which.Message.Should().Contain(DataDirectoryResolver.ConfigKey);
    }

    [Fact]
    public void BlankConfiguredDirectory_IsTreatedAsUnset()
    {
        Action act = () => DataDirectoryResolver.Resolve(configuredDirectory: "   ", isProduction: true);

        act.Should().Throw<InvalidOperationException>("whitespace is not a real durable path");
    }
}
