#!/usr/bin/env bash
# Usage: tests/voicefx/run-voicefx-tests.sh       (WSL; no Unity Editor; ~0.2 GB of Windows RAM for a few seconds)
#
# Compiles the Unity-free voice-output code (Runtime/AI/SpokenText.cs, Runtime/AI/VoiceFx.cs when present) plus
# tests/voicefx/*.cs with Unity's own Roslyn against netstandard 2.1 (the API Unity scripts get), then runs it on
# Unity's bundled .NET 8: the sentence splitter, spoken units, reply shortening, and the voice effect (levels, edge
# inputs, determinism, WAV, chain time). tests/terminal/run-core-tests.sh runs it too.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VER=$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")
DATA="/mnt/c/Program Files/Unity $VER/Editor/Data"
DOTNET="$DATA/NetCoreRuntime/dotnet.exe"
OUT="${MCX_CHECK_DIR:-/mnt/c/Users/${WIN_USER:-pnncl}/mcxcheck}/voicefx"
mkdir -p "$OUT"
W() { wslpath -w "$1"; }

if [[ -z "${MCX_SKIP_RAM_CHECK:-}" ]]; then
  avail=$(timeout 20 powershell.exe -NoProfile -Command "[int]((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue)" 2>/dev/null | tr -dc '0-9')
  if [[ -z "$avail" || "$avail" -lt 900 ]]; then echo "NOT STARTED: Windows has ${avail:-?} MB available (< 900)"; exit 1; fi
fi

SRC=(unity/Assets/MetaCodex/Runtime/AI/SpokenText.cs)
[[ -f "$ROOT/unity/Assets/MetaCodex/Runtime/AI/VoiceFx.cs" ]] && SRC+=(unity/Assets/MetaCodex/Runtime/AI/VoiceFx.cs)
for f in "$ROOT"/tests/voicefx/*.cs; do SRC+=("${f#$ROOT/}"); done
RSP="$OUT/voicefx.rsp"
{
  echo "/nostdlib /noconfig /nologo /target:exe /langversion:9 /nullable:disable /optimize+"
  echo "/out:\"$(W "$OUT")\\VoiceFxTests.dll\""
  echo "/r:\"$(W "$DATA/NetStandard/ref/2.1.0/netstandard.dll")\""
  for s in "${SRC[@]}"; do echo "\"$(W "$ROOT/$s")\""; done
} > "$RSP"
cat > "$OUT/VoiceFxTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "$(ls "$DATA/NetCoreRuntime/shared/Microsoft.NETCore.App" | sort -V | tail -1)" } } }
JSON

t0=$(date +%s.%N)
if ! "$DOTNET" exec "$(W "$DATA/DotNetSdkRoslyn/csc.dll")" "@$(W "$RSP")" 2>&1 | tr -d '\r' | grep -E "error|warning CS0(16|17|18|19)" ; then :; fi
[[ -f "$OUT/VoiceFxTests.dll" && "$OUT/VoiceFxTests.dll" -nt "$RSP" ]] || { echo "voicefx: COMPILE FAILED"; exit 1; }
echo "voicefx: compiled in $(echo "$(date +%s.%N) - $t0" | bc) s"
"$DOTNET" "$(W "$OUT/VoiceFxTests.dll")" "$@" 2>&1 | tr -d '\r'
exit "${PIPESTATUS[0]}"
