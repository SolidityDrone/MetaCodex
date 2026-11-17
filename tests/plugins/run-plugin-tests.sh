#!/usr/bin/env bash
# README
# ------
# Usage: tests/plugins/run-plugin-tests.sh [--verbose] [--rules] [--net] [--specs] [--scaffolds] [--examples] [<plugin-folder>...]
#        tests/plugins/run-plugin-tests.sh --scaffold <app|gesture|behaviour> <id> <dest> [name]
#   --examples    the shipped example plugins
#   --rules       the plugin project rules (folder + marker, kinds, ids, reload, create, delete): PluginHarness/Rules.cs
#   --net         the network API (net.get / net.post, plugin.json "network", limits, unload) and the weather example
#                 against canned replies: PluginHarness/NetTests.cs
#   --specs       the component library's spec rules (ViewSpecCheck): a window with every component passes, each broken
#                 spec is refused with a clear error, doubtful ones warn: PluginHarness/Specs.cs
#   --scaffolds   a new project of each kind, as the app makes it, through the scenario below
#   --scaffold    writes a new plugin project's files (the app's scaffold and its marker) into <dest> and stops
#   --example-project <example id> <dest>   the shipped example as the app adds it (+ ADD): its files, AGENTS.md, marker
#   REF_PROJECT=/mnt/c/Users/pnncl/MetaCodex/unity   a Unity project with a Library (for Newtonsoft.Json.dll)
#   MCX_CHECK_DIR=...        output folder (default /mnt/c/Users/pnncl/mcxcheck-plugins)
#   MCX_SKIP_RAM_CHECK=1     skip the Windows RAM guard (it refuses to start under 900 MB available)
# WSL only. No Unity Editor. A few hundred MB of Windows RAM for about a minute per plugin.
#
# A plugin folder holds plugin.json and main.lua (main.lua.txt is accepted and copied as main.lua). Its folder name is
# the plugin id. --examples takes every folder of unity/Assets/MetaCodex/Runtime/Scripting/Resources/PluginExamples.
# A plugin project (a folder with the marker .metacodex/project.json, {"type": "plugins", "kind": "app"}) is copied into
# a fresh $OUT/run/<n>.<id>/projects/<apps|gestures|behaviours>/<id>/ (its .git left out) and loaded the way the app
# loads /root/metacodex-projects/plugins; any other folder into $OUT/run/<n>.<id>/plugins/<id>/ (an older plugin folder,
# like /mnt/quest/plugins). The host's state: $OUT/run/<n>.<id>/scripts.
#
# What it compiles (Unity's own Roslyn, then runs on Unity's bundled .NET 8): the REAL Runtime/Scripting/ScriptHost.cs,
# LuaJson.cs, ViewSpecCheck.cs and Runtime/AI/VoiceCommands.cs, MoonSharp.Interpreter.dll and Newtonsoft.Json.dll, plus
# tests/plugins/PluginHarness: Stubs.cs (a minimal UnityEngine and MetaCodex.Hands), Fakes.cs (the fake app that records
# every call) and Program.cs (the scenario). No other app file is compiled.
#
# The scenario is the same for every plugin (no plugin-specific code); one line per check, "ok   ..." or "FAIL ...":
#  1. Load: new ScriptHost, Scan, Tick. The entry exists and has no Error.
#  2. The kind: a project's marker, else plugin.json "kind", else app. An app: at least 1 window and 1 tile; every tile
#     with a window points to one of the plugin's windows. A gesture or behaviour project: no tile (never shown).
#  3. Tap each tile; its window is open and got an "open" event; no Error.
#  4. Use every interactive thing in each window's last spec (every button, toggle, the first 3 tappable grid cells,
#     the first option of every choices, every input with the text "test 1 in 5 minutes"; the component library too,
#     inside cards, rows and tabs: checkbox (as a toggle), radio / segmented / select (two options), slider (middle and
#     max), stepper (up and down), field, date (3 days on), time, color, every tab, 3 list rows, 5 canvas shapes and a tap
#     on no shape), 0.1 s of app time between; then tap the notifications. The plugin must keep running.
# 4b. Gestures: every gesture it reacts to is a built-in or its own (else FAIL: a fresh headset has no example poses); a
#     gesture plugin defines or reacts to one at least. Each reaction is simulated (start and end, a clap's tap, each
#     sequence in order) and what it did is printed; then hand shapes (open, palm up, fist, point, pinch, thumbs up /
#     down, victory, fast moves) go through a small stand-in engine (Stubs.cs: built-ins from the hand's flags, poses
#     by curl and direction, Lua detectors) with both hands, then the right one: which of its gestures started, and what
#     that did.
#  5. 3 hours of fake wall clock and app time in 1 s steps. A window rebuilt in full more than 0.5 times a second on
#     average = FAIL (use a text component with an id for the countdown).
#  6. Voice: every command the plugin registered: VoiceCommands.Lint has no warning; it runs once with arguments from
#     its first sample (or phrase), or with no arguments; the plugin must not error.
#  7. Restart: a new fake app and a new ScriptHost on the same folders: same window ids, same tile ids, no Error, and
#     the plugin's store section of .state.json is unchanged.
#  8. Unload (SetEnabled false): all its windows removed, no tiles, no voice commands of it.
#  9. Log: any plugin log line starting with ERROR or warn, at any point, = FAIL.
# Network: no request leaves the PC. The fake fetcher answers from canned replies by URL, tests/plugins/fixtures/net/*.json
# (NetFixture in Fakes.cs: url prefix, status, json or body, delay, never); a request no fixture answers is a FAIL that
# names the URL (add a reply for it there, never to the plugin). A plugin that declares hosts: at most one request a
# minute on average in the 3 h with nobody touching it, none left in flight after the unload.
# Every spec the fake app receives is checked again with ViewSpecCheck (errors and warnings both count) and may have
# at most 16 components. --verbose prints each window's spec (compact JSON, cut at 600 characters).
# Exit code: the number of FAILs (at most 120); 0 = pass. A summary line per plugin: windows, tiles, voice commands,
# builds (full specs sent), text updates (LIVE texts), notifications, events sent to the host.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VER=$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")
DATA="/mnt/c/Program Files/Unity $VER/Editor/Data"
DOTNET="$DATA/NetCoreRuntime/dotnet.exe"
OUT="${MCX_CHECK_DIR:-/mnt/c/Users/${WIN_USER:-pnncl}/mcxcheck-plugins}"
REF="${REF_PROJECT:-$ROOT/unity}"
EXDIR="$ROOT/unity/Assets/MetaCodex/Runtime/Scripting/Resources/PluginExamples"
RT="$ROOT/unity/Assets/MetaCodex/Runtime"
W() { wslpath -w "$1"; }

VERBOSE=(); FOLDERS=(); EXAMPLES=0; RULES=(); NET=(); SPECS=(); SCAFFOLDS=0; SCAFFOLD=(); EXAMPLE_PROJECT=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --verbose) VERBOSE=(--verbose) ;;
    --examples) EXAMPLES=1 ;;
    --rules) RULES=(--rules) ;;
    --net) NET=(--net "$(wslpath -w "$EXDIR")") ;;
    --specs) SPECS=(--specs) ;;
    --scaffolds) SCAFFOLDS=1 ;;
    --example-project) [[ $# -ge 3 ]] || { echo "usage: $0 --example-project <example id> <dest>"; exit 2; }
                [[ -d "$EXDIR/$2" ]] || { echo "no example $2 in $EXDIR"; exit 2; }
                EXAMPLE_PROJECT=("$2" "$3"); shift 2 ;;
    --scaffold) [[ $# -ge 4 ]] || { echo "usage: $0 --scaffold <app|gesture|behaviour> <id> <dest> [name]"; exit 2; }
                SCAFFOLD=("$2" "$3" "$4" "${5:-$3}"); shift $(( $# >= 5 ? 4 : 3 )) ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) FOLDERS+=("$1") ;;
  esac
  shift
done
if [[ "$EXAMPLES" == 1 ]]; then
  [[ -d "$EXDIR" ]] || { echo "no examples folder: $EXDIR"; exit 1; }
  for d in "$EXDIR"/*/; do [[ -f "${d}plugin.json" ]] && FOLDERS+=("${d%/}"); done
fi
[[ ${#FOLDERS[@]} -gt 0 || ${#RULES[@]} -gt 0 || ${#NET[@]} -gt 0 || ${#SPECS[@]} -gt 0 || "$SCAFFOLDS" == 1 || ${#SCAFFOLD[@]} -gt 0 || ${#EXAMPLE_PROJECT[@]} -gt 0 ]] || { echo "usage: $0 [--verbose] [--rules] [--scaffolds] [--examples] [<plugin-folder>...] | --scaffold <kind> <id> <dest> [name]"; exit 2; }

if [[ -z "${MCX_SKIP_RAM_CHECK:-}" ]]; then
  avail=$(timeout 20 powershell.exe -NoProfile -Command "[int]((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue)" 2>/dev/null | tr -dc '0-9')
  if [[ -z "$avail" || "$avail" -lt 900 ]]; then echo "NOT STARTED: Windows has ${avail:-?} MB available (< 900)"; exit 1; fi
fi

NJ=$(ls -d "$REF"/Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll 2>/dev/null | head -1 || true)
[[ -n "$NJ" ]] || { echo "no Newtonsoft.Json.dll under $REF/Library (set REF_PROJECT to a project with a Library)"; exit 1; }
MS="$ROOT/unity/Assets/Plugins/MoonSharp/MoonSharp.Interpreter.dll"
[[ -f "$MS" ]] || { echo "missing $MS"; exit 1; }
mkdir -p "$OUT"
FW="$DATA/NetCoreRuntime/shared/Microsoft.NETCore.App/$(ls "$DATA/NetCoreRuntime/shared/Microsoft.NETCore.App" | sort -V | tail -1)"
cp "$NJ" "$OUT/Newtonsoft.Json.dll"
cp "$MS" "$OUT/MoonSharp.Interpreter.dll"

# compile
RSP="$OUT/plugins.rsp"
{
  echo "-nologo -nostdlib -noconfig -target:exe -langversion:9 -nowarn:1701,1702,1705,0168,0219,0649,0414"
  echo "-out:\"$(W "$OUT")\\PluginTests.dll\""
  for f in "$FW"/*.dll; do b=$(basename "$f"); case "$b" in System.*|mscorlib.dll|netstandard.dll|Microsoft.CSharp.dll) [[ "$b" == *Native* ]] || echo "-r:\"$(W "$f")\"";; esac; done
  echo "-r:\"$(W "$OUT/Newtonsoft.Json.dll")\""
  echo "-r:\"$(W "$OUT/MoonSharp.Interpreter.dll")\""
  for s in "$RT/Scripting/ScriptHost.cs" "$RT/Scripting/PluginProject.cs" "$RT/Scripting/LuaJson.cs" "$RT/Scripting/ViewSpecCheck.cs" "$RT/AI/VoiceCommands.cs" \
           "$ROOT/tests/plugins/PluginHarness/Stubs.cs" "$ROOT/tests/plugins/PluginHarness/Fakes.cs" "$ROOT/tests/plugins/PluginHarness/Program.cs" \
           "$ROOT/tests/plugins/PluginHarness/Rules.cs" "$ROOT/tests/plugins/PluginHarness/NetTests.cs" \
           "$ROOT/tests/plugins/PluginHarness/Specs.cs"; do
    echo "\"$(W "$s")\""
  done
} > "$RSP"
cat > "$OUT/PluginTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "$(basename "$FW")" } } }
JSON
rm -f "$OUT/PluginTests.dll"
"$DOTNET" exec "$(W "$DATA/DotNetSdkRoslyn/csc.dll")" "@$(W "$RSP")" 2>&1 | tr -d '\r' | grep -E "error|warning CS0(16|17|18|19)" || true
[[ -f "$OUT/PluginTests.dll" ]] || { echo "COMPILE FAILED"; exit 1; }

RUN() { "$DOTNET" exec --runtimeconfig "$(W "$OUT/PluginTests.runtimeconfig.json")" "$(W "$OUT/PluginTests.dll")" "$@" 2>&1 | tr -d '\r'; return "${PIPESTATUS[0]}"; }

# --scaffold: the app's files for a new project, made on the Windows side, then copied to <dest> (any WSL path)
if [[ ${#SCAFFOLD[@]} -gt 0 ]]; then
  tmp="$OUT/scaffold/${SCAFFOLD[1]}"; rm -rf "$tmp"
  RUN --scaffold "${SCAFFOLD[0]}" "${SCAFFOLD[1]}" "$(W "$tmp")" "${SCAFFOLD[3]}" || exit 1
  mkdir -p "${SCAFFOLD[2]}"; cp -r "$tmp"/. "${SCAFFOLD[2]}"/; chmod 444 "${SCAFFOLD[2]}/.metacodex/project.json"
  echo "scaffold: ${SCAFFOLD[2]}"; exit 0
fi

# --example-project: the example's files (main.lua.txt as main.lua), then the project's agent files and marker
if [[ ${#EXAMPLE_PROJECT[@]} -gt 0 ]]; then
  ex="${EXAMPLE_PROJECT[0]}"; tmp="$OUT/scaffold/$ex"; rm -rf "$tmp"; mkdir -p "$tmp"
  cp "$EXDIR/$ex/plugin.json" "$tmp/plugin.json"; cp "$EXDIR/$ex/main.lua.txt" "$tmp/main.lua"
  kind=$(sed -n 's/.*"kind": *"\([a-z]*\)".*/\1/p' "$tmp/plugin.json" | head -1); name=$(sed -n 's/.*"name": *"\([^"]*\)".*/\1/p' "$tmp/plugin.json" | head -1)
  RUN --scaffold "${kind:-app}" "$ex" "$(W "$tmp")" "${name:-$ex}" || exit 1
  mkdir -p "${EXAMPLE_PROJECT[1]}"; cp -r "$tmp"/. "${EXAMPLE_PROJECT[1]}"/; chmod 444 "${EXAMPLE_PROJECT[1]}/.metacodex/project.json"
  echo "example project: ${EXAMPLE_PROJECT[1]}"; exit 0
fi

# a fresh run folder: every plugin in its own plugins or projects dir (the folder name is the id) and its own state dir
chmod -R u+w "$OUT/run" 2>/dev/null || true
rm -rf "$OUT/run" "$OUT/scaffold"; mkdir -p "$OUT/run"
if [[ "$SCAFFOLDS" == 1 ]]; then
  for k in app gesture behaviour; do
    RUN --scaffold "$k" "new-$k" "$(W "$OUT/scaffold/new-$k")" "New $k" >/dev/null || { echo "scaffold $k failed"; exit 1; }
    FOLDERS+=("$OUT/scaffold/new-$k")
  done
fi
IDS=()
for f in "${FOLDERS[@]}"; do
  [[ -d "$f" ]] || { echo "not a folder: $f"; exit 1; }
  id=$(basename "$(cd "$f" && pwd)")
  [[ -f "$f/plugin.json" ]] || echo "warning: $f has no plugin.json (the host will not see it)"
  slot="${#IDS[@]}.$id" # its own folder even when two runs share an id (an example and a changed copy of it)
  if [[ -f "$f/.metacodex/project.json" ]]; then
    kind=$(sed -n 's/.*"kind": *"\([a-z]*\)".*/\1/p' "$f/.metacodex/project.json" | head -1)
    case "$kind" in app) kf=apps ;; gesture) kf=gestures ;; behaviour) kf=behaviours ;; *) kf=apps; echo "warning: $f: the marker has no known kind" ;; esac
    dest="$OUT/run/$slot/projects/$kf/$id"
  else
    dest="$OUT/run/$slot/plugins/$id"
  fi
  mkdir -p "$dest" "$OUT/run/$slot/scripts"
  (cd "$f" && tar cf - --exclude=.git .) | (cd "$dest" && tar xf -)
  chmod -R u+w "$dest"
  find "$dest" -name '*.meta' -delete
  if [[ -f "$dest/main.lua.txt" && ! -f "$dest/main.lua" ]]; then mv "$dest/main.lua.txt" "$dest/main.lua"; fi
  IDS+=("$slot")
done

set +e
RUN --run "$(W "$OUT/run")" --fixtures "$(W "$ROOT/tests/plugins/fixtures/net")" ${VERBOSE[@]+"${VERBOSE[@]}"} ${RULES[@]+"${RULES[@]}"} ${NET[@]+"${NET[@]}"} ${SPECS[@]+"${SPECS[@]}"} ${IDS[@]+"${IDS[@]}"}
exit $?
