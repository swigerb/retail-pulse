#!/usr/bin/env bash
# Enables the versioned pre-commit hook in .githooks/ for this clone.
#
# Run once after cloning:
#     ./scripts/setup-hooks.sh
#
# Same effect as: git config core.hooksPath .githooks

set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

git config core.hooksPath .githooks

echo "pre-commit hook enabled (core.hooksPath = .githooks)."
echo "Bypass a single commit with:  git commit --no-verify"
echo "Details: docs/contributing.md#pre-commit-formatting-hook"
