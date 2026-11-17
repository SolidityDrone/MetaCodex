#!/usr/bin/env bash
# Usage: tests/web/run-web-select-tests.sh      (WSL or any machine with Node 18+; no install, ~40 MB RAM, < 1 s)
#
# WEB's chooser for a page's <select> and date / time / colour fields, the page's side (SELECT_JS in
# MetaCodexWebView.java) against a fake DOM: see select-js.test.js. The Unity side is in the self-test (WebChooser).
set -euo pipefail
cd "$(dirname "$0")"
exec node select-js.test.js
