#!/usr/bin/env bash
# Usage: tests/local-coder/run-local-coder-tests.sh      (WSL or any machine with Node 18+; no install, ~50 MB RAM, ~2 s)
#
# The local coder's API layer (local-ai/server: compat.js, engines.js, toolcalls.js) under plain Node with a fake
# model: each output form of Qwen2.5-Coder 0.5B (tagged, fenced, bare JSON, broken JSON, prose, prose with JSON in a
# code block) through the three endpoints (chat completions, Responses, Messages), streamed and not, against the text
# the user should see and the tool calls the harness should get. docs/local-coder.md.
set -euo pipefail
cd "$(dirname "$0")"
exec node --test --test-reporter=spec local-coder.test.js
