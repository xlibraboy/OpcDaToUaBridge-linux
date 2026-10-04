#!/usr/bin/env bash
# Build the OpcBridge portable Windows artifacts on Linux:
#   dist/OpcBridge-<version>-win-x86.zip   portable self-contained server build
#   dist/OpcBridge-<version>-win-x64.zip   portable self-contained server build (64-bit)
#
# The .msi installers are NOT built here: WiX cannot link an MSI on Linux (it reports
# WIX0000 and then fails to validate Directory/@Name), so the installers are built
# natively on a Windows runner by .github/workflows/windows-release.yml. The Release
# carries those installers only (one per app; see packaging/msi/), so this script is how
# the portable zips are produced, for hosts deployed by extraction. The x86/x64 split
# follows the same rule as the installers: a 64-bit process cannot load 32-bit OPC DA
# COM servers, so a host whose DA servers are 32-bit takes win-x86 and a 64-bit-only
# host takes win-x64.
#
# Requirements: docker (no local dotnet SDK — see context.md), python3, the
# ~/.nuget-cache cache directory.
set -euo pipefail
cd "$(dirname "$0")/../.."

SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"
VERSION="$(python3 -c "import re;print(re.search(r'<ServerVersion>([^<]+)</ServerVersion>', open('Directory.Build.props').read()).group(1))")"
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
