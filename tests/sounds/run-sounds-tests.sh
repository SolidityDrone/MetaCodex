#!/usr/bin/env bash
# Usage: tests/sounds/run-sounds-tests.sh [--verbose]     (WSL; no Unity Editor; ~0.2 GB of Windows RAM for a few seconds)
#
# Compiles the UI sounds (unity/Assets/MetaCodex/Runtime/UI/UiSoundSynth.cs, Unity-free) plus tests/sounds/*.cs with
# Unity's own Roslyn against netstandard 2.1 (the API Unity scripts get), runs it on Unity's bundled .NET 8: renders
# every sound, new and old (polish/base, OldSounds.cs), to WAV in MCX_SOUNDS_OUT (default: MetaCodex-sounds in the
# Windows user folder this checkout lives in: new/ and old/), prints a table, checks levels, fades, DC, durations,
# high partials, loudness, determinism and the time to make them. Tiered compilation is off: every method is compiled
# optimised at once, as IL2CPP's C++ is, so the times are not those of unoptimised first-pass code.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VER=$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")
DATA="/mnt/c/Program Files/Unity $VER/Editor/Data"
DOTNET="$DATA/NetCoreRuntime/dotnet.exe"
OUT="${MCX_CHECK_DIR:-/mnt/c/Users/${WIN_USER:-pnncl}/mcxcheck}/sounds"
mkdir -p "$OUT"
W() { wslpath -w "$1"; }

if [[ -z "${MCX_SKIP_RAM_CHECK:-}" ]]; then
  avail=$(timeout 20 powershell.exe -NoProfile -Command "[int]((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue)" 2>/dev/null | tr -dc '0-9')
  if [[ -z "$avail" || "$avail" -lt 900 ]]; then echo "NOT STARTED: Windows has ${avail:-?} MB available (< 900)"; exit 1; fi
fi

SRC=(unity/Assets/MetaCodex/Runtime/UI/UiSoundSynth.cs)
for f in "$ROOT"/tests/sounds/*.cs; do SRC+=("${f#$ROOT/}"); done
RSP="$OUT/sounds.rsp"
{
  echo "/nostdlib /noconfig /nologo /target:exe /langversion:9 /nullable:disable /optimize+"
  echo "/out:\"$(W "$OUT")\\SoundTests.dll\""
  echo "/r:\"$(W "$DATA/NetStandard/ref/2.1.0/netstandard.dll")\""
  for s in "${SRC[@]}"; do echo "\"$(W "$ROOT/$s")\""; done
} > "$RSP"
cat > "$OUT/SoundTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "$(ls "$DATA/NetCoreRuntime/shared/Microsoft.NETCore.App" | sort -V | tail -1)" },
  "configProperties": { "System.Runtime.TieredCompilation": false } } }
JSON

t0=$(date +%s.%N)
if ! "$DOTNET" exec "$(W "$DATA/DotNetSdkRoslyn/csc.dll")" "@$(W "$RSP")" 2>&1 | tr -d '\r' | grep -E "error|warning CS" | grep -v CS2023 ; then :; fi
[[ -f "$OUT/SoundTests.dll" && "$OUT/SoundTests.dll" -nt "$RSP" ]] || { echo "sounds: COMPILE FAILED"; exit 1; }
echo "sounds: compiled in $(echo "$(date +%s.%N) - $t0" | bc) s"
WINHOME=$(echo "$ROOT" | sed -n 's#^\(/mnt/[a-z]/Users/[^/]*\)/.*#\1#p')
WAVS="${MCX_SOUNDS_OUT:-${WINHOME:-$HOME}/MetaCodex-sounds}"
mkdir -p "$WAVS"
"$DOTNET" "$(W "$OUT/SoundTests.dll")" --out "$(W "$WAVS")" "$@" 2>&1 | tr -d '\r'
exit "${PIPESTATUS[0]}"
