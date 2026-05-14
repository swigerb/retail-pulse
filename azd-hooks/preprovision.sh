#!/bin/bash
# Pre-provision hook — validates prerequisites before azd provision

set -e

echo "🔍 Checking prerequisites..."

# Check Azure CLI
if ! command -v az &> /dev/null; then
    echo "❌ Azure CLI (az) is not installed. Install from https://aka.ms/install-azure-cli"
    exit 1
fi
echo "✅ Azure CLI found"

# Check .NET SDK
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET SDK is not installed. Install from https://dot.net/download"
    exit 1
fi
echo "✅ .NET SDK found ($(dotnet --version))"

# Check Node.js
if ! command -v node &> /dev/null; then
    echo "❌ Node.js is not installed. Install from https://nodejs.org"
    exit 1
fi
echo "✅ Node.js found ($(node --version))"

# Build frontend for deployment
echo "📦 Building frontend..."
cd src/RetailPulse.Web
npm ci
npm run build
cd ../..
echo "✅ Frontend built successfully"

echo ""
echo "🚀 All prerequisites met. Proceeding with provisioning..."
