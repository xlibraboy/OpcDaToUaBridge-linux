#!/usr/bin/env bash
# dotnet publish step, run INSIDE the SDK container (no local dotnet SDK — see
# context.md). Called by scripts/msi/build-portable.sh:
#   container-step.sh publish
#
# Publishes the server for the portable zips only. The HMI and Designer publishes are
# done on the Windows runner, which is where the MSI is built
# (see .github/workflows/windows-release.yml).
set -euo pipefail

step="${1:?usage: container-step.sh publish}"

case "$step" in
  publish)
    dotnet publish src/OpcBridge.App/OpcBridge.App.csproj \
      -c Release -r win-x86 --self-contained true -o build/publish-x86
    dotnet publish src/OpcBridge.App/OpcBridge.App.csproj \
      -c Release -r win-x64 --self-contained true -o build/publish-x64
    ;;
  *)
    echo "unknown step: $step (expected publish)" >&2
    exit 1
    ;;
esac
