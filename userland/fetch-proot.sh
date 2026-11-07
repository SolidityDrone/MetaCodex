#!/usr/bin/env bash
# Usage: userland/fetch-proot.sh            (run from anywhere; needs curl, ar, tar, sha256sum, patchelf)
#
# Fetches Termux's proot build (GPL-2.0) and its two libraries for Android arm64, renames them to
# lib*.so so the Android package manager unpacks them into nativeLibraryDir (the one app folder
# where exec() is allowed for targetSdk >= 29), and fixes the DT_NEEDED names to match.
# Output: unity/Assets/Plugins/Android/libs/arm64-v8a/{libproot.so,libproot-loader.so,libtalloc.so,
#         libandroid-shmem.so} + THIRD_PARTY_PROOT.md. Downloads go to $MCX_WORK/dl/<pkg>/ (untrusted).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${MCX_WORK:-$HOME/mcxwork}"
OUT="$ROOT/unity/Assets/Plugins/Android/libs/arm64-v8a"
REPO=https://packages-cf.termux.dev/apt/termux-main/pool/main
U=data/data/com.termux/files/usr

# name|path in pool|sha256 (pinned: change all three together)
PKGS=(
  "proot_5.1.107.102_aarch64|p/proot/proot_5.1.107.102_aarch64.deb|10a394b585c2fc2b2c8502544f4e5f1be581dc8bfff79e8ba62f16207423e030"
  "libtalloc_2.5.0_aarch64|libt/libtalloc/libtalloc_2.5.0_aarch64.deb|556591f43bb773ad8777e1a29522640866a55f95dab71914418b94a8c58ad5a7"
  "libandroid-shmem_0.7_aarch64|liba/libandroid-shmem/libandroid-shmem_0.7_aarch64.deb|0da3a24d558b93c92bcf8d611e0826a99ff96e396b148e6cdf33b47c47c57ff6"
)

command -v patchelf >/dev/null || { echo "patchelf missing (brew install patchelf)" >&2; exit 1; }
for spec in "${PKGS[@]}"; do
  IFS='|' read -r name path sha <<<"$spec"
  d="$WORK/dl/$name"
  if [[ ! -f "$d/pkg.deb" ]] || ! echo "$sha  $d/pkg.deb" | sha256sum -c --quiet 2>/dev/null; then
    rm -rf "$d"; mkdir -p "$d"
    curl -fsS -o "$d/pkg.deb" "$REPO/$path"
    echo "$sha  $d/pkg.deb" | sha256sum -c --quiet
  fi
  rm -rf "$d/x"; mkdir -p "$d/x"
  (cd "$d/x" && ar x ../pkg.deb && tar xf data.tar.*)
done

mkdir -p "$OUT"
P="$WORK/dl/proot_5.1.107.102_aarch64/x/$U"
T="$WORK/dl/libtalloc_2.5.0_aarch64/x/$U"
S="$WORK/dl/libandroid-shmem_0.7_aarch64/x/$U"
install -m 0755 "$P/bin/proot"             "$OUT/libproot.so"
install -m 0755 "$P/libexec/proot/loader"  "$OUT/libproot-loader.so"
install -m 0755 "$T/lib/libtalloc.so.2.5.0" "$OUT/libtalloc.so"
install -m 0755 "$S/lib/libandroid-shmem.so" "$OUT/libandroid-shmem.so"

patchelf --replace-needed libtalloc.so.2 libtalloc.so "$OUT/libproot.so"
patchelf --set-soname libtalloc.so "$OUT/libtalloc.so"
for f in libproot.so libtalloc.so libandroid-shmem.so; do patchelf --remove-rpath "$OUT/$f"; done

cat > "$OUT/THIRD_PARTY_PROOT.md" <<'EOF'
# Third-party binaries in this folder

| File | Upstream | Version | License |
|---|---|---|---|
| libproot.so | PRoot, Termux fork (https://github.com/termux/proot), `bin/proot` | 5.1.107.102 | GPL-2.0-or-later |
| libproot-loader.so | same package, `libexec/proot/loader` | 5.1.107.102 | GPL-2.0-or-later |
| libtalloc.so | talloc (https://talloc.samba.org) | 2.5.0 | LGPL-3.0-or-later |
| libandroid-shmem.so | https://github.com/termux/libandroid-shmem | 0.7 | BSD-3-Clause |

Taken unmodified from the Termux package repository (aarch64), except: file names changed to lib*.so,
DT_NEEDED libtalloc.so.2 -> libtalloc.so, RUNPATH removed (patchelf). They run as separate
executables / libraries; the source is available at the URLs above. Regenerate with userland/fetch-proot.sh.
EOF
ls -la "$OUT"
readelf -d "$OUT/libproot.so" | grep -E "NEEDED|RUNPATH" || true
