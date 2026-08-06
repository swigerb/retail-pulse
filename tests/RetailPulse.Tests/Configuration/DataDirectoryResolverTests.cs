using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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

    // ── RETAIL_PULSE_REQUIRE_DURABLE_STORAGE: environment-agnostic requirement ──

    [Fact]
    public void Required_Development_WithoutConfiguredDirectory_FailsFast_NoTempFallback()
    {
        // The deployed API runs ASPNETCORE_ENVIRONMENT=Development but sets the
        // require flag. A missing durable path must fail startup, NOT fall back to
        // temp, independent of the environment.
        Action act = () => DataDirectoryResolver.Resolve(
            configuredDirectory: null, isProduction: false, requireDurableStorage: true);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(DataDirectoryResolver.RequireDurableStorageKey,
                "the failure must name the flag that made durable storage mandatory")
            .And.Contain(DataDirectoryResolver.ConfigKey,
                "and the setting the operator must provide");
    }

    [Fact]
    public void Required_Development_UnwritableDirectory_FailsFast()
    {
        // A failed Azure Files mount simulated by a path whose parent is a file.
        string blocker = Track(Path.Combine(Path.GetTempPath(), $"rp-blocker-{Guid.NewGuid():N}"));
        File.WriteAllText(blocker, "not a directory");
        string unwritable = Path.Combine(blocker, "cannot", "exist");

        Action act = () => DataDirectoryResolver.Resolve(
            unwritable, isProduction: false, requireDurableStorage: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not writable*")
            .Which.Message.Should().Contain(DataDirectoryResolver.ConfigKey);
    }

    [Fact]
    public void Required_Development_ConfiguredWritableDirectory_IsUsed()
    {
        string target = Track(Path.Combine(Path.GetTempPath(), $"rp-datadir-{Guid.NewGuid():N}", "nested"));

        string resolved = DataDirectoryResolver.Resolve(
            target, isProduction: false, requireDurableStorage: true);

        resolved.Should().Be(target, "an explicit, writable durable path satisfies the requirement in any environment");
        Directory.Exists(resolved).Should().BeTrue();
    }

    [Fact]
    public void NotRequired_Development_WithoutDirectory_UsesTemp()
    {
        // Local development with the flag absent/false is allowed to use temp.
        string resolved = DataDirectoryResolver.Resolve(
            configuredDirectory: null, isProduction: false, requireDurableStorage: false);

        resolved.Should().Be(Path.Combine(Path.GetTempPath(), DataDirectoryResolver.LocalFallbackFolderName));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ParseRequireDurableStorage_AcceptsCanonicalBooleans(string? raw, bool expected) => DataDirectoryResolver.ParseRequireDurableStorage(raw).Should().Be(expected);

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("tru")]
    [InlineData("2")]
    [InlineData("enabled")]
    [InlineData("on")]
    public void ParseRequireDurableStorage_RejectsMalformedTruthyValues_NoSilentDowngrade(string raw)
    {
        Action act = () => DataDirectoryResolver.ParseRequireDurableStorage(raw);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(DataDirectoryResolver.RequireDurableStorageKey,
                "a typo must fail loudly rather than silently disabling the durability requirement");
    }

    [Fact]
    public void ConfigurationOverload_RequireFlagTrue_MissingPath_FailsFast_InDevelopment()
    {
        // End-to-end via IConfiguration + IHostEnvironment: proves the flag is read
        // and enforced even when the environment is Development (mirrors the
        // deployed API's ASPNETCORE_ENVIRONMENT=Development configuration).
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataDirectoryResolver.RequireDurableStorageKey] = "true",
            })
            .Build();

        Action act = () => DataDirectoryResolver.Resolve(config, new StubHostEnvironment("Development"));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(DataDirectoryResolver.RequireDurableStorageKey);
    }

    [Fact]
    public void ConfigurationOverload_MalformedRequireFlag_FailsFast()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataDirectoryResolver.RequireDurableStorageKey] = "yes-please",
            })
            .Build();

        Action act = () => DataDirectoryResolver.Resolve(config, new StubHostEnvironment("Development"));

        act.Should().Throw<InvalidOperationException>();
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "RetailPulse.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
