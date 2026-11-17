# The code editor: a full IDE from the headset's Linux

The owner asked (2026-11-16): "Could we put an actual VS Code or an actual IDE in it? It would be cool to have an IDE
to work for real without using the agents too." The CODE EDITOR is that: **code-server** (MIT, by Coder: the
open-source VS Code ("Code - OSS") build that runs as a server in a browser), installed on request into the headset's
Linux and shown in the app's WEB window. In the app it is called CODE EDITOR or CODE; the product names appear only
here and in [LICENSES.md](LICENSES.md).

## What you get

- The editor of VS Code: tabs, split views, search across files, the git view, the integrated terminal (a shell in the
  same Linux as the app's terminals), the command palette, language features of the built-in extensions
  (TypeScript/JavaScript, JSON, HTML, CSS, Markdown, ...).
- Extensions from **Open VSX** (open-vsx.org, the default gallery of code-server): the Microsoft marketplace is not
  available to it (its terms allow only Microsoft's products).
- Not used: the release also carries VS Code's built-in GitHub Copilot extension and GitHub's Copilot runtime
  (about 220 MB of the 645 MB). The defaults turn its AI features off (`chat.disableAIFeatures`); it would need your
  own GitHub account and GitHub's terms (see [LICENSES.md](LICENSES.md)).
- Your projects: it opens `/root/metacodex-projects` by default, the folder where THREADS, FILES and the agents keep
  them; a project opens by voice ("open the weather project in the code editor").

## Install, size, where things go

| What | Value |
|---|---|
| Version | code-server 4.145.2 (VS Code 1.145.2, Node 24.22.1 bundled), pinned in `mcx-ide` |
| Download | 233,639,045 bytes (about 225 MB), `code-server-4.145.2-linux-arm64.tar.gz` from the GitHub release, sha256 `e867a34416e2da4a83496b09580001893e7a4e5bb58a0624c4fcd22819e7d53d` (GitHub's digest of the asset); a mismatch is refused and the file deleted |
| On the headset | 675,891,723 bytes (about 645 MB, 6,033 files; measured from the release archive) in `/opt/ide/code-server-4.145.2-linux-arm64` |
| Its data | `/root/.local/share/mcx-ide`: settings (`User/settings.json`), extensions, state, `password`, `run/` (pid, port, the server's log), `mcx-ide.log` |
| Needs | the Linux (setup step 3), internet for the install and for extensions; glibc 2.28 or later (the Linux has 2.41) |

Install from **MQH > PLUGINS > INSTALLABLE APPS > code editor** (INSTALL, then OPEN ▸ and DELETE), from **SYSTEM > TOOLS > EDITORS > code editor** (INSTALL), from the **CODE** tile, or by voice ("open the code
editor": it asks first). The install runs `mcx-ide install` in a new terminal, so the download's progress shows; it is
resumable, checked against the pinned sha256 and unpacked into a temporary folder that is renamed only when complete.
The editor opens by itself when it is done. Nothing is downloaded or started at app start.

`mcx-ide` (in the Linux, `userland/overlay/usr/local/bin/mcx-ide`):

| Command | What it does |
|---|---|
| `mcx-ide install` | download, check, unpack (again with `--force`); a newer pinned version replaces the old one (settings and extensions stay) |
| `mcx-ide start` | start it and print its URL and port: through the app's background service when the app answers, else in this terminal's background (then it stops with the terminal) |
| `mcx-ide run` | run it in the foreground: what the app's background service runs |
| `mcx-ide stop` / `status` / `log` / `version` | stop it (TERM, then KILL after 10 s), its state, its last log lines, the installed version |
| `mcx-ide password` | the password, to sign in from another browser (the app's WEB window signs in by itself) |
| `mcx-ide uninstall [--all]` | remove the program; `--all` also its settings, extensions and password; never your projects |

## How to open it

- **The CODE tile**: off the menu at first; add it on CONTROL > WRIST MENU (drag it from the SYSTEM tiles onto a ring).
  It is lit while the WEB window shows the editor; a tap then puts the window away.
- **SYSTEM > TOOLS > code editor**: OPEN, STOP, UPDATE (when the app brings a newer version), STATUS AND LOG, REMOVE.
- **Voice**: "open the code editor", "start the IDE", "open the notes project in the editor", "edit my current project
  in the editor"; "stop the code editor" frees its memory (registry commands `open_code_editor [project]`,
  `stop_code_editor`).
- **Agents and scripts**: `mcx-ui system ide open [folder] | start | stop | status`.

OPEN starts the server when it is not running (how long it takes is not measured yet; the app waits up to 150 s), signs
the WEB window in and shows the page, larger than a web page (x1.25; back to its size when the window closes). Closing
the WEB window leaves the server running, so the next OPEN is quick; STOP (or "stop the code editor") ends it.

## Where it runs, and what it costs

- **Not on the frame loop's cores.** The app starts `exec mcx-ide run` through its AI service process (`:ai`, the way
  MQH runs), which Android places on cores 0-2, away from the cores 3-5 of the frame loop; at `nice 10`. The service
  is a foreground service while it has children, so the editor keeps running while another app is in front. A server
  started by the app's hidden shell runner would not survive: that proot runs with `--kill-on-exit`.
- **Fewer frames to copy.** The WEB window copies the page into VR only when it changes. The defaults the install
  writes (once; your changes stay) keep the page still when nothing happens: a solid cursor (no blinking), reduced
  motion, no smooth scrolling, no minimap. Also: autosave after a short delay (Ctrl+S needs a keyboard), text 16 px,
  the app name out of the window title, `keyboard.dispatch: keyCode` (Android keyboards report key codes, not physical
  keys), telemetry off, update checks off, no startup page, workspace trust off (every folder is yours), and the
  built-in AI chat off (`chat.disableAIFeatures`: it needs an account of its own; the app has its agents).
- **Memory.** It is a Node server plus an extension host and the language servers of the files you open: plan on a few
  hundred MB while it runs (not measured on the headset yet). STOP gives it back.
- **Android quirk handled.** Android refuses apps the network-interface list that Node's `os.networkInterfaces()` reads
  (EACCES since Android 11). `mcx-ide` loads a 4-line shim (`/opt/ide/mcx-netfix.js`, `NODE_OPTIONS=--require`) in its
  Node processes that returns an empty list instead of throwing. The editor's terminals inherit it, so a dev server you
  start there that prints its network addresses does not crash either.

## Security: why it always has a password, and how the app signs in

Android lets every app on the headset connect to `127.0.0.1`. The editor can run any command in your Linux, so without
authentication any app could. So:

- It listens on **127.0.0.1 only** (port 47820, or the next free one up to 47829), never on the network.
- It always runs with **password authentication**. `mcx-ide` makes a random password once (32 bytes from
  `/dev/urandom`, hex) in `/root/.local/share/mcx-ide/password`, mode 600, inside the app's private storage: no other
  app can read it. Login attempts are rate-limited by the server (2 a minute, 12 an hour).
- The server gets the password's **sha256** as `HASHED_PASSWORD` (it removes it from its environment, so its
  terminals do not inherit it). With a hash that is not argon2, code-server compares its session cookie
  (`code-server-session`) with that hash (its `isCookieValid`, method SHA256). The app computes the same sha256 from the
  password file and puts the cookie into its WebView's cookie store before it opens the page
  (`MetaCodexIde.java`, on Android's UI thread): **host-only** (for `127.0.0.1`), **HttpOnly** (no page script reads
  it) and **SameSite=Strict** (no other site's page sends it along). So the WEB window opens signed in and nobody types
  or sees the password. The cookie is a session cookie: it is set again at every OPEN.
- Before it sets the cookie, the app checks that the server answering on the port is **its own**: the pid in
  `run/pid` is alive, the server's own log of this run (`run/server.log`) says it listens on that port, and `/healthz`
  answers. Another app holding the port would not get the cookie.
- What stays: the cookie, like any cookie, is scoped to the host and not to the port, so the WEB window would send it
  to another server on `127.0.0.1` that it opens itself (a dev server you run, or an app's local page you open in WEB).
  Such a server could then sign in to the editor. Opening a local page of an app you do not trust in WEB is the risk;
  pages of other sites never get it (SameSite=Strict). Signing in from the system browser works with `mcx-ide password`.
- Telemetry and update checks are off (`--disable-telemetry`, `--disable-update-check`, and in the settings). The
  extension gallery and the editor's built-in features that fetch from the web reach the internet only when you use
  them.

## Keyboard, clipboard, scrolling: tips

- **A Bluetooth keyboard is best** for real work: pair it in the headset's settings. Shortcuts (Ctrl+P, Ctrl+Shift+P,
  Ctrl+S, Tab, the arrows) need the WEB window to pass hardware keys with their modifiers to the page (see below).
- **The system keyboard** is for short edits: tap where the text goes, then ⌨ in the WEB window's bar; what you type
  is inserted at the cursor (`insertText` into the editor's input field; not tried on the headset yet). ⏎ and ⌫ are the
  window's keys.
- Tap to place the cursor; drag to scroll; files save by themselves.

## What the WEB window must support for real work (browser lane)

Measured against `MetaCodexWebView.java` and `ShellWeb.cs` on 2026-11-16 (neither was changed for the editor):

1. **Hardware keys with modifiers into the page.** `key(code)` sends a bare key code (meta state 0), so no Ctrl, Shift
   or Alt; Tab, Escape and the arrows have no buttons. Needed: forward the headset's hardware keyboard events (key code
   and meta state, repeat) to the page while the WEB window has focus, and Esc / Tab / arrow buttons for the system
   keyboard case.
2. **Paste from the clipboard.** The page's clipboard read (`navigator.clipboard.readText`) needs a permission the
   WebView never grants, so Ctrl+V of text copied elsewhere fails. Needed: a PASTE button that inserts the Android
   clipboard's text (`insertText`, as ⌨ does); copy from the page (`writeText`) should work but is unverified.
3. **Scrolling.** A drag is a touch scroll; the editor's lists and long files scroll better with wheel events:
   `wheel(x, y, dy)` exists in the Java side but nothing calls it yet (a thumbstick or a pinch-drag mode could).
4. **Size and resolution.** The page is 1280 x 800 pixels whatever the window's size; a larger texture (or a zoom
   setting) would show more code. The app scales the window by 1.25 while it shows the editor (ShellIde).
5. **A way to open a window "large"** (an `OpenBrowser(url, large)` option) would replace ShellIde's BaseScale change.

## Not verified yet (all on the headset)

The install (download speed, the unpacked size, `tar` under proot), the first start and its time, the sign-in by
cookie in the WebView, editing, the integrated terminal (node-pty under proot), the file watcher, extensions, memory and
the frame rate with the editor open.

Log lines to look for: `code editor: starting (mcx-ide run in the service process)`, `code editor: up on port 47820 in
N s`, `code editor: the sign-in cookie was not set (...)`, `code editor: stopped`; in the Linux `mcx-ide log`.
