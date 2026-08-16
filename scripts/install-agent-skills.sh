#!/usr/bin/env bash
# Restore the same project agent skills on any machine.
#
# What is shared via git:
#   skills-lock.json  — pinned skill names + source (dotnet/skills) + content hashes
#
# What stays local (gitignored):
#   .agents/skills/   — installed skill files
#   .pi/skills/       — agent-facing links/copies
#
# Usage (from repo root or any cwd):
#   ./scripts/install-agent-skills.sh
#   ./scripts/install-agent-skills.sh --omp-plugins   # also install matching OMP plugins
#
# Requires: Node.js (npx), network access to GitHub for first install/update.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ ! -f skills-lock.json ]]; then
  echo "error: skills-lock.json missing at $ROOT" >&2
  echo "This file pins which skills every PC should install. Commit it from a machine that already has the set." >&2
  exit 1
fi

if ! command -v npx >/dev/null 2>&1; then
  echo "error: npx not found — install Node.js first" >&2
  exit 1
fi

echo "==> Restoring project skills from skills-lock.json"
echo "    repo: $ROOT"
npx --yes skills experimental_install -y

# Optional: collapse duplicate Pi copies into symlinks (saves disk; same content)
if [[ -d .agents/skills && -d .pi/skills ]]; then
  for d in .pi/skills/*/; do
    [[ -d "$d" ]] || continue
    name="$(basename "$d")"
    if [[ -d ".agents/skills/$name" && ! -L ".pi/skills/$name" ]]; then
      rm -rf ".pi/skills/$name"
      ln -s "../../.agents/skills/$name" ".pi/skills/$name"
    fi
  done
fi

echo "==> Project skills now:"
npx skills list 2>/dev/null | head -80 || ls .agents/skills 2>/dev/null || true

if [[ "${1:-}" == "--omp-plugins" ]]; then
  if ! command -v omp >/dev/null 2>&1; then
    echo "warn: omp not on PATH — skip plugin install" >&2
  else
    echo "==> Ensuring OMP marketplace + plugins matching this project's skill set"
    omp plugin marketplace add dotnet/skills 2>/dev/null || true
    for p in \
      dotnet \
      dotnet-advanced \
      dotnet-aspnetcore \
      dotnet-diag \
      dotnet-msbuild \
      dotnet-nuget \
      dotnet-test \
      dotnet-upgrade
    do
      omp plugin install "${p}@dotnet-agent-skills" --scope user 2>&1 | tail -1 || true
    done
    omp plugin list 2>&1 | sed -n '/Marketplace Plugins/,+30p' || true
  fi
fi

echo
echo "Done. On each PC:"
echo "  1. git pull"
echo "  2. ./scripts/install-agent-skills.sh"
echo "  3. cd into this repo and start omp (project skills load from .agents/skills)"
echo
echo "To change the shared set later: install/remove project skills here, then commit the updated skills-lock.json."
