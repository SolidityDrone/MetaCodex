#!/usr/bin/env bash
# Usage: tools/mqh-gut.sh <bb checkout> [dest]     (default dest: mqh/ of this repo)
#
# Makes MQH from an upstream bb checkout: copies it without history, then removes what a headset harness
# with a Unity client does not use (web/desktop/mobile apps, cloud/connect, browser/editor UI plugins, docs,
# brand assets, examples, e2e tests) and the web UI of the plugins we keep (tools/mqh-strip-ui.py). Rerun on a fresh upstream checkout to re-sync; our own changes live
# in later commits on top. Upstream commit and licence are recorded in dest/UPSTREAM.md.
set -euo pipefail
SRC="$(cd "${1:?bb checkout}" && pwd)"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="${2:-$ROOT/mqh}"
COMMIT=$(git -C "$SRC" log -1 --format='%H %ci')

DROP_APPS=(app web desktop mobile connect connect-next demo-server ai-gateway)
# web UI (MQH has no web client: Unity renders the data), remote access and cloud account, desktop shell
DROP_PACKAGES=(connect-db connect-db-pg mobile-bridge provider-parity
  shared-ui plugin-registry client-core desktop-contract query-tags
  connect-client frp-client tunnel-client tunnel-contract local-open-targets)
DROP_PLUGINS=(account-pool agent-annotations bb-ai browser-automation connect drafts environment-modal-sandbox
  github-star inline-vis keep-awake monaco-editor navigation pdf-preview plugin-api-tester push-notifications
  side-chat theme-preview thread-list
  bb-account connect-next plugin-api-docs composer scheduled-send prompt-library
  # bb-only or web-UI features MetaCodex does not use (2026-11-13; docs/mqh.md "Plugins"); bb-guide's MetaCodex
  # skills live in our own plugins/metacodex-guide
  bb-guide docs github mcp memory provider-pi provider-usage secrets storage-retention tasks)
# kept as source (the CLI's `thread show` compiles against its contract) but never registered, bundled or installed
UNREGISTER_PLUGINS=(github-pr-status)
DROP_TOP=(docs assets qa examples tests/integration tests/qa CHANGELOG.md changelog-metadata.ts changelog-parser.ts
  CONTRIBUTING.md SECURITY.md CLAUDE.md .github .bb .bb-env-setup.sh)
# ours, never taken from upstream (upstream's AGENTS.md/README.md describe bb)
OURS=(README.md AGENTS.md UPSTREAM.md)

mkdir -p "$DEST"
EXCL=(--exclude /.git --exclude node_modules)
for o in "${OURS[@]}"; do EXCL+=(--exclude "/$o"); done
for a in "${DROP_APPS[@]}"; do EXCL+=(--exclude "/apps/$a"); done
for p in "${DROP_PACKAGES[@]}"; do EXCL+=(--exclude "/packages/$p"); done
for p in "${DROP_PLUGINS[@]}"; do EXCL+=(--exclude "/plugins/$p"); done
for t in "${DROP_TOP[@]}"; do EXCL+=(--exclude "/$t"); done
# dropped parts are never copied (the rm loop below removes them from an older dest on a re-sync)
rsync -a --delete "${EXCL[@]}" "$SRC/" "$DEST/"
cd "$DEST"
for a in "${DROP_APPS[@]}"; do rm -rf "apps/$a"; done
for p in "${DROP_PACKAGES[@]}"; do rm -rf "packages/$p"; done
for p in "${DROP_PLUGINS[@]}"; do rm -rf "plugins/$p"; done
for t in "${DROP_TOP[@]}"; do rm -rf "$t"; done

cat > UPSTREAM.md <<EOF
# Upstream

MQH is a trimmed hard fork of bb, https://github.com/get-bb/bb (MIT licence, Copyright (c) 2026 Michael Yong,
see LICENSE). Forked from commit \`$COMMIT\` by \`tools/mqh-gut.sh\`.

Removed: apps ${DROP_APPS[*]}; packages ${DROP_PACKAGES[*]}; plugins ${DROP_PLUGINS[*]}; ${DROP_TOP[*]}.
EOF
python3 -I "$ROOT/tools/mqh-gut-patch.py" "$DEST" "${DROP_PLUGINS[@]}" "${UNREGISTER_PLUGINS[@]}"
python3 -I "$ROOT/tools/mqh-strip-ui.py" "$DEST" --apply
echo "mqh: $(find . -type f | wc -l) files from $COMMIT"
