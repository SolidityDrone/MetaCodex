#!/usr/bin/env bash
# Usage: tools/agent-script-test.sh ["request"] [expected panel]
#
# End-to-end check of the live-behaviour story with a real agent: a fresh Claude Code run (the user's own
# CLI login, nothing stored) gets ONLY the quest-scripting skill and a request, and must write the Lua
# script. The script lands in unity/Bench/agent/scripts with EXPECT = the panel that should open; the Unity
# UI self-test (MetaCodexUiTest.AgentScript) loads it without a rebuild and double-claps with simulated hands.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REQUEST="${1:-Make it so if I double clap, the Files panel opens.}"
EXPECT="${2:-files}"
W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT
mkdir -p "$W/.claude/skills/quest-scripting" "$W/mnt/quest/scripts"
cp "$ROOT/mqh/plugins/metacodex-guide/skills/quest-scripting/SKILL.md" "$W/.claude/skills/quest-scripting/"
cd "$W"
NOTE="(Test sandbox: the app's /mnt/quest/scripts folder is ./mnt/quest/scripts here; mcx-ui is not available, so skip the live test step.)"
timeout 300 claude -p "$REQUEST $NOTE" --allowedTools "Read,Write,Edit,Glob,Skill" --permission-mode acceptEdits --max-turns 10 > "$W/agent.log" 2>&1 || true
OUT="$ROOT/unity/Bench/agent/scripts"
rm -rf "$OUT"; mkdir -p "$OUT"
shopt -s nullglob
files=("$W"/mnt/quest/scripts/*.lua)
[[ ${#files[@]} -gt 0 ]] || { echo "agent wrote no script; its answer:"; tail -20 "$W/agent.log"; exit 1; }
cp "${files[@]}" "$OUT/"
echo "$EXPECT" > "$OUT/EXPECT"
cp "$W/agent.log" "$ROOT/unity/Bench/agent/agent.log"
echo "agent wrote: $(cd "$OUT" && ls *.lua)"; for f in "$OUT"/*.lua; do echo "--- $f"; cat "$f"; done
