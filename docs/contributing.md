# Contributing

This page covers the local setup steps that are not implied by simply cloning
the repo. The [README](../README.md#quick-start) covers the runtime
prerequisites (.NET 10 SDK, Node.js 20+, OpenAI credentials); this doc covers
the developer ergonomics that keep pull requests healthy.

## Pre-commit formatting hook

Retail Pulse ships a versioned pre-commit hook under [`.githooks/`](../.githooks)
that runs the same `dotnet format --verify-no-changes` check the CI `lint` job
enforces (see [`.github/workflows/ci.yml`](../.github/workflows/ci.yml)). The
CI job remains the authority; the hook is an early warning that catches
mechanical problems locally so a review round-trip is not spent on them.

### One-time setup after cloning

Hooks are not enabled by `git clone`. Run **one** of the following once per
clone:

```powershell
# Windows / PowerShell
pwsh scripts/setup-hooks.ps1
```

```bash
# Linux / macOS / Git Bash
./scripts/setup-hooks.sh
```

Or, equivalently, the one-liner both scripts run:

```bash
git config core.hooksPath .githooks
```

To confirm it worked:

```bash
git config --get core.hooksPath
# expected output: .githooks
```

### What the hook does

On every `git commit` it:

1. Collects the `.cs` files staged for the commit.
2. Normalizes any working-tree file whose only difference from the index is
   CRLF (Windows editors sometimes save new files with CRLF; `.gitattributes`
   forces LF in the index anyway, so this just lines the working tree up with
   what will be committed). Files with real unstaged edits are left alone.
3. Runs `dotnet format RetailPulse.slnx --verify-no-changes --verbosity diagnostic --include <staged files>`.

If the formatter finds violations, the commit is blocked and the offending
diagnostics are printed. If no `.cs` files are staged (docs, infra text,
config), the hook exits cleanly without invoking the formatter. If `dotnet` is
not on PATH the hook skips with a warning; CI still enforces.

### Bypass

```bash
git commit --no-verify
```

Legitimate reasons to bypass:

- Emergency hotfix where the CI signal is enough and the pre-commit round-trip
  cost is not.
- A local `dotnet` SDK that lags CI (e.g. mid-upgrade); a `--no-verify` commit
  lets CI be the arbiter.
- WIP commits on a private branch you plan to squash before opening a PR.
- Commits that touch no C# — the hook already no-ops, but `--no-verify` also
  skips any future hooks we add.

Bypassing does **not** skip CI. The `lint` job still runs on every push and
PR and will fail the build if the formatter finds anything.

### Line endings

CI runs on `ubuntu-latest`; contributors work on Windows, macOS, and Linux.
`.gitattributes` (`* text=auto eol=lf` plus explicit `text eol=lf` for shell
scripts and `.githooks/*`) forces LF in the repo, but a Windows editor can
still create a new file with CRLF in the working tree, which trips
`dotnet format`'s `ENDOFLINE` check even though the *committed* content is LF.

The pre-commit hook handles the common case (staged file matches index modulo
CR) by re-checking the file out from the index before verifying. If your file
has real content differences plus CRLF, fix the line endings explicitly:

```powershell
# Convert one file in-place to LF (PowerShell)
$content = Get-Content path/to/File.cs -Raw
[System.IO.File]::WriteAllText((Resolve-Path 'path/to/File.cs'), ($content -replace "`r`n", "`n"))
```

```bash
# Convert one file in-place to LF (bash / dos2unix)
dos2unix path/to/File.cs
# or
sed -i 's/\r$//' path/to/File.cs
```

Then `git add` and commit again.

### Troubleshooting

| Symptom | Fix |
|---------|-----|
| Hook did not run | `git config --get core.hooksPath` — must print `.githooks`. Re-run the setup script. |
| Hook runs but is slow on huge commits | Scoping is already applied; the first invocation warms the analyzer cache. Subsequent commits reuse it. Bypass with `--no-verify` for genuinely oversized commits. |
| `dotnet` not on PATH | Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or bypass with `--no-verify`; CI enforces on push. |
| Formatter flags `ENDOFLINE` after a normalization pass | The working tree has real unstaged edits with CRLF. Convert to LF (see above) and re-stage. |
