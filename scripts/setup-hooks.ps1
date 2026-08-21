# Enables the versioned Git hooks in .githooks/ for this clone.
#
# Installs both the pre-commit (fast staged-file check) and pre-push
# (whole-solution CI-equivalent check) hooks by pointing core.hooksPath
# at the versioned .githooks/ directory. Any new hooks added there in
# the future are picked up automatically.
#
# Run once after cloning:
#     pwsh scripts/setup-hooks.ps1
#
# Same effect as: git config core.hooksPath .githooks

$ErrorActionPreference = 'Stop'

$repoRoot = git rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0) {
    throw "Not inside a git repository."
}

Push-Location $repoRoot
try {
    git config core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) {
        throw "git config core.hooksPath .githooks failed."
    }

    Write-Host "Git hooks enabled (core.hooksPath = .githooks):"
    Write-Host "  pre-commit -> dotnet format on staged C# files (fast)"
    Write-Host "  pre-push   -> dotnet format on the whole solution (matches CI)"
    Write-Host ""
    Write-Host "Bypass a single commit with:  git commit --no-verify"
    Write-Host "Bypass a single push   with:  git push   --no-verify"
    Write-Host "Details: docs/contributing.md#pre-commit-and-pre-push-formatting-hooks"
}
finally {
    Pop-Location
}
