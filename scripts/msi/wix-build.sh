#!/usr/bin/env bash
# WiX build step, executed INSIDE the SDK container by scripts/msi/build-msi.sh:
#   wix-build.sh <version>
# Only x86 MSIs are produced — a 64-bit process cannot load 32-bit OPC DA COM
# servers (Matrikon, MX Component) without DCOM surrogate setup.
set -euo pipefail

VERSION="${1:?usage: wix-build.sh <version>}"

# Defaults for the two ports the bridge listens on. They can be overridden with
# OPCBRIDGE_HTTP_PORT / OPCBRIDGE_UA_PORT, but note the app auto-assigns the next
# free port at runtime (Bridge:HttpPort / Bridge:OpcUaPort in appsettings.json), so
# the firewall rules and dashboard shortcut only match a bridge on these ports.
HTTP_PORT="${OPCBRIDGE_HTTP_PORT:-8080}"
UA_PORT="${OPCBRIDGE_UA_PORT:-4840}"

export PATH="$HOME/.dotnet/tools:$PATH"

if ! dotnet tool list --global | grep -q '^wix '; then
  dotnet tool install --global wix --version 5.0.2
fi

for extension in WixToolset.Util.wixext WixToolset.Firewall.wixext WixToolset.UI.wixext; do
  if ! wix extension list --global | grep -q "$extension"; then
    wix extension add --global "$extension"
  fi
done

mkdir -p dist
wix build \
  -arch x86 \
  -ext WixToolset.Util.wixext \
  -ext WixToolset.Firewall.wixext \
  -ext WixToolset.UI.wixext \
  -define "MsiVersion=$VERSION" \
  -d "HttpPort=$HTTP_PORT" \
  -d "UaPort=$UA_PORT" \
  packaging/msi/OpcBridge.wxs \
  build/wix/AppFiles-x86.wxs \
  build/wix/HmiFiles.wxs \
  build/wix/DesignerFiles.wxs \
  -o "dist/OpcBridge-$VERSION-win-x86.msi"

ls -la "dist/OpcBridge-$VERSION-win-x86.msi"
