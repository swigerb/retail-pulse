#!/bin/sh
# Pre-deploy hook (POSIX/sh) — serializes the shared-project build before
# azd's per-service deploy phase.
#
# See azd-hooks/predeploy.ps1 for the full root-cause writeup (issue #67):
# `azd up`/`azd deploy` publishes api/mcpserver/teamsbot in parallel and all
# three race on the same shared RetailPulse.ServiceDefaults obj/ output
# (specifically RetailPulse.ServiceDefaults.sourcelink.json), causing
# intermittent publish failures. Building the whole solution once, here,
# sequentially, before the parallel per-service publish starts eliminates the
# race: MSBuild sees the shared project as already up to date and skips
# regenerating it during each service's publish.

set -e

echo 'Pre-deploy: building the full solution once (sequential) to avoid a shared-project publish race...'

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(dirname "$script_dir")
solution="$repo_root/RetailPulse.slnx"

dotnet restore "$solution"
dotnet build "$solution" --no-restore --configuration Release

echo 'Pre-deploy: solution build complete. Parallel per-service publish can now proceed without racing on shared intermediate output.'
