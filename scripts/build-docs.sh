#!/usr/bin/env bash
# Build the MkDocs documentation site (Material theme, search) into site/.
#
# The dashboard's Docs menu opens /docs/ in a new tab. The deployed site is
# served by the bridge from <app>/docs-site; Docker builds (Dockerfile.local /
# Dockerfile.deploy) do this automatically. For local/dotnet-run previews:
#   scripts/build-docs.sh && cp -r site/* <publish-or-bin>/docs-site/
set -euo pipefail
cd "$(dirname "$0")/.."

PY="${PYTHON:-python3}"
VENV_DIR="${VENV_DIR:-.venv-docs}"

if [ ! -x "$VENV_DIR/bin/mkdocs" ]; then
  "$PY" -m venv "$VENV_DIR"
  "$VENV_DIR/bin/pip" install --quiet mkdocs-material
fi

"$VENV_DIR/bin/mkdocs" build
echo "Docs built to site/ (served at /docs/ when copied to <app>/docs-site)."
