#!/usr/bin/env bash
# Dispatcher for the build steps that must run INSIDE the SDK container
# (no local dotnet SDK — see context.md). Called by scripts/msi/build-msi.sh:
#   container-step.sh publish
#   container-step.sh wix <version>
set -euo pipefail

step="${1:?usage: container-step.sh <publish|wix> [args]}"

case "$step" in
  publish)
    dotnet publish src/OpcBridge.App/OpcBridge.App.csproj \
      -c Release -r win-x86 --self-contained true -o build/publish-x86
    dotnet publish src/OpcBridge.App/OpcBridge.App.csproj \
      -c Release -r win-x64 --self-contained true -o build/publish-x64
    # HMI apps have no COM dependency: ship them 64-bit.
    dotnet publish src/OpcBridge.Hmi/OpcBridge.Hmi.csproj \
      -c Release -r win-x64 --self-contained true -o build/publish-hmi
    dotnet publish src/OpcBridge.Hmi.Designer/OpcBridge.Hmi.Designer.csproj \
      -c Release -r win-x64 --self-contained true -o build/publish-designer
    ;;
  wix)
    shift
    bash /src/scripts/msi/wix-build.sh "$@"
    ;;
  *)
    echo "unknown step: $step (expected publish|wix)" >&2
    exit 1
    ;;
esac
