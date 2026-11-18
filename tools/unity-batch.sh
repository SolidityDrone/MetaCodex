#!/usr/bin/env bash
# Usage: tools/unity-batch.sh <BatchSetup|BatchSelfTest|BatchBuild> [max-minutes]
#
# One-shot batch Editor on this project (no GUI, exits when done): -executeMethod
# MetaCodex.EditorTools.MetaCodexSetup.<method>. Run it in the background; it prints a summary at the end.
# Guards for this machine (16 GB laptop, other Editors and bb running): refuses to start below
# MIN_MB available RAM (default 4500, never below 3000), stops ITS OWN Unity process (by PID, never by
# name) if Windows drops below KILL_MB (default 700, never below; watched every second), or the time limit
# passes, and refuses if another Editor already has this project open.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../unity" && pwd)" # the Unity project
METHOD="${1:?method: BatchSetup | BatchSelfTest | BatchBuild}"
MAXMIN="${2:-60}"
MIN_MB="${MIN_MB:-4500}"
KILL_MB="${KILL_MB:-700}"
# floors (2026-11-08): with KILL_MB=250 Unity pushed Windows down until the bb IDE's UI (Chrome) starved and bb
# went down; the Editor must stop itself first. Override only for a run nobody watches: UNSAFE_GUARDS=1.
if [[ "${UNSAFE_GUARDS:-0}" != 1 ]]; then
  if [[ "$KILL_MB" -lt 700 ]]; then echo "KILL_MB=$KILL_MB raised to 700 (below that the bb IDE starves)"; KILL_MB=700; fi
  if [[ "$MIN_MB" -lt 3000 ]]; then echo "MIN_MB=$MIN_MB raised to 3000 (a UI test needs ~2.3 GB + bb's headroom)"; MIN_MB=3000; fi
fi
# variables the Windows Editor must see (WSL passes only those listed in WSLENV): MCX_RELEASE=1 builds a release
# (non-debuggable) APK signed with the release key MCX_KEYSTORE names (a Windows path), MCX_UITEST_ONLY picks
# self-test sections, BEE_BUILD_THREADS caps the build's compile threads
for v in MCX_RELEASE MCX_KEYSTORE MCX_UITEST_ONLY BEE_BUILD_THREADS; do
  if [[ -n "${!v:-}" && ":${WSLENV:-}:" != *":$v"* ]]; then export WSLENV="${WSLENV:+$WSLENV:}$v/w"; fi
done
VER=$(sed -n 's/^m_EditorVersion: //p' "$ROOT/ProjectSettings/ProjectVersion.txt")
EXE_W="C:\\Program Files\\Unity $VER\\Editor\\Unity.exe"
P_W=$(wslpath -w "$ROOT")
mkdir -p "$ROOT/Logs"
LOG="$ROOT/Logs/batch-$METHOD.log"
LOG_W=$(wslpath -w "$LOG")

avail() { timeout 20 powershell.exe -NoProfile -Command "[int]((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue)" 2>/dev/null | tr -dc '0-9'; }
a=$(avail)
if [[ -z "$a" || "$a" -lt "$MIN_MB" ]]; then echo "NOT STARTED: Windows has ${a:-?} MB available, need $MIN_MB (close Chrome tabs or wait for the other Editor)"; exit 3; fi
if powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | Where-Object { \$_.CommandLine -match [regex]::Escape('$P_W') } | Select-Object -First 1 -ExpandProperty ProcessId" 2>/dev/null | tr -dc '0-9' | grep -q .; then
  echo "NOT STARTED: an Editor already has $P_W open"; exit 3
fi

rm -f "$LOG"
ARGS="'-batchmode','-projectPath','$P_W','-buildTarget','Android','-executeMethod','MetaCodex.EditorTools.MetaCodexSetup.$METHOD','-logFile','$LOG_W'"
PID=$(powershell.exe -NoProfile -Command "\$p = Start-Process -FilePath '$EXE_W' -ArgumentList $ARGS -WindowStyle Hidden -PassThru; \$p.Id" | tr -dc '0-9')
[[ -n "$PID" ]] || { echo "could not start Unity"; exit 1; }
echo "started $METHOD: Unity pid $PID, log $LOG, $a MB available"
t0=$(date +%s); low=$a; killed=""
# Available memory, sampled every half second by one PowerShell that stays up (a new PowerShell per sample takes
# 1-2 s, and with one sample every 10 s Windows went from 900 MB to 200 MB between two of them, 2026-11-10: the
# Editor was stopped too late for the bb IDE). The loop below reads the last sample once a second.
MEMF=$(mktemp)
echo "$a" > "$MEMF"
( powershell.exe -NoProfile -Command "\$c = New-Object System.Diagnostics.PerformanceCounter('Memory','Available MBytes'); while (\$true) { [int]\$c.NextValue(); Start-Sleep -Milliseconds 500 }" 2>/dev/null \
    | while IFS= read -r line; do v="${line//[^0-9]/}"; [[ -n "$v" ]] && echo "$v" > "$MEMF"; done ) &
SAMPLER=$!
n=0
while :; do
  sleep 1; n=$((n + 1))
  # is it still there: asked every 3 s (tasklist takes a moment)
  if (( n % 3 == 0 )) && ! tasklist.exe /FI "PID eq $PID" /NH 2>/dev/null | grep -q "$PID"; then break; fi
  m=$(cat "$MEMF" 2>/dev/null); [[ -n "$m" && "$m" -lt "$low" ]] && low=$m
  if [[ -n "$m" && "$m" -lt "$KILL_MB" ]]; then killed="low memory ($m MB)"; fi
  if (( $(date +%s) - t0 > MAXMIN * 60 )); then killed="time limit ($MAXMIN min)"; fi
  if [[ -n "$killed" ]]; then taskkill.exe /PID "$PID" /T /F >/dev/null 2>&1; echo "STOPPED Unity $PID: $killed"; break; fi
done
pkill -P "$SAMPLER" 2>/dev/null; kill "$SAMPLER" 2>/dev/null; rm -f "$MEMF"
secs=$(( $(date +%s) - t0 ))
echo "--- $METHOD finished in ${secs}s, lowest available ${low} MB"
grep -E "\[MetaCodex\]|error CS|Shader error in|Exception|Exiting batchmode|Aborting batchmode|Scripts have compiler errors" "$LOG" 2>/dev/null | tr -d '\r' | grep -v "^UnityEngine\." | tail -40
if grep -q "Exiting batchmode successfully" "$LOG" 2>/dev/null || grep -Eq "\[MetaCodex\] (UI )?SELFTEST OK|\[MetaCodex\] Succeeded" "$LOG" 2>/dev/null; then
  [[ -z "$killed" ]] && ! grep -q "\[MetaCodex\] FAILED" "$LOG" && { echo "RESULT: OK"; exit 0; }
fi
echo "RESULT: FAILED (see $LOG)"; exit 1
