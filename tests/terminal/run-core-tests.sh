#!/usr/bin/env bash
# Usage: tests/terminal/run-core-tests.sh        (WSL; no Unity Editor needed, ~0.3 GB of Windows RAM for ~10 s)
#
# Compiles the Unity-free core (unity/Assets/MetaCodex/Runtime/{Terminal,Linux}) (terminal emulator, key encoder, rootfs extractor/setup, sessions) plus
# Tests/CoreTests with Unity's own Roslyn against netstandard 2.1 (the API Unity scripts get), then runs it
# on Unity's bundled .NET 8: unit checks, real-program replays (tmux reference), the real rootfs tarball.
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
  unity/Assets/MetaCodex/Runtime/Terminal/VtTerminal.cs
  unity/Assets/MetaCodex/Runtime/Terminal/Wcwidth.cs
  unity/Assets/MetaCodex/Runtime/Terminal/KeyEncoder.cs
  unity/Assets/MetaCodex/Runtime/Linux/TarGzExtractor.cs
  unity/Assets/MetaCodex/Runtime/Linux/RootfsSetup.cs
  unity/Assets/MetaCodex/Runtime/Linux/PtyNative.cs
  unity/Assets/MetaCodex/Runtime/Linux/TerminalSessions.cs
  tests/terminal/CoreTests/Program.cs
)
RSP="$OUT/core.rsp"
{
  echo "/nostdlib /noconfig /nologo /target:exe /langversion:9 /nullable:disable /optimize+"
  echo "/out:\"$(W "$OUT")\\CoreTests.dll\""
  echo "/r:\"$(W "$DATA/NetStandard/ref/2.1.0/netstandard.dll")\""
  for s in "${SRC[@]}"; do echo "\"$(W "$ROOT/$s")\""; done
} > "$RSP"
cat > "$OUT/CoreTests.runtimeconfig.json" <<EOF
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "$(ls "$DATA/NetCoreRuntime/shared/Microsoft.NETCore.App" | sort -V | tail -1)" } } }
EOF

t0=$(date +%s.%N)
if ! "$DOTNET" exec "$(W "$DATA/DotNetSdkRoslyn/csc.dll")" "@$(W "$RSP")" 2>&1 | tr -d '\r' | grep -E "error|warning CS0(16|17|18|19)" ; then :; fi
[[ -f "$OUT/CoreTests.dll" && "$OUT/CoreTests.dll" -nt "$RSP" ]] || { echo "COMPILE FAILED"; exit 1; }
echo "compiled in $(echo "$(date +%s.%N) - $t0" | bc) s"
"$DOTNET" "$(W "$OUT/CoreTests.dll")" "$(W "$ROOT/tests/terminal/corpus/cases")" \
  "$(W "$ROOT/unity/Assets/StreamingAssets/linux/rootfs.tar.gz")" "$(W "$ROOT/tests/terminal/corpus/synthetic.tar.gz")" \
  "$(W "$ROOT/unity/Assets/StreamingAssets/linux/overlay.tar.gz")" 2>&1 | tr -d '\r'
