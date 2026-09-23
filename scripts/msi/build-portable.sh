#!/usr/bin/env bash
# Build the OpcBridge portable Windows artifacts on Linux:
#   dist/OpcBridge-<version>-win-x86.zip   portable self-contained server build
#   dist/OpcBridge-<version>-win-x64.zip   portable self-contained server build (64-bit)
#
# The .msi installer is NOT built here: WiX cannot link an MSI on Linux (it reports
# WIX0000 and then fails to validate Directory/@Name), so the installer is built
# natively on a Windows runner by .github/workflows/windows-release.yml. That workflow
# also produces these same zips, so this script is for local iteration. The installer
# is x86 only — a 64-bit process cannot load 32-bit OPC DA COM servers (Matrikon, MX
# Component) without DCOM surrogate setup — which is why the x64 build ships as a
# portable zip for hosts that only use 64-bit or out-of-proc DA servers.
#
# Requirements: docker (no local dotnet SDK — see context.md), python3, the
# ~/.nuget-cache cache directory.
set -euo pipefail
cd "$(dirname "$0")/../.."

SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"
VERSION="$(python3 -c "import re;print(re.search(r'<Version>([^<]+)</Version>', open('Directory.Build.props').read()).group(1))")"
echo "==> Building OpcBridge $VERSION portable artifacts for Windows"

run_docker() {
  docker run --rm -v "$PWD":/src -w /src -v "$HOME/.nuget-cache":/home/build \
    -e HOME=/home/build -e DOTNET_CLI_HOME=/home/build \
    --user "$(id -u):$(id -g)" "$SDK_IMAGE" bash /src/scripts/msi/container-step.sh "$@"
}

echo "==> 1/2 dotnet publish (server win-x86 + win-x64)"
rm -rf build/publish-*
run_docker publish

echo "==> 2/2 portable zips"
python3 scripts/msi/make-zip.py build/publish-x86 "dist/OpcBridge-$VERSION-win-x86.zip"
python3 scripts/msi/make-zip.py build/publish-x64 "dist/OpcBridge-$VERSION-win-x64.zip"

echo "==> Artifacts:"
ls -la dist/OpcBridge-$VERSION-*
