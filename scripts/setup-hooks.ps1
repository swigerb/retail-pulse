# Enables the versioned pre-commit hook in .githooks/ for this clone.
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
    Write-Host "pre-commit hook enabled (core.hooksPath = .githooks)."
    Write-Host "Bypass a single commit with:  git commit --no-verify"
    Write-Host "Details: docs/contributing.md#pre-commit-formatting-hook"
}
finally {
    Pop-Location
}
