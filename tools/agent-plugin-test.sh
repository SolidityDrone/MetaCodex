#!/usr/bin/env bash
# Usage: tools/agent-plugin-test.sh ["request"]
#
# One-shot check of the gesture-plugin story with a real agent: a fresh Claude Code run (the user's own CLI
# login, nothing stored) gets the same skills the headset's Linux has (/root/.claude/skills: the MetaCodex
# skills quest-gesture-plugins, quest-scripting, metacodex-context, plus the bundled Unity and Meta skills)
# and one request, and must write the plugin folder. It lands in unity/Bench/agent/plugins; the Unity UI
# self-test (MetaCodexUiTest.AgentPlugin) loads it without a rebuild and replays the story with simulated
# hands: right thumbs-up opens git, a left thumbs-up does not, a fast push of an open palm closes git. No example
# plugin is loaded there (as on a fresh headset), so the agent's plugin must bring its own thumbs-up pose.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REQUEST="${1:-Make me a gesture plugin: when I do a thumbs up with my right hand, open the git panel; when I push my open palm away from me fast, close it.}"
W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT
mkdir -p "$W/.claude/skills" "$W/mnt/quest/plugins" "$W/mnt/quest/scripts"
for s in quest-gesture-plugins quest-scripting metacodex-context; do cp -r "$ROOT/mqh/plugins/metacodex-guide/skills/$s" "$W/.claude/skills/"; done
for g in unity meta; do cp -r "$ROOT/userland/agent-skills/$g/." "$W/.claude/skills/"; done
cd "$W"
NOTE="(Test sandbox: the headset's /mnt/quest is ./mnt/quest here, so write the plugin under ./mnt/quest/plugins/. mcx-ui is not available: skip the live test steps; the gesture list would show only the 7 built-ins: no examples are installed, so bring your own poses.)"
timeout 600 claude -p "$REQUEST $NOTE" --allowedTools "Read,Write,Edit,Glob,Grep,Skill" --permission-mode acceptEdits --max-turns 20 > "$W/agent.log" 2>&1 || true
OUT="$ROOT/unity/Bench/agent/plugins"
rm -rf "$OUT"; mkdir -p "$OUT"
shopt -s nullglob
dirs=("$W"/mnt/quest/plugins/*/)
[[ ${#dirs[@]} -gt 0 ]] || { echo "agent wrote no plugin; its answer:"; tail -30 "$W/agent.log"; exit 1; }
for d in "${dirs[@]}"; do cp -r "$d" "$OUT/"; done
cp "$W/agent.log" "$ROOT/unity/Bench/agent/plugin-agent.log"
echo "agent wrote:"; (cd "$OUT" && find . -type f | sort)
for f in $(cd "$OUT" && find . -type f | sort); do echo "--- $f"; cat "$OUT/$f"; echo; done
echo "--- agent's answer"; tail -25 "$W/agent.log"
