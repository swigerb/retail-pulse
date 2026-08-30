# Contributing

This page covers the local setup steps that are not implied by simply cloning
the repo. The [README](../README.md#quick-start) covers the runtime
prerequisites (.NET 10 SDK, Node.js 20+, OpenAI credentials); this doc covers
the developer ergonomics that keep pull requests healthy.

## Branching model (dev-first)

All feature work branches from `dev` and targets `dev`. `main` carries released
code only, and the sole route into it is a promotion PR from `dev` (or an urgent
`hotfix/*`).

| Branch | Purpose | Accepts PRs from |
| --- | --- | --- |
| `main` | Released code | `dev`, `hotfix/*` |
| `dev` | Integration branch, where all feature work lands | any `squad/*` branch |

```bash
git checkout dev
git pull origin dev
git checkout -b squad/{issue-number}-{slug}
# work, commit, push
gh pr create --base dev --title "..." --body "Closes #{issue-number}"
```

Two mechanisms enforce this, because documentation alone did not:

1. **CI runs on `dev`.** [`ci.yml`](../.github/workflows/ci.yml) triggers on both
   `main` and `dev` for `push` and `pull_request`. Previously it ran on `main`
   only, so a PR into `dev` got no signal at all and every branch was pushed to
   `main` instead to obtain one. That single gap is how `dev` fell 51 commits
   behind.
2. **The `branch-policy` job fails PRs into `main`** whose head branch is not
   `dev` or `hotfix/*`. `gh pr create` defaults to the repository default branch,
   so without this guard the wrong base is one forgotten flag away.

Every CI job also carries an explicit `timeout-minutes`. A `Build & Test (.NET)`
run once hung for six hours before GitHub's own job cap cancelled it, which
consumed the runner budget and produced no signal.

## Pre-commit and pre-push formatting hooks

Retail Pulse ships two versioned Git hooks under
[`.githooks/`](../.githooks). Together they mirror what the CI `lint` job
enforces (see [`.github/workflows/ci.yml`](../.github/workflows/ci.yml)) so a
review round-trip is not spent on mechanical formatting problems. The CI
`Lint (dotnet format)` job remains the authority; the hooks are the early
warning.

| Hook | Scope | Typical runtime | Purpose |
|------|-------|-----------------|---------|
| `pre-commit` | staged `.cs` files (`dotnet format --include ...`) | ~6-8s | Fast per-commit gate; catches most drift at the point of edit. |
| `pre-push`   | whole solution (`dotnet format RetailPulse.slnx --verify-no-changes --verbosity diagnostic`) | ~30-40s | Same command CI runs; catches whole-solution diagnostics (import ordering, cross-file analyzer warnings) before the push leaves your machine. |

Timings above were measured on a warm developer workstation against the
current solution; first invocations are slower while the analyzer cache
warms.

### Why two hooks

`dotnet format --include <staged files>` scopes the analysis to the files
staged for that commit. This is deliberately fast but structurally
incomplete versus the CI job, which passes no `--include` and analyses the
whole compilation. Two classes of diagnostic slip through a staged-only
run:

1. **Whole-solution style analyzers** (e.g. import ordering, some
   `IDE00xx` diagnostics) inspect the full compilation. Their verdict on
   any one file can depend on symbols defined in another. A `--include`
   run may skip the diagnostic on files it does not touch.
2. **Commits that stage no `.cs` files** (docs-only, workflow YAML,
   configuration text) short-circuit the pre-commit hook, but CI still
   runs the full-solution formatter on the pushed content. Any latent
   drift already on your branch survives to CI.

The pre-push hook runs the exact CI command against the push range and
closes both gaps in one place: it fires at most once per push regardless
of how many commits the push contains, and it fires whether or not the
individual commits touched `.cs` files, as long as any commit in the push
range touches C#, `.csproj`, `.props`, `.targets`, `.editorconfig`,
`RetailPulse.slnx`, `global.json`, or `Directory.*.props`. Pushes that
touch none of those (docs-only) skip the check.

### One-time setup after cloning

Hooks are not enabled by `git clone`. Run **one** of the following once per
clone to install both the pre-commit and pre-push hooks:

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

### What each hook does

#### `pre-commit`

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

#### `pre-push`

On every `git push` it:

1. Walks the ranges being pushed (one per ref). Ref deletions and pushes
   whose commits touch no C# / build-graph files skip the check.
2. First-time pushes of a new branch always run the check — a new branch
   is a legitimate moment to verify the full solution.
3. Checks whether the tree it is about to analyse — your working tree — is
   identical to the tree CI will analyse (the pushed commit tree). It
   flags two cases: pushing a ref whose tip differs from your checked-out
   `HEAD` (for example `git push origin some-other-branch` or
   `git push --all`), and a working tree with uncommitted / untracked
   build-graph files. Both cases are surfaced as an advisory `NOTE:` line
   before the formatter runs and again in the success message; the hook
   does not block, because CI is already the authority on the pushed tree
   and blocking would break legitimate `git push --all` and unrelated-WIP
   pushes.
4. Runs `dotnet format RetailPulse.slnx --verify-no-changes --verbosity diagnostic` — byte-for-byte the CI command.

If the formatter finds violations, the push is blocked and the diagnostics
are printed. If `dotnet` is not on PATH the hook skips with a warning; CI
still enforces.

### What the hooks guarantee

If the pre-push hook completes without error, you do not pass
`--no-verify`, **and** the hook confirms tree identity in its success
message (working tree matches the pushed commit tree), the CI
`Lint (dotnet format)` job will pass on the same commits. The pre-push
hook is the load-bearing gate: it runs the exact CI command against the
whole solution; the pre-commit hook is a fast partial gate on top of it.
When tree identity does not hold, the hook still runs as an early
warning but explicitly downgrades its success message — CI is
authoritative for the pushed commit tree in that case.

The guarantee has four explicit boundaries. Documenting them here rather
than implying a stronger promise:

- **Bypass**: `git commit --no-verify` and `git push --no-verify` skip
  the hooks by design. CI will still enforce.
- **SDK drift**: your local `dotnet` SDK may resolve a different analyzer
  version than CI's `setup-dotnet`. When that happens the two verdicts can
  disagree; CI is authoritative.
- **PATH**: if `dotnet` is not on PATH when a hook runs, the hook exits 0
  with a warning rather than blocking the workflow, because a working
  install cannot be assumed. CI still enforces.
- **Tree identity**: the hook runs `dotnet format` against your **working
  tree**; CI runs it against the **pushed commit tree**. In the normal
  case (`git push` of your currently checked-out branch after committing,
  with a clean working tree) those two trees are byte-identical and the
  guarantee holds. They diverge when you push a ref whose tip is not your
  checked-out `HEAD` (for example `git push origin some-other-branch` or
  `git push --all`) or the working tree has uncommitted / untracked
  `.cs`, `.csproj`, `.props`, `.targets`, `.editorconfig`,
  `RetailPulse.slnx`, `global.json`, or `Directory.*.props` content. The
  hook detects both cases and prints an advisory `NOTE:` line plus an
  "advisory only" tag on the success message; CI is authoritative for
  the pushed commits. We chose the advisory over a fail-closed guard so
  the hook does not break legitimate multi-branch pushes or pushes with
  unrelated in-progress edits — CI already enforces the pushed tree.

### Bypass

```bash
git commit --no-verify
git push   --no-verify
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
| Pre-push takes ~30-40s | Expected — it runs the same whole-solution formatter CI runs. Bypass with `git push --no-verify` for genuinely emergency pushes; CI still enforces. |
| `dotnet` not on PATH | Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or bypass with `--no-verify`; CI enforces on push. |
| Formatter flags `ENDOFLINE` after a normalization pass | The working tree has real unstaged edits with CRLF. Convert to LF (see above) and re-stage. |
