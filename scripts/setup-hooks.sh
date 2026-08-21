#!/usr/bin/env bash
# Enables the versioned Git hooks in .githooks/ for this clone.
#
# Installs both the pre-commit (fast staged-file check) and pre-push
# (whole-solution CI-equivalent check) hooks by pointing core.hooksPath
# at the versioned .githooks/ directory. Any new hooks added there in
# the future are picked up automatically.
#
# Run once after cloning:
#     ./scripts/setup-hooks.sh
#
# Same effect as: git config core.hooksPath .githooks

set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

git config core.hooksPath .githooks

echo "Git hooks enabled (core.hooksPath = .githooks):"
echo "  pre-commit -> dotnet format on staged C# files (fast)"
echo "  pre-push   -> dotnet format on the whole solution (matches CI)"
echo ""
echo "Bypass a single commit with:  git commit --no-verify"
echo "Bypass a single push   with:  git push   --no-verify"
echo "Details: docs/contributing.md#pre-commit-and-pre-push-formatting-hooks"
