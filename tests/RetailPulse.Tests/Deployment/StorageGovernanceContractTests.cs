using System.Text.RegularExpressions;
using FluentAssertions;
using RetailPulse.Api.Configuration;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Regression guardrails for the storage governance hotfix.
///
/// A prior change (PR #21) mounted the API's SQLite data directory on an Azure
/// Files share registered with the Container Apps Environment using the storage
/// <b>account key</b>. This tenant's governance policy forces every new storage
/// account to <c>allowSharedKeyAccess=false</c> and
/// <c>publicNetworkAccess=Disabled</c> right after creation, so the account-key
/// CIFS mount failed with <c>Permission denied</c> and every API replica
/// crash-looped — a production outage. This hotfix removed the incompatible
/// topology so a future <c>azd provision</c> cannot re-break production.
///
/// These tests only inspect repo files (the infra Bicep and azd hooks) — they
/// never invoke <c>azd</c>, Bicep, or Azure. They assert that the deployed IaC
/// does <b>not</b> provision/register/mount an Azure Files volume and does
/// <b>not</b> force the durable-path environment flags, and that the application's
/// Development temp fallback stays functional so the deployed synthetic demo (which
/// runs <c>ASPNETCORE_ENVIRONMENT=Development</c> with no data-directory configured)
/// boots safely instead of being pinned to a now-missing mount path.
/// </summary>
public partial class StorageGovernanceContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [GeneratedRegex(@"storageType:\s*'AzureFile'")]
    private static partial Regex AzureFileVolumeRegex();

    [GeneratedRegex(@"Microsoft\.App/managedEnvironments/storages@")]
    private static partial Regex ManagedEnvStoragesRegex();

    [GeneratedRegex(@"Microsoft\.Storage/storageAccounts")]
    private static partial Regex StorageAccountRegex();

    [GeneratedRegex(@"volumeMounts:")]
    private static partial Regex VolumeMountsRegex();

    private static string ReadInfra(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, "infra", relativePath));

    private static string ReadHook(string hookFile) =>
        File.ReadAllText(Path.Combine(RepoRoot, "azd-hooks", hookFile));

    // ── No storage account / Azure Files module in the deployed IaC ─────────

    [Fact]
    public void StorageModule_IsRemoved()
    {
        File.Exists(Path.Combine(RepoRoot, "infra", "modules", "storage.bicep"))
            .Should().BeFalse(
                "the account-key Azure Files storage module is incompatible with tenant governance " +
                "(allowSharedKeyAccess=false / publicNetworkAccess=Disabled) and must not be provisioned");
    }

    [Fact]
    public void MainBicep_DoesNotWireStorageModule()
    {
        string bicep = ReadInfra("main.bicep");
        bicep.Should().NotContain("./modules/storage.bicep",
            "main.bicep must not provision the incompatible storage module");
    }

    [Fact]
    public void NoInfraFile_DeclaresAStorageAccount()
    {
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot, "infra"), "*.bicep", SearchOption.AllDirectories))
        {
            string bicep = File.ReadAllText(file);
            StorageAccountRegex().IsMatch(bicep).Should().BeFalse(
                $"{Path.GetFileName(file)} must not declare or reference a storage account under this governance posture");
        }
    }

    // ── Environment does not register Azure Files as managed-env storage ────

    [Fact]
    public void EnvironmentModule_DoesNotRegisterAzureFilesStorage()
    {
        string bicep = ReadInfra("modules/container-apps-env.bicep");

        ManagedEnvStoragesRegex().IsMatch(bicep).Should().BeFalse(
            "the managed environment must not register an Azure Files share (the account-key mount is blocked by policy)");
        bicep.Should().NotContain("listKeys(storageAccount",
            "no storage account key may be fetched for a file-share mount");
        bicep.Should().NotContain("azureFile",
            "no azureFile storage entry may be registered with the environment");
    }

    // ── API container mounts no Azure Files volume and forces no durable flags ─

    [Fact]
    public void ApiContainer_DoesNotMountAzureFilesVolume()
    {
        string bicep = ReadInfra("modules/container-apps.bicep");

        AzureFileVolumeRegex().IsMatch(bicep).Should().BeFalse(
            "no container app may mount an Azure Files volume under this governance posture");
        VolumeMountsRegex().IsMatch(bicep).Should().BeFalse(
            "the API must not declare a volumeMount for a removed durable share");
        bicep.Should().NotContain("/mnt/retailpulse-data",
            "the removed durable mount path must not linger in the container template");
    }

    [Fact]
    public void ApiContainer_DoesNotForceDurablePathFlags()
    {
        string bicep = ReadInfra("modules/container-apps.bicep");

        bicep.Should().NotContain("RETAIL_PULSE_DATA_DIRECTORY",
            "the deployed API must not be pinned to a data directory that no longer exists — it must use the temp fallback");
        bicep.Should().NotContain("RETAIL_PULSE_REQUIRE_DURABLE_STORAGE",
            "the deployed API must not force the durable-storage requirement without a policy-compatible durable path");
    }

    [Fact]
    public void ApiContainer_KeepsSingleWriterScale()
    {
        string bicep = ReadInfra("modules/container-apps.bicep");

        // Single-writer invariant is retained even without a share: one SQLite writer.
        bicep.Should().MatchRegex(@"maxReplicas:\s*1",
            "the API keeps a single replica so one SQLite writer owns the local stores");
    }

    // ── main.bicep emits none of the removed storage/durable outputs ────────

    [Theory]
    [InlineData("AZURE_STORAGE_ACCOUNT_NAME")]
    [InlineData("AZURE_FILE_SHARE_NAME")]
    [InlineData("RETAIL_PULSE_DATA_DIRECTORY")]
    [InlineData("RETAIL_PULSE_REQUIRE_DURABLE_STORAGE")]
    public void MainBicep_DoesNotEmitRemovedStorageOutput(string outputName)
    {
        string bicep = ReadInfra("main.bicep");
        var pattern = new Regex($@"output\s+{Regex.Escape(outputName)}\s+string\s*=", RegexOptions.Multiline);
        pattern.IsMatch(bicep).Should().BeFalse(
            $"main.bicep must not emit '{outputName}' — the durable Azure Files topology was removed");
    }

    // ── Hooks do not re-assert the removed durable env vars ─────────────────

    [Theory]
    [InlineData("postprovision.ps1")]
    [InlineData("postprovision.sh")]
    public void PostprovisionHook_DoesNotSetDurableEnvVars(string hookFile)
    {
        string script = ReadHook(hookFile);
        script.Should().NotContain("RETAIL_PULSE_DATA_DIRECTORY",
            $"{hookFile} must not re-assert a data directory the infra no longer provides");
        script.Should().NotContain("RETAIL_PULSE_REQUIRE_DURABLE_STORAGE",
            $"{hookFile} must not re-assert the durable-storage requirement without a policy-compatible path");
    }

    // ── Development temp fallback stays functional for the deployed demo ────

    [Fact]
    public void DevelopmentFallback_ResolvesToWritableTemp_WhenNoDurablePathConfigured()
    {
        // Mirrors the deployed synthetic demo: ASPNETCORE_ENVIRONMENT=Development,
        // no RETAIL_PULSE_DATA_DIRECTORY and no require flag. The resolver must land
        // on a writable per-machine temp directory rather than throwing or being
        // forced to a missing mount path.
        string resolved = DataDirectoryResolver.Resolve(
            configuredDirectory: null, isProduction: false, requireDurableStorage: false);

        resolved.Should().Be(
            Path.Combine(Path.GetTempPath(), DataDirectoryResolver.LocalFallbackFolderName),
            "the deployed Development demo must safely use its temp fallback with no durable volume");
        Directory.Exists(resolved).Should().BeTrue("the resolver creates and write-probes the fallback directory");
    }

    [Fact]
    public void ProductionFailClosed_IsPreserved_ForFutureAuthPr()
    {
        // The hotfix must NOT weaken the Production/required fail-closed behavior the
        // pending auth PR depends on. Production with no durable path still throws.
        Action prod = () => DataDirectoryResolver.Resolve(configuredDirectory: null, isProduction: true);
        prod.Should().Throw<InvalidOperationException>(
            "Production without a durable path must still fail closed so the auth PR can coordinate a real path");

        Action required = () => DataDirectoryResolver.Resolve(
            configuredDirectory: null, isProduction: false, requireDurableStorage: true);
        required.Should().Throw<InvalidOperationException>(
            "an explicit durable-storage requirement must still fail closed regardless of environment");
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
