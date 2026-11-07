#!/usr/bin/env bash
# Usage: tools/cscheck.sh            (WSL; no Editor; ~0.4 GB Windows RAM, ~10 s)
#
# Compiles MetaCodex's scripts with Unity's Roslyn using the reference list and defines of a real Editor
# compile: this project's own (Library/Bee/artifacts/*/Assembly-CSharp.rsp) once it has one, else
# SandyBridge's (same Unity, same package versions). Two passes for the runtime code: as the Editor sees
# it, and as the device build sees it (UNITY_EDITOR off, so #if UNITY_ANDROID && !UNITY_EDITOR compiles).
# Then the Editor scripts against the runtime output. Prints "check ok" or the first errors.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../unity" && pwd)" # the Unity project
VER=$(sed -n 's/^m_EditorVersion: //p' "$ROOT/ProjectSettings/ProjectVersion.txt")
DATA="/mnt/c/Program Files/Unity $VER/Editor/Data"
OUT="${MCX_CHECK_DIR:-/mnt/c/Users/${WIN_USER:-pnncl}/mcxcheck}"
mkdir -p "$OUT"
W() { wslpath -w "$1"; }

avail=$(timeout 20 powershell.exe -NoProfile -Command "[int]((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue)" 2>/dev/null | tr -dc '0-9')
if [[ -z "$avail" || "$avail" -lt 900 ]]; then echo "NOT STARTED: Windows has ${avail:-?} MB available (< 900)"; exit 1; fi

# *E.dag = the Editor's script compile, *P.dag = the player (device) build's
OWN=$(ls -t "$ROOT"/Library/Bee/artifacts/*E.dag/Assembly-CSharp.rsp 2>/dev/null | head -1 || true)
if [[ -n "$OWN" ]]; then BASE_PROJ="$ROOT"; else BASE_PROJ="${REF_PROJECT:-/mnt/c/Users/${WIN_USER:-pnncl}/SandyBridge}"; fi
RSP_RT=$(ls -t "$BASE_PROJ"/Library/Bee/artifacts/*E.dag/Assembly-CSharp.rsp | head -1)
RSP_ED="$(dirname "$RSP_RT")/Assembly-CSharp-Editor.rsp"
RSP_DEV=$(ls -t "$BASE_PROJ"/Library/Bee/artifacts/*P.dag/Assembly-CSharp.rsp 2>/dev/null | head -1 || true)
echo "references from: $(dirname "$RSP_RT")${RSP_DEV:+ and $(dirname "$RSP_DEV")}"

mapfile -t RUNTIME < <(cd "$ROOT" && find Assets -name '*.cs' -not -path '*/Editor/*' | sort)
mapfile -t EDITOR < <(cd "$ROOT" && find Assets -path '*/Editor/*' -name '*.cs' | sort)
# managed plugin DLLs of the project (e.g. MoonSharp): a fresh one is not in the Editor's rsp yet
mapfile -t PLUGIN_DLLS < <(cd "$ROOT" && find Assets/Plugins -name '*.dll' -not -path '*/Android/*' 2>/dev/null | sort)

opts() { # rsp without its sources, output and analyzers that point into the other project
  grep -v -E '^"|^-out:|^-refout:|^-analyzer:|^-additionalfile:|^/additionalfile:' "$1" | \
    grep -v -E '^-r:"?Library/Bee/artifacts/.*Assembly-CSharp\.ref\.dll' || true
}
make_rsp() { # rsp-in, out-name, editor-defines(yes|no), extra-ref, sources...
  local in="$1" name="$2" ed="$3" extra="$4"; shift 4
  {
    if [[ "$ed" == yes ]]; then opts "$in"; else opts "$in" | grep -v -E '^-define:UNITY_EDITOR'; fi
    echo "-nowarn:0618,0649,0414,0169"
    echo "-out:\"$(W "$OUT")\\$name.dll\""
    echo "-refout:\"$(W "$OUT")\\$name.ref.dll\""
    [[ -n "$extra" ]] && echo "-r:\"$(W "$OUT")\\$extra.ref.dll\""
    for d in "${PLUGIN_DLLS[@]}"; do echo "-r:\"$(W "$ROOT/$d")\""; done
    for s in "$@"; do echo "\"$(W "$ROOT/$s")\""; done
  } > "$OUT/$name.rsp"
}
run() {
  (cd "$BASE_PROJ" && timeout 300 nice "$DATA/NetCoreRuntime/dotnet.exe" exec "$(W "$DATA/DotNetSdkRoslyn/csc.dll")" /nostdlib /noconfig "@$(W "$OUT/$1.rsp")" 2>&1 | tr -d '\r')
}
check() { # name
  local r e
  r=$(run "$1" || true); e=$(echo "$r" | grep -c "error CS" || true)
  if [[ "$e" != 0 ]]; then { echo "$r" | grep "error CS" | head -12; } || true; echo "CHECK FAILED ($1: $e errors)"; exit 1; fi
}

make_rsp "$RSP_RT" McxRuntime yes "" "${RUNTIME[@]}";        check McxRuntime
if [[ -n "$RSP_DEV" ]]; then make_rsp "$RSP_DEV" McxRuntimeDevice yes "" "${RUNTIME[@]}"
else make_rsp "$RSP_RT" McxRuntimeDevice no "" "${RUNTIME[@]}"; fi
check McxRuntimeDevice
make_rsp "$RSP_ED" McxEditor yes McxRuntime "${EDITOR[@]}"; check McxEditor
echo "check ok (runtime as Editor + as device, editor scripts): ${#RUNTIME[@]} + ${#EDITOR[@]} files"
