#!/usr/bin/env bash
# Usage: tools/userland-image.sh            (WSL, no sudo, no emulation; a few minutes, needs the network)
#
# Builds the Linux userland MetaCodex ships ready to use (feat/ready-on-install, owner 2026-11-13: "a judge must not
# wait for downloads and setup at first start"): the official Debian trixie-slim arm64 layer (userland/fetch-rootfs.sh)
# plus what MQH and the app need, so the first start downloads nothing:
#   - Node.js 24 LTS for linux-arm64 (nodejs.org, pinned version and sha256), unpacked in /usr/local: plain files,
#     ready at once (MQH starts on it)
#   - the Debian packages `mcx-setup` installed at first use (ca-certificates curl git xz-utils python3 procps less
#     nano) as arm64 .deb files in /var/cache/metacodex/base-debs: this PC's apt works out which ones the base layer
#     lacks (its own dpkg status, Debian's signed index, checked with the keyring from the layer) and downloads them,
#     each checked against the signed index's sha256. Then dpkg installs them inside the image, under proot +
#     qemu-aarch64 user mode (fetched with `apt-get download`, nothing installed on this PC): the image is complete.
#     If that fails (or MCX_IMAGE_EMULATE=0), the .deb files stay in the image and `mcx-setup` installs them on the
#     headset with dpkg, offline, in the background at the first start.
# Not in it (on request, as before): the agents (AGENTS > GET), the native app toolchain (`mcx-setup quest`, the
# first time a native app is made), the local coder.
# Output: unity/Assets/StreamingAssets/linux/rootfs.tar.gz (gitignored) + rootfs.json (tracked). The app installs it
# only where no userland is installed yet (LinuxHost: installed.json); an installed one is never replaced.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
W="${MCX_IMAGE_WORK:-$HOME/mcxwork/readyinstall}"
BASE="${MCX_IMAGE_BASE:-$W/base-trixie-slim.tar.gz}"      # the official layer as fetch-rootfs.sh stored it
BASE_INFO="${MCX_IMAGE_BASE_INFO:-$W/base-trixie-slim.json}"
OUT="${MCX_IMAGE_OUT:-$ROOT/unity/Assets/StreamingAssets/linux}"
PKGS="ca-certificates curl git xz-utils python3 procps less nano"
NODE_VER=24.22.3
NODE_SHA=6e51a2ada00f0011cc266f65d98922b403d7ad88f809ca2aa615a5fe1ce6e86c
DEBDIR=var/cache/metacodex/base-debs
say() { printf '\033[1;36m==> %s\033[0m\n' "$*"; }

[ -f "$BASE" ] || { echo "no base layer at $BASE: run userland/fetch-rootfs.sh, then copy its rootfs.tar.gz + rootfs.json there" >&2; exit 1; }
R="$W/rootfs"
say "base: $(basename "$BASE")"
rm -rf "$R" && mkdir -p "$R"
tar -xzf "$BASE" -C "$R" --no-same-owner

say "Debian packages (arm64) the base lacks: $PKGS"
A="$W/apt"
rm -rf "$A" && mkdir -p "$A/etc/apt/apt.conf.d" "$A/etc/apt/preferences.d" "$A/etc/apt/trusted.gpg.d" "$A/var/lib/apt/lists/partial" "$A/var/cache/apt/archives/partial" "$A/var/log/apt"
KEY="$R/usr/share/keyrings/debian-archive-keyring.gpg"
[ -f "$KEY" ] || KEY=$(ls "$R"/etc/apt/trusted.gpg.d/*.gpg 2>/dev/null | head -1)
[ -f "$KEY" ] || { echo "no Debian keyring in the base layer" >&2; exit 1; }
cat > "$A/etc/apt/sources.list" <<EOF
deb [arch=arm64 signed-by=$KEY] http://deb.debian.org/debian trixie main
deb [arch=arm64 signed-by=$KEY] http://deb.debian.org/debian trixie-updates main
deb [arch=arm64 signed-by=$KEY] http://deb.debian.org/debian-security trixie-security main
EOF
APTOPT=(-o Dir="$A" -o Dir::State::status="$R/var/lib/dpkg/status" -o APT::Architecture=arm64 -o "APT::Architectures::=arm64"
        -o Debug::NoLocking=1 -o Dir::Etc::Parts="$A/etc/apt/apt.conf.d" -o Dir::Etc::TrustedParts="$A/etc/apt/trusted.gpg.d"
        -o APT::Install-Recommends=false)
apt-get "${APTOPT[@]}" -qq update
apt-get "${APTOPT[@]}" -qq --print-uris -y install $PKGS > "$W/uris.txt"
# the sha256 of each file from Debian's signed Packages index (apt verified its signature at `update`; --print-uris
# shows only a weaker hash, or none)
python3 -I - "$A/var/lib/apt/lists" "$W/uris.txt" > "$W/uris-sha.txt" <<'PY'
import os, sys
lists, uris = sys.argv[1], sys.argv[2]
sha = {}
for f in os.listdir(lists):
    if not f.endswith("_Packages"): continue
    fn = h = None
    for line in open(os.path.join(lists, f), encoding="utf-8", errors="replace"):
        if line.startswith("Filename: "): fn = os.path.basename(line[10:].strip())
        elif line.startswith("SHA256: "): h = line[8:].strip()
        elif not line.strip():
            if fn and h: sha[fn] = h
            fn = h = None
    if fn and h: sha[fn] = h
for line in open(uris):
    parts = line.split()
    if len(parts) < 3: continue
    url = parts[0].strip("'")
    import urllib.parse
    file = urllib.parse.unquote(os.path.basename(urllib.parse.urlparse(url).path))  # the pool's name (no epoch, no %3a)
    if file not in sha: sys.exit("no sha256 in the index for " + file)
    print(url, file, parts[2], sha[file])
PY
mkdir -p "$R/$DEBDIR"
n=0
while read -r url file size sum; do
  [ -f "$W/debs/$file" ] && echo "$sum  $W/debs/$file" | sha256sum -c --quiet - 2>/dev/null || { mkdir -p "$W/debs"; curl -fsSL -o "$W/debs/$file" "$url"; }
  echo "$sum  $W/debs/$file" | sha256sum -c --quiet -
  cp "$W/debs/$file" "$R/$DEBDIR/$file"
  n=$((n + 1))
done < "$W/uris-sha.txt"
[ "$n" -gt 0 ] || { echo "apt found nothing to add (already in the base?)" >&2; }
ls "$R/$DEBDIR" > "$R/$DEBDIR/LIST"
say "$n packages, $(( $(du -sb "$R/$DEBDIR" | cut -f1) / 1048576 )) MB"

say "Node.js $NODE_VER (linux-arm64)"
NF="node-v$NODE_VER-linux-arm64.tar.xz"
[ -f "$W/$NF" ] || curl -fsSL -o "$W/$NF" "https://nodejs.org/dist/v$NODE_VER/$NF"
echo "$NODE_SHA  $W/$NF" | sha256sum -c --quiet -
tar -xJf "$W/$NF" -C "$R/usr/local" --strip-components=1 --no-same-owner
file "$R/usr/local/bin/node" | grep -q "ARM aarch64" || { echo "node is not arm64" >&2; exit 1; }

if [ "${MCX_IMAGE_EMULATE:-1}" = 1 ]; then
  T="$W/tools"
  mkdir -p "$T"
  # proot 5.5.0 (static, github.com/proot-me/proot, sha256 from its release's SHA256SUMS): 5.1 (Ubuntu's) does not
  # translate faccessat2, so under qemu dpkg's access check reached this PC's own /var/lib/dpkg and failed
  PROOT_URL=https://github.com/proot-me/proot/releases/download/v5.5.0/proot
  PROOT_SHA=db185300a9daabbce86aca7632d8c7fdc4249f6f153171954013b70c5f491ec0
  if ! echo "$PROOT_SHA  $T/proot-5.5.0" | sha256sum -c --quiet - 2>/dev/null; then
    say "tools: proot 5.5.0 (static)"
    curl -fsSL -o "$T/proot-5.5.0" "$PROOT_URL" && echo "$PROOT_SHA  $T/proot-5.5.0" | sha256sum -c --quiet - && chmod +x "$T/proot-5.5.0"
  fi
  if [ ! -x "$T/qemu-aarch64-static" ]; then
    say "tools: qemu-user-static (apt-get download, unpacked here, not installed)"
    ( cd "$T" && rm -f ./qemu-user-static*.deb && apt-get download qemu-user-static >/dev/null && dpkg-deb -x qemu-user-static*.deb x \
      && cp x/usr/bin/qemu-aarch64-static . && rm -rf x qemu-user-static*.deb )
  fi
  P() {
    PROOT_NO_SECCOMP=1 "$T/proot-5.5.0" -q "$T/qemu-aarch64-static" -0 -r "$R" -b /dev -b /proc -b /sys -w /root \
      /usr/bin/env -i HOME=/root LANG=C.UTF-8 DEBIAN_FRONTEND=noninteractive PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin "$@"
  }
  say "dpkg in the image (emulated arm64): $n packages"
  install -D -m 755 "$ROOT/userland/overlay/usr/local/sbin/mcx-install-debs" "$R/usr/local/sbin/mcx-install-debs"
  # in passes, as apt does: a package whose Pre-Depends are not configured yet waits for the next pass (dpkg -i takes
  # them in name order: python3-minimal came before python3.13-minimal, which it pre-depends on)
  if P /bin/sh -c "/usr/local/sbin/mcx-install-debs /$DEBDIR >/tmp/dpkg.log 2>&1 || { tail -20 /tmp/dpkg.log; exit 1; }" \
     && P /bin/sh -c 'node -v && npm -v && git --version && python3 --version && curl --version | head -1'; then
    rm -rf "$R/$DEBDIR" "$R"/tmp/* "$R/var/log/dpkg.log" "$R"/var/cache/debconf/*-old
    INSTALLED=1
  else
    echo "dpkg under emulation failed: the .deb files stay in the image (the headset installs them, offline)" >&2
    INSTALLED=0
  fi
else
  INSTALLED=0
fi

say "pack"
tmp="$OUT/rootfs.tar.gz.part"
tar --sort=name --numeric-owner --owner=0 --group=0 -C "$R" -cf - . | gzip -9 > "$tmp"
entries=$(tar -tzf "$tmp" | wc -l)
sha=$(sha256sum "$tmp" | cut -d' ' -f1)
size=$(stat -c %s "$tmp")
mv "$tmp" "$OUT/rootfs.tar.gz"
python3 -I - "$BASE_INFO" "$OUT/rootfs.json" "$sha" "$size" "$entries" "$PKGS" "$NODE_VER" "$n" "$INSTALLED" <<'PY'
import json, sys, datetime
base = json.load(open(sys.argv[1]))
out = {
    "distro": base.get("distro", "debian"), "tag": base.get("tag", "trixie-slim") + "+metacodex-base", "arch": base.get("arch", "arm64"),
    "layerDigest": "sha256:" + sys.argv[3], "sizeBytes": int(sys.argv[4]), "entries": int(sys.argv[5]),
    "base": {k: base.get(k) for k in ("tag", "manifestDigest", "layerDigest", "sizeBytes")},
    "packages": sys.argv[6].split(), "debs": int(sys.argv[8]), "installed": sys.argv[9] == "1", "node": sys.argv[7],
    "built": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}
json.dump(out, open(sys.argv[2], "w"), indent=2)
PY
say "rootfs.tar.gz: $((size / 1048576)) MB, $entries entries (base: $(( $(stat -c %s "$BASE") / 1048576 )) MB)"
