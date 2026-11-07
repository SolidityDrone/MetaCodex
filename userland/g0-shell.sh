#!/usr/bin/env bash
# Usage: userland/g0-shell.sh [stage...]      stages: facts push extract smoke setup agents qvac pty  (default: all)
#
# Gate G0 without an APK: runs the app's own proot + Debian rootfs on the headset from `adb shell`
# (/data/local/tmp allows exec for the shell user), so the userland, apt, Node and the agent CLIs are
# proven on the Quest kernel before any Unity build. The app domain differs from the shell domain
# (SELinux exec rules, seccomp), which only the APK test can settle. Every result line starts with "G0".
# Needs: headset on adb (USB once, or Wi-Fi adb), the skill set's bin/adb on PATH, ~1 GB free on the headset.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
D=/data/local/tmp/mcx
LIB="$ROOT/unity/Assets/Plugins/Android/libs/arm64-v8a"
STAGES=("$@"); [[ ${#STAGES[@]} -eq 0 ]] && STAGES=(facts push extract smoke setup agents qvac pty)
has() { local s; for s in "${STAGES[@]}"; do [[ "$s" == "$1" ]] && return 0; done; return 1; }
sh() { adb shell "$@" 2>&1 | tr -d '\r'; }
now() { date +%s.%N; }
took() { printf '%.1f' "$(echo "$(now) - $1" | bc)"; }

PROOT="PROOT_LOADER=$D/lib/libproot-loader.so PROOT_TMP_DIR=$D/tmp LD_LIBRARY_PATH=$D/lib $D/lib/libproot.so --kill-on-exit --link2symlink -0 -r $D/rootfs -b /dev -b /proc -b /sys -b $D/rootfs/tmp:/dev/shm -b /proc/self/fd:/dev/fd -w /root /usr/bin/env -i HOME=/root USER=root TERM=xterm-256color LANG=C.UTF-8 TMPDIR=/tmp PATH=/root/.local/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
guest() { sh "$PROOT /bin/bash -lc '$1'"; } # $1 must not contain single quotes

[[ "$(adb get-state 2>/dev/null | tr -d '\r')" == device ]] || { echo "G0 no device on adb (plug the headset in once, accept USB debugging)"; exit 2; }

if has facts; then
  echo "G0 facts: model $(sh getprop ro.product.model) | android $(sh getprop ro.build.version.release) sdk $(sh getprop ro.build.version.sdk) | build $(sh getprop ro.build.display.id)"
  echo "G0 facts: kernel $(sh uname -r) | cpus $(sh nproc) | $(sh "cat /proc/meminfo | head -3 | tr -s ' ' | tr '\n' ' '")"
  echo "G0 facts: gpu libs: $(sh "ls /vendor/lib64 /vendor/lib64/hw /vendor/lib64/egl 2>/dev/null | grep -i -E 'opencl|vulkan|adreno' | sort -u | tr '\n' ' '")"
  echo "G0 facts: public libs: $(sh "cat /vendor/etc/public.libraries.txt 2>/dev/null | tr '\n' ' '")"
  echo "G0 facts: phantom monitor=$(sh settings get global settings_enable_monitor_phantom_procs) max_phantom=$(sh device_config get activity_manager max_phantom_processes)"
  echo "G0 facts: storage $(sh "df -h /data | tail -1 | tr -s ' '")"
fi

if has push; then
  t=$(now)
  sh "rm -rf $D && mkdir -p $D/lib $D/tmp"
  for f in "$LIB"/*.so; do adb push "$f" "$D/lib/" >/dev/null; done
  adb push "$ROOT/native/pty/out/android/pty_test" "$D/" >/dev/null
  adb push "$ROOT/unity/Assets/StreamingAssets/linux/rootfs.tar.gz" "$D/" >/dev/null
  adb push "$ROOT/unity/Assets/StreamingAssets/linux/overlay.tar.gz" "$D/" >/dev/null
  sh "chmod 755 $D/lib/*.so $D/pty_test"
  echo "G0 push: $(took $t) s"
fi

if has extract; then
  t=$(now)
  out=$(sh "cd $D && rm -rf rootfs && mkdir rootfs && (gzip -dc rootfs.tar.gz | tar -x -C rootfs) 2>&1 | tail -3; (gzip -dc overlay.tar.gz | tar -x -C rootfs) 2>&1 | tail -2; ls rootfs | tr '\n' ' '")
  echo "G0 extract: $(took $t) s: $out"
  sh "printf 'nameserver 1.1.1.1\nnameserver 8.8.8.8\n' > $D/rootfs/etc/resolv.conf; printf '127.0.0.1 localhost metacodex\n' > $D/rootfs/etc/hosts; mkdir -p $D/rootfs/etc/apt/apt.conf.d; printf 'APT::Sandbox::User \"root\";\n' > $D/rootfs/etc/apt/apt.conf.d/99metacodex; chmod 777 $D/rootfs/tmp"
fi

if has smoke; then
  t=$(now)
  echo "G0 smoke ($(took $t) s): $(guest 'head -1 /etc/os-release; uname -m; id -u; echo PROOT-OK' | tr '\n' ' ')"
  echo "G0 proc readable (shell domain): $(sh 'for f in /proc/stat /proc/loadavg /proc/uptime /proc/version /proc/vmstat; do [ -r $f ] && cat $f >/dev/null 2>&1 && printf "%s=ok " $f || printf "%s=NO " $f; done')"
fi

if has setup; then
  t=$(now); r=$(guest 'mcx-setup 2>&1 | tail -4'); echo "G0 mcx-setup base (apt + node 24): $(took $t) s: $(echo "$r" | tr '\n' ' ')"
  echo "G0 node cpus/mem: $(guest 'node -e "const os=require(\"os\");console.log(os.cpus().length, \"cpus\", Math.round(os.totalmem()/1048576), \"MB\", os.loadavg())"')"
  echo "G0 node https: $(guest 'node -e "fetch(\"https://registry.npmjs.org/\").then(r=>console.log(\"fetch\",r.status)).catch(e=>console.log(\"fetch failed\",e.message))"')"
fi

if has agents; then
  t=$(now); r=$(guest 'mcx-setup agents 2>&1 | tail -4'); echo "G0 mcx-setup agents: $(took $t) s: $(echo "$r" | tr '\n' ' ')"
  echo "G0 agent binaries: $(guest 'ls -la $(command -v claude) $(command -v opencode) 2>&1 | tr -s " " | cut -d" " -f9- | tr "\n" " "')"
  echo "G0 du: $(guest 'du -sh /usr /root 2>/dev/null | tr "\n" " "')"
fi

if has qvac; then
  t=$(now); r=$(guest 'mcx-setup qvac 2>&1 | tail -4'); echo "G0 mcx-setup qvac: $(took $t) s: $(echo "$r" | tr '\n' ' ')"
  echo "G0 qvac addons: $(guest 'cd /root/qvac && ls node_modules/@qvac | tr "\n" " "; ls node_modules | grep -E "^bare-runtime-" | tr "\n" " "')"
  echo "G0 qvac import: $(guest 'cd /root/qvac && timeout 60 node -e "import(\"@qvac/sdk\").then(m=>console.log(\"exports\",Object.keys(m).length)).catch(e=>console.log(\"import failed\",e.message))"')"
fi

if has pty; then
  r=$(sh "cd $D && PROOT_LOADER=$D/lib/libproot-loader.so PROOT_TMP_DIR=$D/tmp LD_LIBRARY_PATH=$D/lib ./pty_test 30 100 8 'tty; stty size; echo \$TERM; exit 5\n' $D/lib/libproot.so --kill-on-exit -0 -r $D/rootfs -b /dev -b /proc -b /sys -w /root /usr/bin/env -i HOME=/root TERM=xterm-256color PATH=/usr/bin:/bin /bin/bash --norc -i" 2>&1)
  echo "G0 pty+proot: $(echo "$r" | grep -E 'pts|30 100|xterm|exit' | tr '\n' ' ')"
fi
echo "G0 done"
