#!/usr/bin/env bash
# Usage: Linux/fetch-rootfs.sh [tag]      (default tag: trixie-slim; needs curl, jq, sha256sum)
#
# Downloads the official Debian arm64 image (one layer = a plain rootfs tarball) from Docker Hub
# without docker, checks the layer's sha256 against its digest, and stores it where the app ships it:
#   Assets/StreamingAssets/linux/rootfs.tar.gz  +  rootfs.json (tag, digests, size)
# The app extracts it on first run (RootfsInstaller) and adds the Android fixes itself.
# Debian trixie = glibc 2.41, g++ 14 (QVAC's Linux arm64 build needs g++ >= 13).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${MCX_WORK:-$HOME/mcxwork}"
TAG="${1:-trixie-slim}"
REPO=library/debian
OUT="$ROOT/Assets/StreamingAssets/linux"
D="$WORK/dl/debian-$TAG-arm64"
rm -rf "$D"; mkdir -p "$D" "$OUT"

TOKEN=$(curl -fsS "https://auth.docker.io/token?service=registry.docker.io&scope=repository:$REPO:pull" | jq -r .token)
H=(-H "Authorization: Bearer $TOKEN")
ACCEPT='application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json'
curl -fsS "${H[@]}" -H "Accept: $ACCEPT" "https://registry-1.docker.io/v2/$REPO/manifests/$TAG" > "$D/index.json"
MDIGEST=$(jq -r '.manifests[] | select(.platform.architecture=="arm64" and .platform.os=="linux" and ((.platform.variant // "v8")=="v8")) | .digest' "$D/index.json" | head -1)
[[ -n "$MDIGEST" && "$MDIGEST" != null ]] || { echo "no linux/arm64 manifest for $TAG" >&2; exit 1; }
curl -fsS "${H[@]}" -H 'Accept: application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json' \
  "https://registry-1.docker.io/v2/$REPO/manifests/$MDIGEST" > "$D/manifest.json"
[[ "$(jq '.layers | length' "$D/manifest.json")" == 1 ]] || { echo "expected a single-layer image" >&2; exit 1; }
LDIGEST=$(jq -r '.layers[0].digest' "$D/manifest.json")
curl -fsSL "${H[@]}" -o "$D/layer.tar.gz" "https://registry-1.docker.io/v2/$REPO/blobs/$LDIGEST"
echo "${LDIGEST#sha256:}  $D/layer.tar.gz" | sha256sum -c --quiet
SIZE=$(stat -c %s "$D/layer.tar.gz")
ENTRIES=$(tar tzf "$D/layer.tar.gz" | wc -l)

cp "$D/layer.tar.gz" "$OUT/rootfs.tar.gz"
cat > "$OUT/rootfs.json" <<EOF
{
  "distro": "debian",
  "tag": "$TAG",
  "arch": "arm64",
  "manifestDigest": "$MDIGEST",
  "layerDigest": "$LDIGEST",
  "sizeBytes": $SIZE,
  "entries": $ENTRIES,
  "fetched": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF
cat "$OUT/rootfs.json"
