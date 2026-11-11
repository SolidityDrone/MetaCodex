#!/usr/bin/env bash
# Usage: tests/typing/run-typing-tests.sh [--long]      (WSL; no Unity Editor, ~0.3 GB of Windows RAM, ~15 s)
#
# Compiles the table keyboard's typing engine (unity/Assets/MetaCodex/Runtime/Typing: layout, press detector, key
# decoder; no Unity types) plus tests/typing/TypingTests with Unity's own Roslyn against netstandard 2.1, then runs
# it on Unity's bundled .NET 8: unit checks and a simulated typist under hand-tracking noise (a table of error
# rates, this engine against the height threshold it replaced). --long: 12 sittings instead of 5.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VER=$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")
DATA="/mnt/c/Program Files/Unity $VER/Editor/Data"
DOTNET="$DATA/NetCoreRuntime/dotnet.exe"
OUT="${MCX_CHECK_DIR:-/mnt/c/Users/${WIN_USER:-pnncl}/mcxcheck}"
mkdir -p "$OUT"
W() { wslpath -w "$1"; }

avail=$(timeout 20 powershell.exe -NoProfile -Command "[int]((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue)" 2>/dev/null | tr -dc '0-9')
if [[ -z "$avail" || "$avail" -lt 900 ]]; then echo "NOT STARTED: Windows has ${avail:-?} MB available (< 900)"; exit 1; fi

SRC=(
  unity/Assets/MetaCodex/Runtime/Typing/KeyboardLayout.cs
  unity/Assets/MetaCodex/Runtime/Typing/TapDetector.cs
  unity/Assets/MetaCodex/Runtime/Typing/KeyDecoder.cs
  tests/typing/TypingTests/Program.cs
)
RSP="$OUT/typing.rsp"
{
  echo "/nostdlib /noconfig /nologo /target:exe /langversion:9 /nullable:disable /optimize+"
  echo "/out:\"$(W "$OUT")\\TypingTests.dll\""
  echo "/r:\"$(W "$DATA/NetStandard/ref/2.1.0/netstandard.dll")\""
  for s in "${SRC[@]}"; do echo "\"$(W "$ROOT/$s")\""; done
} > "$RSP"
cat > "$OUT/TypingTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "$(ls "$DATA/NetCoreRuntime/shared/Microsoft.NETCore.App" | sort -V | tail -1)" } } }
JSON

rm -f "$OUT/TypingTests.dll"
if ! "$DOTNET" exec "$(W "$DATA/DotNetSdkRoslyn/csc.dll")" "@$(W "$RSP")" 2>&1 | tr -d '\r' | grep -E "error|warning CS0(16|17|18|19)" ; then :; fi
[[ -f "$OUT/TypingTests.dll" ]] || { echo "COMPILE FAILED"; exit 1; }
"$DOTNET" "$(W "$OUT/TypingTests.dll")" "$(W "$ROOT/unity/Assets/MetaCodex/Resources/Typing/letters.bytes")" "$@" 2>&1 | tr -d '\r'
