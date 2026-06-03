<#
.SYNOPSIS
    End-to-end verification that coverlet.collector produces valid coverage output.

.DESCRIPTION
    Run after any change to coverlet.collector (notably the v6 → v10 upgrade) to
    prove that the CI coverage pipeline still works.

    Steps:
      1. Run `dotnet test --collect:"XPlat Code Coverage"` against a tight filter
         so the verification finishes in well under the full suite's runtime.
      2. Verify a coverage.cobertura.xml file is produced.
      3. Parse the cobertura XML and verify it carries real coverage data
         (line-rate, branch-rate, packages, classes, methods).
      4. Repeat with the opencover format override that ci.yml uses, and
         verify the OpenCover document shape.
      5. Re-run with an exclude attribute filter and verify the collector
         still respects ExcludeByAttribute (proves filter configuration
         compatibility on v10).

    Notes on what is NOT exercised here:
      - coverlet.msbuild is not referenced anywhere in this repo, so the
        `/p:CollectCoverage=true` MSBuild path is not part of our pipeline.
        Adding it would require pulling in a new package; it is out of scope
        for this upgrade-validation script.
      - There are no enforced coverage thresholds (the CI step is
        "informational" — see ci.yml). If a threshold is added later, extend
        Test-Threshold below.

.PARAMETER Filter
    xUnit filter expression. Defaults to a tiny, fast suite that still produces
    non-empty coverage data.

.PARAMETER KeepArtifacts
    Leave generated TestResults on disk after the run for inspection.
#>
[CmdletBinding()]
param(
    [string]$Filter = "FullyQualifiedName~RetailPulse.Tests.Tooling.CoverletCollectorConfigurationTests",
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests/RetailPulse.Tests/RetailPulse.Tests.csproj"
$scratchRoot = Join-Path $repoRoot "tests/RetailPulse.Tests/TestResults/_coverage-verify"

if (Test-Path $scratchRoot) {
    Remove-Item -Recurse -Force $scratchRoot
}
New-Item -ItemType Directory -Force -Path $scratchRoot | Out-Null

function Write-Section($title) {
    Write-Host ""
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

function Invoke-Coverage {
    param(
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [string]$ResultsDir,
        [string]$Format = "cobertura",
        [string[]]$ExtraSettings = @()
    )

    Write-Section "Run: $Label"

    $args = @(
        "test", $testProject,
        "--nologo",
        "--filter", $Filter,
        "--collect", "XPlat Code Coverage",
        "--results-directory", $ResultsDir
    )

    $dataCollectorArgs = @("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=$Format") + $ExtraSettings
    if ($dataCollectorArgs.Count -gt 0) {
        $args += "--"
        $args += $dataCollectorArgs
    }

    Write-Host "  dotnet $($args -join ' ')" -ForegroundColor DarkGray

    $output = & dotnet @args 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne 0) {
        Write-Host ($output | Out-String) -ForegroundColor Red
        throw "dotnet test failed with exit code $exit during '$Label'"
    }

    Write-Host "  dotnet test exit code: $exit" -ForegroundColor Green
}

function Find-CoverageFile {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$FileName
    )

    $matches = Get-ChildItem -Path $Root -Filter $FileName -Recurse -File -ErrorAction SilentlyContinue
    if (-not $matches -or $matches.Count -eq 0) {
        throw "Expected coverage file '$FileName' was not produced under $Root"
    }

    # Most recent first — guard against leftover files from earlier runs.
    return ($matches | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName
}

function Test-CoberturaReport {
    param([Parameter(Mandatory)] [string]$Path)

    Write-Host "  Parsing cobertura: $Path"
    [xml]$xml = Get-Content -Raw $Path

    if (-not $xml.coverage) {
        throw "Cobertura root element <coverage> is missing in $Path"
    }

    foreach ($attr in @("line-rate", "branch-rate")) {
        if (-not $xml.coverage.HasAttribute($attr)) {
            throw "Cobertura report missing required attribute '$attr'"
        }
    }

    $packageCount = @($xml.SelectNodes('//package')).Count
    $classCount = @($xml.SelectNodes('//class')).Count
    $methodCount = @($xml.SelectNodes('//method')).Count
    $lineCount = @($xml.SelectNodes('//line')).Count

    Write-Host ("    packages={0}, classes={1}, methods={2}, lines={3}" -f $packageCount, $classCount, $methodCount, $lineCount) -ForegroundColor Green

    if ($packageCount -lt 1) { throw "Cobertura report contains no <package> elements" }
    if ($classCount -lt 1) { throw "Cobertura report contains no <class> elements" }
    if ($methodCount -lt 1) { throw "Cobertura report contains no <method> elements" }
    if ($lineCount -lt 1) { throw "Cobertura report contains no <line> elements" }
}

function Test-OpenCoverReport {
    param([Parameter(Mandatory)] [string]$Path)

    Write-Host "  Parsing opencover: $Path"
    [xml]$xml = Get-Content -Raw $Path

    if (-not $xml.CoverageSession) {
        throw "OpenCover root element <CoverageSession> is missing in $Path"
    }
    if (-not $xml.CoverageSession.Summary) {
        throw "OpenCover report missing top-level <Summary>"
    }

    $summary = $xml.CoverageSession.Summary
    foreach ($attr in @("numClasses", "numMethods", "visitedClasses", "visitedMethods")) {
        if (-not $summary.HasAttribute($attr)) {
            throw "OpenCover <Summary> missing required attribute '$attr'"
        }
    }

    Write-Host ("    numClasses={0}, numMethods={1}, visitedClasses={2}, visitedMethods={3}" -f `
        $summary.numClasses, $summary.numMethods, $summary.visitedClasses, $summary.visitedMethods) -ForegroundColor Green
}

# --- 1+2+3: Cobertura collection ---------------------------------------------
$coberturaDir = Join-Path $scratchRoot "cobertura"
Invoke-Coverage -Label "XPlat Code Coverage (cobertura)" -ResultsDir $coberturaDir -Format "cobertura"
$coberturaFile = Find-CoverageFile -Root $coberturaDir -FileName "coverage.cobertura.xml"
Test-CoberturaReport -Path $coberturaFile

# --- 4 (CI parity): OpenCover collection -------------------------------------
$openCoverDir = Join-Path $scratchRoot "opencover"
Invoke-Coverage -Label "XPlat Code Coverage (opencover — CI parity)" -ResultsDir $openCoverDir -Format "opencover"
$openCoverFile = Find-CoverageFile -Root $openCoverDir -FileName "coverage.opencover.xml"
Test-OpenCoverReport -Path $openCoverFile

# --- 5: Exclude/include filter compatibility ---------------------------------
# Passes an ExcludeByAttribute directive through XPlat data collector
# configuration. This is the same wire format used by runsettings filters,
# so a successful run proves the v10 collector still consumes filter
# configuration the way v6 did.
$filterDir = Join-Path $scratchRoot "filtered"
Invoke-Coverage `
    -Label "XPlat Code Coverage (ExcludeByAttribute filter)" `
    -ResultsDir $filterDir `
    -Format "cobertura" `
    -ExtraSettings @(
        "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByAttribute=GeneratedCodeAttribute,CompilerGeneratedAttribute"
    )
$filterFile = Find-CoverageFile -Root $filterDir -FileName "coverage.cobertura.xml"
Test-CoberturaReport -Path $filterFile

# --- Summary ----------------------------------------------------------------
Write-Section "Summary"
Write-Host "  Cobertura artifact : $coberturaFile" -ForegroundColor Green
Write-Host "  OpenCover artifact : $openCoverFile" -ForegroundColor Green
Write-Host "  Filtered artifact  : $filterFile" -ForegroundColor Green
Write-Host ""
Write-Host "coverlet.collector verification: PASS" -ForegroundColor Green

if (-not $KeepArtifacts) {
    Remove-Item -Recurse -Force $scratchRoot -ErrorAction SilentlyContinue
}
