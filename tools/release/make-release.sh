#!/usr/bin/env bash
# Usage: tools/release/make-release.sh        -> release/MetaCodex-<version>/ (APK, install scripts, INSTALL.md) + .zip
# Run after tools/unity-batch.sh BatchBuild. release/ is gitignored.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APK="$ROOT/unity/Builds/MetaCodex.apk"
[ -f "$APK" ] || { echo "no APK: build first (tools/unity-batch.sh BatchBuild)"; exit 1; }
VER=$(sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' "$ROOT/unity/Assets/MetaCodex/Editor/MetaCodexSetup.cs")
D="$ROOT/release/MetaCodex-$VER"
rm -rf "$D" && mkdir -p "$D"
cp "$APK" "$D/MetaCodex.apk"
cp "$ROOT/tools/release/install-metacodex.sh" "$ROOT/tools/release/install-metacodex.bat" "$ROOT/tools/release/INSTALL.md" "$D/"
chmod +x "$D/install-metacodex.sh"
( cd "$D" && sha256sum MetaCodex.apk > SHA256SUMS )
( cd "$ROOT/release" && rm -f "MetaCodex-$VER.zip" && python3 -I -m zipfile -c "MetaCodex-$VER.zip" "MetaCodex-$VER" )
echo "release/MetaCodex-$VER: $(( $(stat -c %s "$D/MetaCodex.apk") / 1048576 )) MB APK, install scripts, INSTALL.md; release/MetaCodex-$VER.zip"
