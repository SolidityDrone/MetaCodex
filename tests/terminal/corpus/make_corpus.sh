#!/usr/bin/env bash
# Usage: Tests/corpus/make_corpus.sh        (WSL; needs tmux, script, vim, less, nano, top)
#
# Records real programs for the terminal emulator test: each case runs under `script` (which logs the
# exact bytes the program writes) inside a 100x30 tmux pane (a full terminal emulator, the reference).
# After the keystrokes, the pane's text and cursor are saved next to the raw bytes:
#   <case>.bin (program output), <case>.screen (tmux capture-pane), <case>.cursor ("x,y")
# CoreTests replays .bin into VtTerminal(30, 100) and must produce the same screen and cursor.
set -euo pipefail
OUT="$(cd "$(dirname "$0")" && pwd)/cases"
WORK="${TMPDIR:-/tmp}/mcx-vt"
mkdir -p "$OUT" "$WORK"
S=mcxvt
SCRIPT_BIN="$(command -v script)"

seq 1 400 | sed 's/^/line /' > "$WORK/long.txt"
printf 'alpha\nbeta\ngamma\n' > "$WORK/sample.txt"
cat > "$WORK/progress.sh" <<'EOF'
# apt-style: a scroll region above a fixed progress line
printf '\e[1;29r'
for i in $(seq 1 40); do
  printf '\e7\e[30;1H\e[0;30;42mProgress: [%3d%%]\e[0m [%-40s]\e8' $((i*100/40)) "$(printf '#%.0s' $(seq 1 $i))"
  printf 'Unpacking package-%02d (1.%d) ...\n' $i $i
done
printf '\e7\e[30;1H\e[2K\e8\e[r'
echo finished
EOF
cat > "$WORK/unicode.sh" <<'EOF'
printf '\e[38;5;208m╭──────────────────╮\e[0m\n'
printf '\e[38;5;208m│\e[0m 中文 宽字符 ✻ ⏺ ⎿ \e[38;5;208m│\e[0m\n'
printf '\e[38;5;208m╰──────────────────╯\e[0m\n'
printf 'emoji: 🚀 ok, tabs:\tA\tB\n'
printf '\e[38;2;255;100;0mtrue\e[48;2;0;60;120m colour\e[0m \e[4munder\e[0m \e[7minverse\e[0m \e[9mstrike\e[0m\n'
printf 'cursor save\e7 overwritten\e8XX\n'
printf 'insert: abcdef\e[3D\e[2@__\n'
printf 'delete: abcdef\e[4D\e[2P\n'
printf 'erase:  abcdef\e[3D\e[1K\n'
EOF

run_case() { # name, command, keys...
  local name="$1" cmd="$2"; shift 2
  tmux kill-session -t $S 2>/dev/null || true
  rm -f "$WORK/$name.raw"
  tmux new-session -d -s $S -x 100 -y 30 \
    "cd $WORK && env TERM=xterm-256color LANG=C.UTF-8 PS1='\$ ' $SCRIPT_BIN -q -f -O $WORK/$name.raw -c \"$cmd\"; sleep 30"
  sleep 1.2
  for k in "$@"; do
    if [[ "$k" == sleep:* ]]; then sleep "${k#sleep:}"; else tmux send-keys -t $S "$k"; sleep 0.4; fi
  done
  sleep 1.0
  tmux capture-pane -p -t $S > "$OUT/$name.screen"
  tmux display -p -t $S '#{cursor_x},#{cursor_y}' > "$OUT/$name.cursor"
  # drop script(1)'s own header line from the log
  sed '1{/^Script started/d}' "$WORK/$name.raw" > "$OUT/$name.bin"
  tmux kill-session -t $S 2>/dev/null || true
  echo "$name: $(stat -c %s "$OUT/$name.bin") bytes, cursor $(cat "$OUT/$name.cursor")"
}

run_case bash_colors "bash --norc --noprofile -i" \
  "printf '\\e[1;31mbold red\\e[0m normal\\n'; ls --color=always /usr/bin | head -32; echo done" Enter
run_case vim_edit "vim -u NONE -N -i NONE sample.txt" \
  "ihello world" Escape ":set number" Enter "jjA tail" Escape
run_case less_scroll "less -R long.txt" " " " " "/line 1[0-9]5" Enter
run_case nano_edit "nano -0 sample.txt" "typed in nano" Enter "second line"
run_case top_once "top -d 1" "sleep:2.5"
run_case progress "bash --norc --noprofile progress.sh; bash --norc --noprofile -i" "sleep:1"
run_case unicode "bash --norc --noprofile unicode.sh; bash --norc --noprofile -i" "sleep:0.5"
ls -la "$OUT"
