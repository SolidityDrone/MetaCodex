#!/usr/bin/env bash
# Usage: tools/sync-agent-skills.sh [meta-skills-dir]
#
# Refreshes userland/agent-skills/: third-party skills bundled into the headset's Linux at
# /root/.claude/skills (build-overlay.py), so agents there (Claude Code, opencode, MQH threads) can use
# them next to MetaCodex's own skills (quest-gesture-plugins, quest-scripting, metacodex-context).
#   unity/  all of https://github.com/Unity-Technologies/skills (fresh shallow clone; Unity Companion
#           License: "for Unity-dependent projects", which MetaCodex is)
#   meta/   the Meta skills useful at runtime (hand tracking, interaction, comfort, UI, placement), copied
#           from the vendored Unity + Quest skill set (Apache-2.0; upstream github.com/meta-quest/agentic-tools)
# Writes SOURCES.md (commits, dates, list) and the licence files. Downloads go to a fresh temp dir.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/userland/agent-skills"
META_SRC="${1:-$HOME/projects/unity-skill-set/skills}"
META_SET=(hz-immersive-designer hz-unity-meta-interaction-sdk hz-unity-meta-core-sdk hz-unity-meta-quest-ui
          hz-unity-placement hz-unity-code-review hz-unity-meta-mixed-reality-utility-kit)
TMP=$(mktemp -d /tmp/agent-skills-XXXXXX)
trap 'rm -rf "$TMP"' EXIT
git clone -q --depth 1 https://github.com/Unity-Technologies/skills.git "$TMP/unity"
UCOMMIT=$(git -C "$TMP/unity" log -1 --format='%H %cs')
rm -rf "$DEST/unity" "$DEST/meta"
mkdir -p "$DEST/unity" "$DEST/meta"
cp -r "$TMP/unity/skills/." "$DEST/unity/"
cp "$TMP/unity/LICENSE.md" "$DEST/LICENSE-unity-skills.md"
for s in "${META_SET[@]}"; do
  [[ -f "$META_SRC/$s/SKILL.md" ]] || { echo "missing Meta skill $s in $META_SRC" >&2; exit 1; }
  cp -r "$META_SRC/$s" "$DEST/meta/"
done
LIC="$(dirname "$META_SRC")/vendor/licenses/meta-agentic-tools-LICENSE"
[[ -f "$LIC" ]] && cp "$LIC" "$DEST/LICENSE-meta-agentic-tools" || echo "note: Meta licence file not found next to $META_SRC" >&2
find "$DEST" -name '.DS_Store' -delete
{
  echo "# Agent skills bundled into the headset's Linux (/root/.claude/skills)"
  echo
  echo "Refreshed by \`tools/sync-agent-skills.sh\` on $(date -u +%Y-%m-%d). Do not edit these copies; MetaCodex's own"
  echo "skills live in \`mqh/plugins/metacodex-guide/skills/\` (quest-gesture-plugins, quest-scripting, metacodex-context)."
  echo
  echo "| Set | Source | Version | Licence | Skills |"
  echo "|---|---|---|---|---|"
  echo "| unity | https://github.com/Unity-Technologies/skills | $UCOMMIT | Unity Companion License (LICENSE-unity-skills.md) | $(ls "$DEST/unity" | wc -l) |"
  echo "| meta | https://github.com/meta-quest/agentic-tools (via the vendored skill set) | see the skill set's vendor/lock.json | Apache-2.0 (LICENSE-meta-agentic-tools) | ${#META_SET[@]} |"
  echo
  echo "Unity: $(ls "$DEST/unity" | tr '\n' ' ')"
  echo
  echo "Meta: ${META_SET[*]}"
} > "$DEST/SOURCES.md"
echo "agent skills: $(ls "$DEST/unity" | wc -l) unity + ${#META_SET[@]} meta, $(du -sh "$DEST" | cut -f1)"
