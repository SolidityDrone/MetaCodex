#!/system/bin/sh
# Runs a script inside MetaCodex's own Linux (the app's proot + Debian rootfs), as the app's terminals do.
# From the PC, debuggable builds only:
#   adb shell run-as com.pnncl.metacodex env MCX_LIB=<lib/arm64> sh files/native/in-linux.sh /root/native/<script>.sh
# run-as has no view of the app's shared storage (/mnt/quest in the app's terminals): files go into the rootfs (/root/native).
# the app's native libraries (proot is one): run-as cannot list /data/app, so the caller passes it
# (adb shell dumpsys package com.pnncl.metacodex | grep legacyNativeLibraryDir, then /arm64)
LIB=${MCX_LIB:?set MCX_LIB to the lib/arm64 folder of the app}
FILES=/data/user/0/com.pnncl.metacodex/files
ROOTFS=$FILES/linux/rootfs
TMP=$FILES/linux/tmp
mkdir -p "$TMP"
cd "$FILES" || exit 1
PROOT_LOADER=$LIB/libproot-loader.so PROOT_TMP_DIR=$TMP LD_LIBRARY_PATH=$LIB HOME=$FILES TMPDIR=$TMP \
  "$LIB/libproot.so" --kill-on-exit --link2symlink -0 -r "$ROOTFS" \
  -b /dev -b /proc -b /sys -b "$ROOTFS/tmp:/dev/shm" -b /dev/urandom:/dev/random -b /proc/self/fd:/dev/fd \
  -w /root \
  /usr/bin/env -i HOME=/root USER=root LOGNAME=root SHELL=/bin/bash TERM=xterm-256color LANG=C.UTF-8 TMPDIR=/tmp \
  PATH=/root/.local/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin METACODEX=1 \
  /bin/bash -l "$1"
