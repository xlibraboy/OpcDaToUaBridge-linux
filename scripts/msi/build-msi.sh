#!/usr/bin/env bash
# Build the OpcBridge Windows release artifacts:
#   dist/OpcBridge-<version>-win-x86.msi   installer with a feature tree: Server
#                                          (service), HMI Runtime, Designer
#   dist/OpcBridge-<version>-win-x86.zip   portable self-contained server build
#   dist/OpcBridge-<version>-win-x64.zip   portable self-contained server build (64-bit)
#
# The MSI is x86 only: a 64-bit process cannot load 32-bit OPC DA COM servers
# (Matrikon, MX Component) without DCOM surrogate setup. The x64 build ships as a
# portable zip for hosts that only use 64-bit or out-of-proc DA servers.
#
# Requirements: docker (no local dotnet SDK — see context.md), python3, the
# ~/.nuget-cache cache directory.
set -euo pipefail
cd "$(dirname "$0")/../.."

SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"
VERSION="$(python3 -c "import re;print(re.search(r'<Version>([^<]+)</Version>', open('Directory.Build.props').read()).group(1))")"
echo "==> Building OpcBridge $VERSION for Windows"

run_docker() {
  docker run --rm -v "$PWD":/src -w /src -v "$HOME/.nuget-cache":/home/build \
    -e HOME=/home/build -e DOTNET_CLI_HOME=/home/build \
    --user "$(id -u):$(id -g)" "$SDK_IMAGE" bash /src/scripts/msi/container-step.sh "$@"
}

echo "==> 1/4 dotnet publish (server win-x86 + win-x64, HMI + Designer win-x64)"
rm -rf build/publish-*
run_docker publish

echo "==> 2/4 harvest published files into WiX fragments"
# Fragments are generated into build/ (gitignored), not the source tree. Each app's
# exe is skipped here and declared in OpcBridge.wxs, which is where the service
# (server) and the Start Menu shortcuts (HMI/Designer) attach to it.
mkdir -p build/wix
python3 scripts/msi/harvest.py build/publish-x86 build/wix/AppFiles-x86.wxs \
  INSTALLFOLDER AppFiles OpcBridge.App.exe
python3 scripts/msi/harvest.py build/publish-hmi build/wix/HmiFiles.wxs \
  HmiFolder HmiFiles OpcBridge.Hmi.exe
python3 scripts/msi/harvest.py build/publish-designer build/wix/DesignerFiles.wxs \
  DesignerFolder DesignerFiles OpcBridge.Hmi.Designer.exe

echo "==> 3/4 wix build (x86 MSI)"
run_docker wix "$VERSION"

echo "==> 4/4 portable zips"
python3 scripts/msi/make-zip.py build/publish-x86 "dist/OpcBridge-$VERSION-win-x86.zip"
python3 scripts/msi/make-zip.py build/publish-x64 "dist/OpcBridge-$VERSION-win-x64.zip"

echo "==> Artifacts:"
ls -la dist/OpcBridge-$VERSION-*
