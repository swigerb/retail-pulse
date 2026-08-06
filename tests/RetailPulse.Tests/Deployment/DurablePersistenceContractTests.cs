using System.Text.RegularExpressions;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Static guardrails for the durable-persistence deployment contract added to fix
/// the blocking observability-history defect: the deployed API stored its SQLite
/// databases under <c>Path.GetTempPath()</c>, which a fresh ACA replica
/// (minReplicas=0, no persistent volume) wiped on every scale-to-zero cycle.
///
/// These tests only inspect repo files (the infra Bicep and azd hooks) — they
/// never invoke <c>azd</c>, Bicep, or Azure. They assert that a dedicated Azure
/// Files-backed volume is provisioned, registered with the Container Apps
/// Environment, mounted into the API, and pointed at by a configurable data
/// directory, with the storage-account key never surfaced as an output/azd value.
/// </summary>
public partial class DurablePersistenceContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [GeneratedRegex(@"output\s+\w*[Kk]ey\w*\s+string", RegexOptions.Multiline)]
    private static partial Regex KeyOutputRegex();

    [GeneratedRegex(@"output\s+RETAIL_PULSE_DATA_DIRECTORY\s+string\s*=", RegexOptions.Multiline)]
    private static partial Regex DataDirectoryOutputRegex();

    [GeneratedRegex(@"output\s+RETAIL_PULSE_REQUIRE_DURABLE_STORAGE\s+string\s*=\s*'true'", RegexOptions.Multiline)]
    private static partial Regex RequireDurableStorageOutputRegex();

    [GeneratedRegex(@"name:\s*'RETAIL_PULSE_REQUIRE_DURABLE_STORAGE'\s*value:\s*'true'", RegexOptions.Singleline)]
    private static partial Regex RequireDurableStorageEnvRegex();

    [GeneratedRegex(@"output\s+\w*STORAGE\w*KEY\w*\s+string", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex StorageKeyOutputRegex();

    [GeneratedRegex(@"volumeMounts:")]
    private static partial Regex VolumeMountsRegex();

    [GeneratedRegex(@"storageType:\s*'AzureFile'")]
    private static partial Regex AzureFileVolumeRegex();

    private static string ReadInfra(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, "infra", relativePath));

    // ── storage.bicep: least-cost, hardened Standard_LRS StorageV2 + share ──

    [Fact]
    public void StorageModule_Exists()
    {
        File.Exists(Path.Combine(RepoRoot, "infra", "modules", "storage.bicep"))
            .Should().BeTrue("a dedicated Azure Files storage module must exist for durable app data");
    }

    [Fact]
    public void StorageModule_ProvisionsHardenedStandardLrsStorageV2()
    {
        string bicep = ReadInfra("modules/storage.bicep");

        bicep.Should().MatchRegex(@"Microsoft\.Storage/storageAccounts@",
            "the module must declare a storage account");
        bicep.Should().MatchRegex(@"name:\s*'Standard_LRS'",
            "least-cost locally-redundant storage is required");
        bicep.Should().MatchRegex(@"kind:\s*'StorageV2'",
            "a general-purpose v2 account is required");
        bicep.Should().MatchRegex(@"minimumTlsVersion:\s*'TLS1_2'",
            "TLS 1.2 must be the floor");
        bicep.Should().MatchRegex(@"allowBlobPublicAccess:\s*false",
            "anonymous blob access must be disabled");
    }

    [Fact]
    public void StorageModule_DeclaresPrivateFileShare()
    {
        string bicep = ReadInfra("modules/storage.bicep");
        bicep.Should().MatchRegex(@"Microsoft\.Storage/storageAccounts/fileServices/shares@",
            "the module must declare an Azure Files share for the durable SQLite stores");
    }

    [Fact]
    public void StorageModule_DoesNotOutputAccountKey()
    {
        string bicep = ReadInfra("modules/storage.bicep");
        KeyOutputRegex().IsMatch(bicep)
            .Should().BeFalse("the storage account key must never be emitted as a module output");
    }

    [Fact]
    public void MainBicep_WiresStorageModule()
    {
        string bicep = ReadInfra("main.bicep");
        bicep.Should().Contain("./modules/storage.bicep",
            "main.bicep must provision the durable storage module");
    }

    // ── container-apps-env.bicep: environment storage, key stays inside ARM ──

    [Fact]
    public void EnvironmentModule_RegistersAzureFilesShareReadWrite()
    {
        string bicep = ReadInfra("modules/container-apps-env.bicep");

        bicep.Should().MatchRegex(@"Microsoft\.App/managedEnvironments/storages@",
            "the share must be registered with the Container Apps Environment as environment storage");
        bicep.Should().MatchRegex(@"accessMode:\s*'ReadWrite'",
            "the API must be able to write its SQLite stores, so the share is mounted read-write");
    }

    [Fact]
    public void EnvironmentModule_ObtainsAccountKeyInsideBicep_WithoutOutputtingIt()
    {
        string bicep = ReadInfra("modules/container-apps-env.bicep");

        bicep.Should().Contain("listKeys(",
            "the account key must be obtained inside ARM/Bicep via listKeys(), not passed in or stored");
        KeyOutputRegex().IsMatch(bicep)
            .Should().BeFalse("the environment-storage module must never output the account key");
    }

    // ── container-apps.bicep: API-only volume + mount + data dir env ─────────

    [Fact]
    public void ApiContainer_MountsAzureFilesVolumeAtDataPath()
    {
        string bicep = ReadInfra("modules/container-apps.bicep");

        bicep.Should().MatchRegex(@"storageType:\s*'AzureFile'",
            "the API must mount an Azure Files volume for durable data");
        bicep.Should().Contain("/mnt/retailpulse-data",
            "the durable volume must be mounted at a clear, documented path");
        bicep.Should().Contain("RETAIL_PULSE_DATA_DIRECTORY",
            "the API container must set the data-directory env var so it cannot regress to temp storage");
    }

    [Fact]
    public void ApiContainer_MountPathHasExactValue()
    {
        string bicep = ReadInfra("modules/container-apps.bicep");

        // Exact-value contract: the mount path the app reads (RETAIL_PULSE_DATA_DIRECTORY)
        // must equal the volume mount path, and both come from this default.
        bicep.Should().MatchRegex(@"param\s+dataMountPath\s+string\s*=\s*'/mnt/retailpulse-data'",
            "the mounted durable path must be exactly '/mnt/retailpulse-data'");
    }

    [Fact]
    public void ApiContainer_RequiresDurableStorage_EnvAgnostically()
    {
        string bicep = ReadInfra("modules/container-apps.bicep");

        // The API deploys with ASPNETCORE_ENVIRONMENT=Development, so durability must
        // be enforced by an explicit, environment-agnostic flag set to exactly 'true'
        // alongside the mount — not inferred from the environment.
        RequireDurableStorageEnvRegex().IsMatch(bicep)
            .Should().BeTrue("the API container must set RETAIL_PULSE_REQUIRE_DURABLE_STORAGE to exactly 'true'");
    }

    [Fact]
    public void OnlyApiMountsDurableVolume()
    {
        string bicep = ReadInfra("modules/container-apps.bicep");

        VolumeMountsRegex().Count(bicep).Should().Be(1,
            "only the API requires the shared durable data; the MCP server re-seeds and the bot is stateless");
        AzureFileVolumeRegex().Count(bicep).Should().Be(1,
            "exactly one container app should declare the Azure Files volume");
    }

    [Fact]
    public void ApiContainer_KeepsSingleReplicaScaleToZero()
    {
        string bicep = ReadInfra("modules/container-apps.bicep");

        // Single-writer constraint: SQLite over SMB is safe only with one replica.
        bicep.Should().MatchRegex(@"minReplicas:\s*0",
            "scale-to-zero remains enabled — durability now comes from the Azure Files mount, not a warm replica");
        bicep.Should().MatchRegex(@"maxReplicas:\s*1",
            "max one replica so a single writer owns the SQLite files on the SMB share");
    }

    // ── azd hooks re-assert the durable directory ───────────────────────────

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_SetsDataDirectoryEnvVar(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));
        script.Should().Contain("RETAIL_PULSE_DATA_DIRECTORY",
            $"{hookFile} must re-assert the durable data directory on the API so it cannot regress");
    }

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_ReassertsRequireDurableStorageFlag(string hookFile)
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));
        script.Should().Contain("RETAIL_PULSE_REQUIRE_DURABLE_STORAGE",
            $"{hookFile} must re-assert the environment-agnostic durability requirement on the API so a " +
            "re-provision cannot drop it");
    }

    [Fact]
    public void MainBicep_EmitsDataDirectoryContract_WithoutStorageKey()
    {
        string bicep = ReadInfra("main.bicep");

        DataDirectoryOutputRegex().IsMatch(bicep)
            .Should().BeTrue("main.bicep must emit the mount path so the hook re-asserts the same durable path");
        StorageKeyOutputRegex().IsMatch(bicep)
            .Should().BeFalse("no storage account key may be emitted as an azd output");
    }

    [Fact]
    public void MainBicep_EmitsRequireDurableStorageContract_ExactlyTrue()
    {
        string bicep = ReadInfra("main.bicep");

        RequireDurableStorageOutputRegex().IsMatch(bicep)
            .Should().BeTrue("main.bicep must emit RETAIL_PULSE_REQUIRE_DURABLE_STORAGE = 'true' so the hook " +
                "re-asserts the exact durability requirement");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repo root (RetailPulse.slnx) walking up from " + AppContext.BaseDirectory);
    }
}
