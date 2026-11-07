#!/usr/bin/env bash
# Usage: tools/mqh-build.sh [install|build|all] [turbo filters...]      (default: all)
#
# Builds MQH in a WSL-native mirror (~/mcxwork/mqh-build): pnpm on the Windows drive (/mnt/c) is far too
# slow (no hard links into the store, Defender scans). mqh/ in the repo stays the source of truth; the
# mirror is rsynced from it (node_modules, dist and caches are kept there). Low concurrency on purpose:
# the laptop also runs Unity and bb. Default targets: the MQH server, host daemon, CLI and the bundled plugins.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/mqh"
B="${MQH_BUILD_DIR:-$HOME/mcxwork/mqh-build}"
WHAT="${1:-all}"; shift || true
FILTERS=("$@"); [[ ${#FILTERS[@]} -eq 0 ]] && FILTERS=(@bb/server @bb/host-daemon @bb/cli @bb/bundled-plugins)
export COREPACK_ENABLE_DOWNLOAD_PROMPT=0
# install/build scripts must not see the real bb's variables (agent shells on this machine carry BB_*)
for v in $(env | grep -o '^\(BB\|MQH\)_[A-Z0-9_]*'); do unset "$v"; done
mkdir -p "$B"
rsync -a --delete --exclude node_modules --exclude dist --exclude .turbo --exclude '.bundled-stage-*' \
  --exclude 'tsconfig.tsbuildinfo' "$SRC/" "$B/"
# a workspace package removed from the source keeps its (excluded) node_modules in the mirror; drop it
for d in "$B"/apps/* "$B"/packages/* "$B"/plugins/* "$B"/tests/*; do
  [[ -d "$d" && ! -e "$SRC/${d#"$B"/}" ]] && rm -rf "$d"
done
cd "$B"
t0=$(date +%s)
if [[ "$WHAT" == install || "$WHAT" == all ]]; then
  nice corepack pnpm install --no-frozen-lockfile --child-concurrency=2 --network-concurrency=8 2>&1 | tail -15
  echo "install: $(( $(date +%s) - t0 )) s, node_modules $(du -sh node_modules 2>/dev/null | cut -f1)"
fi
if [[ "$WHAT" == build || "$WHAT" == all ]]; then
  t1=$(date +%s)
  args=(); for f in "${FILTERS[@]}"; do args+=(--filter="$f..."); done
  nice corepack pnpm exec turbo run build "${args[@]}" --concurrency=2 --output-logs=errors-only --summarize=false 2>&1 | tail -40
  echo "build: $(( $(date +%s) - t1 )) s"
fi
# the lockfile the install resolved goes back to the source (it changes when packages are removed)
cp -f "$B/pnpm-lock.yaml" "$SRC/pnpm-lock.yaml"
