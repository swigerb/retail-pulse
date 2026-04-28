#!/usr/bin/env bash
#
# Retail Pulse — One-click local setup and launch
#
# Usage:
#   ./deploy/deploy.sh                              # Restore, install, and build
#   ./deploy/deploy.sh --start                       # Build and start everything
#   ./deploy/deploy.sh --start --api-key "sk-..."    # Configure key, build, start
#   ./deploy/deploy.sh --skip-build --start          # Start without rebuilding
#

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# ── Parse Arguments ──────────────────────────────────────────────────

SKIP_BUILD=false
START_ALL=false
API_KEY=""
ENDPOINT=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-build)  SKIP_BUILD=true; shift ;;
        --start)       START_ALL=true; shift ;;
        --api-key)     API_KEY="$2"; shift 2 ;;
        --endpoint)    ENDPOINT="$2"; shift 2 ;;
        -h|--help)
            echo "Usage: $0 [--start] [--skip-build] [--api-key KEY] [--endpoint URL]"
            exit 0
            ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

echo ""
echo "========================================"
echo "  Retail Pulse — Local Setup"
echo "========================================"
echo ""

# ── Prerequisites Check ──────────────────────────────────────────────

echo "[1/6] Checking prerequisites..."

if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
fi
echo "  .NET SDK: $(dotnet --version)"

if ! command -v node &>/dev/null; then
    echo "ERROR: Node.js not found. Install from https://nodejs.org/"
    exit 1
fi
echo "  Node.js:  $(node --version)"
echo "  npm:      $(npm --version)"

# ── API Key Configuration ────────────────────────────────────────────

if [[ -n "$API_KEY" ]]; then
    echo ""
    echo "[2/6] Configuring OpenAI API key..."

    pushd "$REPO_ROOT/src/RetailPulse.Api" > /dev/null
    dotnet user-secrets init 2>/dev/null || true
    dotnet user-secrets set "OpenAI:ApiKey" "$API_KEY"
    if [[ -n "$ENDPOINT" ]]; then
        dotnet user-secrets set "OpenAI:Endpoint" "$ENDPOINT"
    fi
    popd > /dev/null

    echo "  API key configured via user-secrets"
else
    echo ""
    echo "[2/6] Skipping API key setup (use --api-key to configure)"
fi

# ── NuGet Restore ────────────────────────────────────────────────────

echo ""
echo "[3/6] Restoring NuGet packages..."

dotnet restore "$REPO_ROOT/RetailPulse.slnx" --verbosity quiet
echo "  NuGet packages restored"

# ── npm Install ──────────────────────────────────────────────────────

echo ""
echo "[4/6] Installing npm packages..."

pushd "$REPO_ROOT/src/RetailPulse.Web" > /dev/null
npm install --silent 2>/dev/null
popd > /dev/null
echo "  npm packages installed (RetailPulse.Web)"

# ── Build ────────────────────────────────────────────────────────────

if [[ "$SKIP_BUILD" == false ]]; then
    echo ""
    echo "[5/6] Building solution..."

    dotnet build "$REPO_ROOT/RetailPulse.slnx" --configuration Release --verbosity quiet --no-restore
    echo "  Solution built successfully"
else
    echo ""
    echo "[5/6] Skipping build (--skip-build)"
fi

# ── Start Services ───────────────────────────────────────────────────

cleanup() {
    echo ""
    echo "Stopping services..."
    [[ -n "${ASPIRE_PID:-}" ]] && kill "$ASPIRE_PID" 2>/dev/null || true
    [[ -n "${WEB_PID:-}" ]] && kill "$WEB_PID" 2>/dev/null || true
    wait 2>/dev/null || true
    echo "Services stopped."
}

if [[ "$START_ALL" == true ]]; then
    echo ""
    echo "[6/6] Starting services..."
    echo ""

    trap cleanup EXIT INT TERM

    echo "  Starting Aspire AppHost (API :5100, MCP :5200, Frontend :5173)..."
    cd "$REPO_ROOT"
    dotnet run --project src/RetailPulse.AppHost --no-build --configuration Release &
    ASPIRE_PID=$!

    sleep 5

    echo "  Starting React frontend (:5173)..."
    cd "$REPO_ROOT/src/RetailPulse.Web"
    npm run dev &
    WEB_PID=$!

    sleep 3

    echo ""
    echo "========================================"
    echo "  Retail Pulse is running!"
    echo "========================================"
    echo ""
    echo "  Dashboard:        http://localhost:5173"
    echo "  API:              http://localhost:5100"
    echo "  MCP Server:       http://localhost:5200"
    echo "  Aspire Dashboard: check terminal for login URL"
    echo ""
    echo "  Press Ctrl+C to stop all services"
    echo ""

    wait
else
    echo ""
    echo "[6/6] Skipping service start (use --start to launch)"

    echo ""
    echo "========================================"
    echo "  Setup complete!"
    echo "========================================"
    echo ""
    echo "  To start manually:"
    echo "    Terminal 1: dotnet run --project src/RetailPulse.AppHost"
    echo "    Terminal 2: cd src/RetailPulse.Web && npm run dev"
    echo ""
    echo "  Then open: http://localhost:5173"
    echo ""
fi
