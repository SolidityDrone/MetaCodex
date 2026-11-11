#!/usr/bin/env bash
# Usage: tests/sidebar/run-sidebar-tests.sh      (WSL; no Unity Editor, ~0.3 GB of Windows RAM, ~20 s)
#
# Compiles THREAD's sidebar data layer (unity/Assets/MetaCodex/Runtime/UI/CodeText.cs: syntax colours and diff rows;
# Runtime/AI/ThreadChanges.cs: the changed-file list from git and the timeline) plus tests/sidebar/SidebarTests with
# Unity's own Roslyn, then runs it on Unity's bundled .NET 8: tokenizer and diff checks, and real git repos made in
# $OUT (a repo with commits, edits, a deletion, an untracked file and a long line; a worktree-style branch; a folder
# that is no repo; a missing folder), read through the same shell script and wsl.exe path the Editor uses.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VER=$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")
DATA="/mnt/c/Program Files/Unity $VER/Editor/Data"
DOTNET="$DATA/NetCoreRuntime/dotnet.exe"
OUT="${MCX_CHECK_DIR:-/mnt/c/Users/${WIN_USER:-pnncl}/mcxcheck-sidebar}"
REF="${REF_PROJECT:-$ROOT/unity}"
NJ=$(ls -d "$REF"/Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll 2>/dev/null | head -1)
[[ -n "$NJ" ]] || { echo "no Newtonsoft.Json.dll under $REF/Library (set REF_PROJECT to a project with a Library)"; exit 1; }
mkdir -p "$OUT"
W() { wslpath -w "$1"; }
FW="$DATA/NetCoreRuntime/shared/Microsoft.NETCore.App/$(ls "$DATA/NetCoreRuntime/shared/Microsoft.NETCore.App" | sort -V | tail -1)"
cp "$NJ" "$OUT/Newtonsoft.Json.dll"
RSP="$OUT/sidebar.rsp"
{
  echo "-nologo -nostdlib -noconfig -target:exe -langversion:9 -nowarn:1701,1702,0168,0219"
  echo "-out:\"$(W "$OUT")\\SidebarTests.dll\""
  for f in "$FW"/*.dll; do b=$(basename "$f"); case "$b" in System.*|mscorlib.dll|netstandard.dll|Microsoft.CSharp.dll) [[ "$b" == *Native* ]] || echo "-r:\"$(W "$f")\"";; esac; done
  echo "-r:\"$(W "$OUT/Newtonsoft.Json.dll")\""
  for s in unity/Assets/MetaCodex/Runtime/UI/CodeText.cs unity/Assets/MetaCodex/Runtime/AI/ThreadChanges.cs tests/sidebar/SidebarTests/Program.cs; do echo "\"$(W "$ROOT/$s")\""; done
} > "$RSP"
cat > "$OUT/SidebarTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "$(basename "$FW")" } } }
JSON
rm -f "$OUT/SidebarTests.dll"
"$DOTNET" exec "$(W "$DATA/DotNetSdkRoslyn/csc.dll")" "@$(W "$RSP")" 2>&1 | tr -d '\r' | grep -E "error|warning CS0(16|17|18|19)" || true
[[ -f "$OUT/SidebarTests.dll" ]] || { echo "COMPILE FAILED"; exit 1; }
# the repos the git checks read (made here with WSL's git, read back by the program through wsl.exe like the Editor)
R="$OUT/repos"; rm -rf "$R"; mkdir -p "$R"
( cd "$R" && mkdir repo && cd repo && git init -q -b main && git config user.email t@t && git config user.name t \
  && printf 'using System;\n\nnamespace Demo\n{\n    public static class Calc\n    {\n%s    }\n}\n' "$(for i in $(seq 1 40); do printf '        public static int F%d(int x) => x + %d;\n' $i $i; done)" > Calc.cs \
  && printf 'old notes\nline two\n' > notes.txt && mkdir -p src && printf 'print("hi")\n' > src/app.py \
  && git add -A && git commit -q -m first \
  && sed -i 's/x + 7;/x * 7; \/\/ changed/; /F20(/d' Calc.cs && printf '        public static string Long() => "%s";\n' "$(printf 'x%.0s' $(seq 1 300))" >> Calc.cs \
  && git rm -q notes.txt && printf 'def main():\n    """doc"""\n    return 1\n' > src/new_tool.py )
( cd "$R" && git -C repo worktree add -q -b feat ../wt main && cd wt && sed -i 's/x + 3;/x + 33;/' Calc.cs && git commit -qam "wt change" && printf 'readme\n' > README.md )
mkdir -p "$R/plain" && printf 'not a repo\n' > "$R/plain/file.txt"
"$DOTNET" exec --runtimeconfig "$(W "$OUT/SidebarTests.runtimeconfig.json")" "$(W "$OUT/SidebarTests.dll")" "$R" 2>&1 | tr -d '\r'
