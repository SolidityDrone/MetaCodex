#!/usr/bin/env bash
# Usage: tools/local-ai-models-pack.sh [--clean]      (WSL)
#
# Puts the local AI service's core models in the APK (feat/ready-on-install, owner 2026-11-13: no downloads at first
# start): the non-optional kinds of local-ai/server/models.js (asr Whisper base, llm Qwen3 0.6B, embed e5-small, tts
# Supertonic 3, ~680 MB), each checked against the catalogue's sha256, into
#   unity/Assets/StreamingAssets/qvac/models/<file>  +  qvac/models.json  (both gitignored: generated)
# At first start QvacHost copies them into <data>/models with their .sha256 markers (the service then counts them as
# installed). The optional local coder stays a download (AGENTS > LOCAL CODER).
# Source: $MCX_MODELS_DIR (default ~/mcxwork/qvac-native/models), e.g. the PC service's verified downloads.
# --clean removes them again (a lighter APK for development builds: then the models download as before).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/unity/Assets/StreamingAssets/qvac"
SRC="${MCX_MODELS_DIR:-$HOME/mcxwork/qvac-native/models}"
if [ "${1:-}" = "--clean" ]; then rm -rf "$OUT/models" "$OUT/models.meta" "$OUT/models.json" "$OUT/models.json.meta"; echo "models pack removed"; exit 0; fi
mkdir -p "$OUT/models"
python3 -I - "$ROOT/local-ai/server/models.js" "$SRC" "$OUT" <<'PY'
import hashlib, json, os, re, shutil, sys
js, src, out = sys.argv[1], sys.argv[2], sys.argv[3]
text = open(js, encoding="utf-8").read()
cat = text[text.index("const CATALOGUE = {"):text.index("\n}\n", text.index("const CATALOGUE = {"))]
entries = []
for m in re.finditer(r"\n  (\w+): \{(.*?)\n  \}", cat, re.S):
    kind, body = m.group(1), m.group(2)
    f = lambda k: (re.search(k + r": '([^']*)'", body) or [None, None])[1]
    if re.search(r"optional: true", body): continue
    entries.append({"kind": kind, "file": f("file"), "bytes": int(re.search(r"bytes: (\d+)", body).group(1)), "sha256": f("sha256")})
total = 0
for e in entries:
    s = os.path.realpath(os.path.join(src, e["file"]))
    if not os.path.isfile(s) or os.path.getsize(s) != e["bytes"]: sys.exit(f"missing or wrong size: {s} (want {e['bytes']} bytes)")
    h = hashlib.sha256()
    with open(s, "rb") as fh:
        for b in iter(lambda: fh.read(1 << 22), b""): h.update(b)
    if h.hexdigest() != e["sha256"]: sys.exit(f"sha256 mismatch: {s}")
    d = os.path.join(out, "models", e["file"])
    if not (os.path.isfile(d) and os.path.getsize(d) == e["bytes"]): shutil.copyfile(s, d)
    total += e["bytes"]
    print(f"  {e['kind']:6} {e['file']}  {e['bytes'] // 1048576} MB  ok")
json.dump({"models": entries, "bytes": total}, open(os.path.join(out, "models.json"), "w"), indent=2)
print(f"models pack: {len(entries)} models, {total // 1048576} MB")
PY
