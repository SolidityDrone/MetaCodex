# MetaCodex spatial UI: style, the Whisp, windows, gestures, plugins, layouts

The spatial interface of MetaCodex: its style, the Whisp, the windows and where they open, hands, gestures, scripts and
plugins, Ambient Focus and layouts. The app shows no logos: SYSTEM > TOOLS is a plain list in groups (see
[LICENSES.md](LICENSES.md)). Headset checks: [TEST_CHECKLIST.md](TEST_CHECKLIST.md).

## Style

A command console of our own design: thick dark navy glass, chamfered corners (a big cut top-left and bottom-right, small
ones elsewhere), a steel frame lit from above with corner brackets, a stepped header energy bar with a lead
block before the title, a pulse running in it and a hatched edge, command-card buttons with a bevel, accent
ticks at their cuts and a sheen crossing them while hovered, quiet list rows (glass, a notch, a hair line) that
the accent floods into when chosen, segmented energy bars for progress and sliders, cyan accent (#35D0FF) with
amber / red / green for warning, danger and ok; the light that casts a window is a warm white.

**The glass.** A hexagon lattice and a grid of points between its cells are printed on the glass, flat, with no depth. A ruler runs down the left edge (a tick every 10 mm), a conduit of energy along the
bottom from the right cut with a pulse in it. **Your hands light it**: a fingertip within 14 cm of a window, or
the cursor of a hand's ray, is a point of light on the lattice and the frame (`_McxPointer0` / `_McxPointer1`, set by
the router every frame); the pool tightens as the finger comes closer, so it also shows how far the glass still is. Lattice
lines fade out with distance before they get thinner than a pixel (no shimmer). Titles and buttons use Rajdhani SemiBold in capitals with letter spacing (the console voice), body text
Inter, code DejaVu Sans Mono. All OFL / free licences. Short UI sounds are synthesised in code.

Everything is drawn by one shader, `MetaCodex/SciFiUI`: each panel, button, header, bar, line, dot and
selection frame is a quad whose shape is computed per pixel from vertex data (size, chamfer, fill style,
border, state), so a whole panel is one mesh and one draw call, crisp at any distance. A change of colour or state
only (a hover, a press, a selection, the flash after a click) rewrites that shape's colours and states in place and
uploads those two streams; only a change of size, place or visibility rebuilds the panel's geometry (every
hover and every frame of a click's flash rebuilt the whole mesh, thousands of vertices on a big panel). Text is TextMeshPro
with runtime SDF fonts drawn by `MetaCodex/TextBend` (TMP mobile SDF plus the panel bend). No logos or
pictures of third-party software anywhere. LOOK settings (the render scale is fixed at 1.6): panel opacity,
foveated rendering (off by default; it blurs the edges of the view), sounds and volume.

**Opacity.** Passthrough is an underlay: the eye buffer's alpha decides how much of the room shows. Every UI
shader blends its alpha as `One OneMinusSrcAlpha` (coverage only adds), so a glow over a window
can never cut a see-through fissure. At PANEL OPACITY 75 % and above, a depth-only pass writes the panel's
body (1.5 mm behind its face), so text, icons and buttons of windows behind it are hidden, not shown through.
All windows write this depth before any shape draws its colour (`UiShapeMesh`: a depth-only copy of the material
at queue 2989), so the GPU skips the hidden pixels of windows behind.
The wrist menu's glass follows PANEL OPACITY too: its tiles and watch face take the same value (`WristArt`
`_Opacity`); its lines, icons and sparks stay as bright as before.

**Bending.** BEND PANELS curves each window around a vertical axis on the GPU: the panel's radius goes into
a global table (`_PanelBend`, 128 slots) and every renderer of the panel (shapes, text, sub-meshes, icons)
carries its slot, so text that changes every frame bends with the panel and never flashes flat. The
curvature eases in and out (about 0.3 s) instead of jumping. Rays and pokes hit the curved surface (ray-
cylinder intersection), so the reticle sits where the beam meets the window.

**Opening and closing: a window is cast** (`UiPanelFx`, `McxPanelFx.hlsl`). It is drawn with light, like a hologram
being printed, by its caster (`UiPanelFx.Caster`: the Whisp, when it is on). Opening (0.35 s): four corner brackets
fly in and lock on where the window will be, with a faint outline of it; two thin rays run from the caster to a hot
warm-white line (about 5 cm of glow), beads of light running in it, that sweeps across between the brackets. Behind
the line the window exists: first a glowing hexagon lattice whose cells close from their rims inwards and cool
into glass, the frame, then the controls and icons coming online hot, the texts flickering for a moment; before
the line nothing is there yet. The line runs on 16 cm past the far edge, so everything has cooled when it
stops, a last pulse goes through the lattice and the frame, and the final frames are the window at rest (the
flying brackets hand over to the frame's own, same shape and light). Closing (0.22 s): a surge, the line runs
back and takes the window with it, the brackets let go, and a spark of what is left flies home to the caster. An
open window asked to open again pings: the caster lights it up, its frame flashes, the brackets close in round it
once. Small passing panels (messages, the voice card, the Whisp's menu squares) use a quick variant (0.22 s in, 0.12 s out,
no brackets). One motion scale serves the whole UI (`UiTheme`: fast 0.12 s, base 0.22 s, slow 0.35 s): ease out for
what arrives, ease in for what leaves, smooth both ways for a move. Both directions play back from where they are when the user changes their mind. For
the rest of the app a window is closed the moment `Close()` is called (`IsOpen` false, `Closed` raised) and
open the moment `Open()` is; it is turned off when its closing ends (dialogs and messages are destroyed then).

How it is drawn: nothing is moved or scaled. The scan line is one plane in world space per window
(`_PanelScan[slot]`, with `_PanelFx[slot]` = cast on, flash, all hidden, own brackets hidden; all zero at rest),
and every shader that draws inside a window asks how far behind that plane its pixel is: `SciFiUI` (the body
builds its lattice and glass, cell by cell; controls fade in 12 mm behind the line), `TextBend`, `UiIcon`,
`ViewImage`, and the opaque ones, `PanelUnlit` (a terminal's background) and `StageLit` (a 3D stage), which
clip what the line has not reached. Renderers a window did not make itself (a terminal's rows, a stage) get its
slot when a cast starts (`TagForeign`). The line leaves from the caster's side (`UiPanelFx.Plan`: left, right,
top or bottom, whichever edge is nearest to it) and comes back to it; without a caster it crosses from the left. A
small overlay mesh in the window's own space draws the light itself with `MetaCodex/WristArt` (the flying
brackets, the scan line, its two rays, the spark), bent like the window; on a bent window a horizontal line is an
arc. Captures render a window at rest. Sounds follow: two ticks as
the brackets lock on, a rising sweep with a shimmer, a low thump and a clear note; a falling sweep, a quick
rise as the spark flies home.

## The Whisp

The Whisp is a small orb of light that floats by the user and casts the windows, speaks and shows the
app's messages. In the code it is the orb: `UiOrb` is its body, `ShellOrb` what it does, `MetaCodex/Orb` how it is
drawn. CONTROL > LOOK & SOUND > Whisp turns it off (settings key `orb`; voice "turn the orb off"); then windows are
cast from their left edge and messages float in the upper view.

- **How it looks.** One quad that faces each eye; every pixel follows its own ray through a unit sphere, so the
  orb has true depth per eye: a white-hot core, two shells of plasma filaments turning against each other, a dark
  glass body with a bright limb, three thin rings round it (two of warm gold, one dashed in its own colour; a ring is a
  tube from every side and passes behind the sphere), four motes, a halo. Premultiplied: its light is added over the
  room and its body darkens what is behind it, so it reads in a bright room; a depth pass on its disc keeps a hand
  that is behind it from cutting it out.
- **States.** Each state eases into the next:
  - idle: calm cyan, it breathes, its light shimmers slowly;
  - listening: gold, the rings face you, the light follows your voice; it starts with a flash of ice white and a
    ripple (also when "Hey Whisp" is heard: `WhispFlare`);
  - thinking: violet, the plasma and the rings spin up;
  - speaking: the light pulses with every syllable and ripples leave it; the spoken answer comes from its place
    (the AudioSource sits on it, 80 % spatial);
  - needs you: an amber heartbeat while a thread waits for your approval or answer (until you open its menu or the
    LOG);
  - busy: dim steel and slow while the first start's setup keeps the voice locked;
  - unread messages: up to four of its motes turn gold;
  - pointing: when a window had to open out of view, its rings turn to the window for 2.5 s (`PointAt`).
- **Where it floats.** Up to the right of where you face (30 degrees right, 17 up, 0.52 m), in a frame that
  follows you lazily: it stays where it is in the room while you only glance round, and comes along when you turn
  (more than 34 degrees for 0.7 s) or walk away (25 cm), moving round you, never through your head. While it shows
  something (a callout, the voice card, its menu; listening, thinking or speaking) and for 1.5 s after, it holds
  still, so you can turn your head to read; then only walking 80 cm, or turning more than 70 degrees, moves it.
  It never sits in front of a window: when one covers its place for half a second, it takes the next free one (ten
  candidates, both sides, higher up) and goes back three seconds after its place is free again. A window that opens
  where it idles makes it move aside. Pinch it and put it anywhere: it keeps that place by you (`orbPinned`,
  `orbYaw`, `orbPitch`, `orbDist`); its menu's PLACE lets it choose again.
- **What it says.** Every message of the app (`Toast`) is a callout: a card cast by the Whisp on an arc round its
  **right** side, the stack centred on its level, the newest lowest, up to four, every card turned to you and
  joined to the Whisp by a rail of light along the arc; the voice card is the first card while it shows.
- **Its menu.** Touch the Whisp with a fingertip, or point at it and pinch: its menu opens round its **left** side:
  four plain squares on one arc, each turned to you: ◉ TALK, ♫ SPEAK (spoken answers on or off), ✥ PLACE, and ✉ LOG
  ("✉ n NEW" while there are unread messages), which opens LOG · MESSAGES: the last 40 messages and CLEAR ALL. Each
  square is a frameless `UiPanel` (60 x 54 mm at scale 1: `OrbActW`, `OrbActH`), 8 mm apart (`OrbActGap`); the top
  and bottom ones sit 45 degrees off the level. Labels are 13.5 mm, about a degree at half a metre. A hand at the
  Whisp does not also click the window behind it (`UiInputRouter.HandBlocked`). The first time the app runs with the
  Whisp, one message says what it is (`orbTipShown`).
- **It keeps off the window you watch** (`Shell/ShellOrbAvoid.cs`). The watched window is the first one your gaze
  meets, for 2 s after your gaze leaves it. What the Whisp takes of your view (its body, its menu's arc on the left,
  its callouts' arc on the right) must not cover it: when it would, the Whisp glides aside or below to the nearest
  place where nothing of it covers that window, within 60 degrees of where you look; slowly (0.4 s), never while a
  hand is at it or at its items, and back to its own place 1.5 s after that is free again. Idle and not looked at, it
  is 80 % of its size and dimmer; a look or a hand brings it back.
- The threads used last stay in `recent-threads.txt` (beside the settings): `LastUsedThread` (`Shell/ShellReturn.cs`)
  reads them for the way back from an app.
- Control port: `orb on | off | menu | place | status`.

## Code map (`unity/Assets/MetaCodex/Runtime`)

| Folder | What |
|---|---|
| `UI/` | `UiTheme` (colours, sizes, fonts), `UiShapeMesh` (batched shapes, rotated strokes), `UiPanel` (frame, header, the cast, drag, GPU bend and cast slots, curved ray hits), `UiPanelFx` (the cast's timelines, its scan plane and its light), `UiOrb` (the orb's body: moods, motion, the uniforms of its shader), widgets (`UiButton`, `UiToggle`, `UiSegmented`, `UiSlider`, `UiTextField` (one line, or `Wrap` for a box that grows), `UiDragGrip` (a bar to drag: THREAD's message box), `UiList`, `UiTabs`, `UiProgress`, `UiDivider`), `UiViz` (canvases drawn from pooled shapes: `UiChart`, `UiFlow`, `UiSequence`, `UiTable`, `UiCodeView`, `UiRing`, `UiChangeMap`, `UiToolList` (SYSTEM > TOOLS: groups and rows, no icons), `UiStorageBar`; the series palette), `UiMermaid` (flowchart / sequence / pie subset), `UiKeyboard` (the headset's system keyboard), `UiInputRouter` (pointers, beams, reticles, selection), `UiToasts`, `UiModal`, `UiSounds` |
| `Hands/` | `GestureEngine` (built-ins, pose templates, custom gestures with hold/cooldown, head-relative directions), `PoseSpec`, `OvrHandSource` (OVRHand/OVRSkeleton), `SimulatedHands` (any pose, for tests) |
| `Scripting/` | `ScriptHost`: Lua (MoonSharp) scripts and plugin folders, hot reload, sandbox, instruction budget, detectors as persistent coroutines, persisted state; `PoseJson` |
| `Security/` | `SecretVault` (Android Keystore sealer, Editor dev sealer, policies, access log) |
| `AI/` | `QvacHost` (starts the local AI service process, token, restarts), `QvacClient` (HTTP), `VoiceRecorder` (microphone, end of speech), `VoiceCommands` (registry, phrase templates, tool specs), `ThreadPulse` (a thread's timeline and state, docs/thread-view.md), `WorkspaceMap` (checkouts and worktrees, docs/workspaces.md) |
| `Shell/` | `MetaCodexShell` (wiring, panels by id and aliases, control-port commands), `ShellPanels` (dock, terminals, files, git logic, LINKS (id browser), MQH's SCRIPTS tab, SETTINGS' pages, saved layouts), `ShellControl` (CONTROL's rail and categories, the one size of its windows), `ShellWrist` (the main menu table `MainMenuItems`, the wrist menu), `ShellGestures` (Gestures studio, bindings, actions, recording, the gesture examples and the one-time cleanup of the old starter copies), `ShellAgents` (agents, threads grouped by project, thread with composer chips and fork, new thread, terminal links, the threads used last), `ShellThreadComposer` (THREAD's message box: the grip, its limits, voice bigger / smaller / reset; docs/thread-view.md), `ShellWorkspace` (the thread's linked workspace module, the picker), `ShellViews` + `ViewStage` (agent views, the physics stage), `ShellVault` (vault: secrets and GIT with the GitHub account; secret broker), `ShellVoice` (voice pipeline, shell commands, VOICE panel, HUD), `ShellAmbient` (Ambient Focus), `ShellSystem` (installed tools, storage), `ShellLayout` (fixed / float carousel, the thread + workspace pair), `ShellPlacement` (where a panel opens), `ShellOrb` (the orb: where it floats, hands, the voice, callouts, its menu of squares), `Services` (system deep links, settings, MQH client, git, files) |
| `../Resources/Shaders/` | `AmbientFocus` (the dome: depth, floor plane, blur), `FocusMesh` (hand cut-outs), `FocusDepthCopy` (floor readback), `TextBend` (TMP SDF + bend), `UiIcon` (textured quads + bend; no tool logos any more), `StageLit` (physics bodies), `ViewImage` (images in views), `Orb` (the orb), `WristArt` (the wrist menu and the cast's light): in Resources so they ship without material assets; `../Shaders/McxPanelFx.hlsl` (the cast's scan plane, shared by every shader that draws inside a window) |
| `../Plugins/Android/MetaCodexVault.java` | the Keystore bridge (AES-256-GCM, StrongBox or TEE) |

## Panels

| Panel | Does |
|---|---|
| Dock (the default main menu, a grab bar on top to move it; [control-center.md](control-center.md)) | TALK, TERMS (the terminals: hidden ones listed, a tap shows one, + NEW opens another), FILES, THREADS, WEB, CONTROL; CLIP, FOCUS (on / off), SELECT, KEYS, CENTER: the wrist menu's table (`MainMenuItems`) in its order. A tile is selected while what it opens or switches is on, as the wrist's tiles are lit (checked 4 times a second, colours only). Setup (agents, MQH, vault and git, voice, system, settings) is in CONTROL |
| Wrist menu (made only when DISPLAY's "Main menu on the wrist" is on, or "turn on the wrist menu"; its icon atlas is drawn on a background thread, and the DOCK stays until it is ready) | the dock's 11 actions (inner ring: TALK, TERMS, FILES, THREADS, WEB, CONTROL; outer: CLIP, FOCUS, SELECT, KEYS, CENTER; KEYS: the system keyboard for the terminal) on the left wrist (`UiWristMenu`, `ShellWrist`): look at the back of the wrist as at a watch and a watch face is projected over it (a beam from the wrist, a hexagon of glass with the time, sixty ticks and a seconds hand); touch it with the right index fingertip (no pointing, no pinch: the fingertip goes into the surface) and it unfolds: a shock ring, TALK / TERMS / FILES / THREADS / WEB / CONTROL swinging in clockwise, the other five the other way, each drawing its line icon with a scan line and typing its name, under a bezel and drifting arcs. Touch a tile to run it (flash, ring, sparks); lit tiles (a strip of light) show what is on. Every change is animated both ways and plays back from where it is (never pops). Stable: a 1-euro filter on the wrist; while the right hand is at it, or over the left hand, it holds still and never closes (that hand hides the left one from the cameras); jumps of the tracked wrist are ignored until they persist; a lost left hand keeps it 1.5 s, a wrist turned away 0.45 s; a slide across it presses nothing. Folds after 7 s untouched. One procedural mesh (`WristArt` shader: glass tiles, lines, icons from `UiIconAtlas`, a signed-distance atlas drawn by code, the beam, the face), the glass's depth (`WristDepth`) and two small particle systems (`WristSpark`). The glass (tiles, watch face) follows PANEL OPACITY, from the same value as the panels; the lines, icons and sparks do not; the depth (`WristDepth`) does not change |
| Keyboard (`SystemKeyboard`, `UiKeyboard` in `UI/UiElements.cs`; every text field, the terminal, WEB's ⌨) | The headset's system keyboard. A text field opens it with its text (masked for secret fields): what you type shows in the field at once; Done keeps it, Cancel puts the old text back. The terminal (KEYS on the dock or the wrist, the frame's ⌨, the key bar's ⌨) opens or closes it: each key goes to the shell at once (the box's text is diffed: a deleted letter is one Backspace), Done presses Enter. Voice "show the keyboard" opens it and never closes it. WEB's ⌨ types the text into the page's focused field when you press Done. While it is open, and 1 s after, only its text box counts: the same keys also arrive as key events and were typed twice ("ls" arrived as "lsls"). One keyboard at a time: a field takes it from the terminal. Horizon OS decides where it shows: with Settings > Experimental > Surface keyboard on, it can lie on a table. An app cannot place it or turn that mode on, so the first time it opens in a session a toast says where the setting is. A Bluetooth or USB keyboard and the terminal's key bar (Esc, Tab, arrows, Ctrl, Alt) work as before |
| Terminal frames | every terminal in a frame: drag by the header, `⇗` open the link the terminal printed (sign-in flows), `⎘` paste the clipboard, `⌨` keyboard, `+` new, `–` hide (the shell keeps running), `✕` end it (the last one is hidden instead: "TERMS on the menu shows it"), tap to focus. The first terminal starts hidden: TERMS on the menu (dock or wrist) lists it, a tap shows it |
| Terminals | list of terminal instances (hidden ones marked): new, show, hide, close |
| Files | the Linux userland's folders (`/root`, `/mnt/quest`, `/`): open folders, cd / view / edit / paste path; FILES APP (the end of its row) opens Horizon's Files app |
| WEB (the in-app browser, `ShellWeb`) | a window like the others, beside them (Horizon OS cannot show its own browser next to an immersive app: even Web Tasks send the app to the background). The page is Android's WebView (`Plugins/Android/MetaCodexWebView.java`): laid out off screen at 1280 × 800, drawn into an `ImageReader` only when the page changes (30 fps at most, nothing while the window is closed), copied into one buffer that Unity uploads into the page's texture (`AndroidJNI.GetDirectByteBuffer`: a view, no copy, no unsafe code). Toolbar ◀ ▶ ⟳, the address (localhost:5173 → http, a word with a dot → https, words → a search), GO, ⌨ (types into the page's focused field), ⏎ ⌫, ☰ (LINKS), ⇗ (this page in Horizon's browser). A pinch or a fingertip on the page is a touch, a drag scrolls, the pointer hovers; the window never bends (`ShellLayout.StaysFlat`). Opens on a start page with the bookmarks and the local servers found. Remote debugging is on: `chrome://inspect` on the PC (USB adb) shows the page's console and DOM. Every page the app opens goes here (GO, bookmarks, local servers, terminal links, voice `open_url`, Lua `browser.open`) except sign-in pages (`SignInHosts`, `SignInPaths`: claude.ai, anthropic, openai, Google, GitHub /login, …/oauth…): those open in Horizon's browser, also when a page links to one, so no account's web session ever stays in the app's cookies |
| LINKS (panel id `browser`, alias `links`; titled LINKS, WEB is the browser) | the address field, bookmarks (add / remove), local servers on 127.0.0.1 (⟳ SCAN of the usual dev ports); a tap opens the page in WEB. Horizon's system pages moved where they belong: HORIZON HANDS ⚙ to HANDS, APP PERMISSIONS ⚙ to ABOUT, FILES APP to FILES |
| Scripts (MQH > SCRIPTS) | the Lua scripts in /mnt/quest/scripts only (plugins are in MQH > PLUGINS): status (ON / OFF / ERROR), ENABLE, ⟳ RELOAD, + EXAMPLE (double clap toggles TERMINALS), FOLDER, DELETE, log; empty: one line "No scripts · + EXAMPLE, or ask an agent (skill quest-scripting)". The hands' live view is GESTURES > LIVE only |
| Gestures (MQH > GESTURES) | studio: ALL (grouped like SKILLS: YOURS = my-poses and every plugin or script gesture that is not an untouched example, "none · RECORD a pose or ASK AN AGENT" when empty; SYSTEM (7) = the built-ins with their role: pinch click, grab move · spin, point ray, open, palm-up, clap two hands, select select tool; EXAMPLES, folded (tap to open): `core-poses` and `swipes`, + ADD installs one, ADDED lists its gestures under it. TEST ▶ and BIND → for any gesture; SOURCE and DELETE POSE only for YOURS, + ADD only for an example not installed; the list refreshes by itself), LIVE (finger curls of both hands, palm/thumb direction, velocity, active gestures, recent events, Lua detector cost), BIND (gesture → action, no code; 12 presets: TALK, TERMINALS, NEW TERMINAL, THREADS, FILES, WEB, GIT, KEYBOARD, SELECT, RECENTER, FOCUS, CTRL-C; CUSTOM ACTION… for any other action), RECORD (3-2-1, the hand's pose becomes a named gesture in plugin `my-poses`; one line points to MQH > PLUGINS for new plugins) |
| Agents | GET: OPENCODE, CLAUDE CODE, CODEX (each: its official npm package installed in a terminal you watch, `npm i -g --allow-scripts=<pkg> <pkg>@latest`; ✓ and the version once it is on the PATH) and MORE · COMING SOON (not clickable). Then the supported agents only (Claude Code, Codex, OpenCode, in that order; Cursor, Pi and any other provider MQH has are not listed and are switched off in MQH, so no thread, model list or probe uses them), each with sign-in state, version, account (OpenCode, shown by MQH only once installed, comes from its execution options); ⚿ API KEY: SUPPORTED PROVIDERS for that agent (OpenCode: OpenCode Zen / Go, Anthropic, OpenAI, OpenRouter, Google Gemini, xAI, DeepSeek; Claude Code: Anthropic Console; Codex: OpenAI), ⎘ PASTE or type, SAVE: sealed in the vault (policy allow) and put into MQH's machine environment, so every agent MQH starts finds it (OpenCode reads those variables itself); a key MQH did not take yet is tried again every 20 s and when MQH comes up. No links to providers' sites anywhere. SIGN IN runs the agent's own login in a new terminal (⇗ on that terminal opens its link in Horizon's browser); each button says the one thing it does now: ENABLE or DISABLE (in MQH), UPDATE through MQH only for an installed agent (GET is how one is installed); NEW THREAD (Codex and OpenCode). The rail's status: how many are ready, else "sign in". Claude Code runs in a terminal for now (TERMINAL): its MQH provider plugin carries Anthropic's SDK, so it is not in the APK (installed at first use, next) |
| Threads | grouped by project type: dim headers NATIVE, DEFAULT, RESEARCH (then OTHER for a project outside `/root/metacodex-projects/` with no type), a tap folds one; under each its projects, under each project its threads, newest first; the project row shows how many run or need you, a tap folds it; a tap on a thread opens it. + PROJECT… asks the type (NATIVE APP · DEFAULT · RESEARCH · EXISTING…), then the name: the project is made in `/root/metacodex-projects/<type>/<name as a folder>` with `git init` (a local identity "MetaCodex" only when there is no global one) and `mcx-project set`; EXISTING… adds a folder as it is. The type comes from the path, else `.metacodex/project.json` (read at most every 30 s). A project with a worktree shows its folders as rows (▣ checkout, ⎇ each worktree with its base; 1 WAITING, 2 WORKING ⚠), the threads under the folder they work in ([workspaces.md](workspaces.md)); ⎇ WORKSPACES, ◆ AGENTS |
| Workspaces | every folder threads work in, per project: who works there and who waits, its git state against its base; MERGE INTO base (dry run first: git merge-tree; conflicts go to the worktree's agent), NEW THREAD HERE, REMOVE (archive + delete the worktree, the branch stays), OPEN THREAD, FILES, TERMINAL HERE. MQH holds a turn while another thread works in the same folder (SEND NOW in THREAD overrides it) |
| Thread | a chat ([thread-view.md](thread-view.md)): your prompt in a bubble (pinned on top once it scrolls away), the agent's work under one header per exchange (open while it works, closed after: thoughts with their first sentence, commands with ✓ / ✕ and time, edits with line counts, reads grouped; a tap opens a thought, an output, a closed header), the answer in full (markdown-lite, streaming with a caret; views as `[▣ title]`); drag or ▲▼, ↓ NEW when scrolled up. The status line under it: what it does now, for how long, quiet / may be stuck, waiting for you, answered. The meta row: where it works, and the plan and usage limits (5h / week / month). Approvals (ALLOW ONCE / SESSION / DENY, only while one is pending), composer + ◉ (say it: speech to text into the composer) + SEND, and the chips under it: MODEL (+ effort), ACCESS (Edits / Auto / Full access), the workspace (project · checkout or worktree), the CONTEXT ring (use of the model's window; tap: compact or fork, the usage windows with their resets), STOP; on top FORK…, ⟳; on its right edge the sidebar's handle |
| Thread sidebar | the handle on THREAD's right edge opens it, attached at the thread's right edge (moves, bends and closes with it; each thread remembers it and its tab; docs/thread-sidebar.md). CHANGES: every file the thread changed (git against HEAD or the worktree's base, untracked included; outside git the agent's edits), a VS Code-like diff (numbers, +/- gutter, green / red lines, syntax colours, folds, wrap or sideways scroll). FILES: the project folder with a breadcrumb, a read-only code view. GIT: branch, status, commits; no repo: INIT GIT only. STATUS: model, context, what runs, approvals, usage, MQH. VIEWS: the thread's agent views |
| Views | agent views (below): one panel per view, named "View · title"; the same id updates in place |
| Picker | a short list in front of the panel that asked: model, effort, access, fork target, context, packages |
| New thread | provider, model (from MQH's catalogue for that provider; the local coder, when installed, is the first row: ⌂, LOCAL badge, "on this headset · offline · slow"; it is never picked by default), permissions (EDITS / AUTO / FULL), workspace (project checkout or a new git worktree), START. START opens THREAD as a draft ("New thread · <project>", NOT STARTED, the composer focused, the chips as chosen): nothing is made in MQH until the first message (typed or ◉ said, with its attachments); then MQH makes the thread with it as the first prompt and names it from it. A draft closed (✕, another thread, a new draft) keeps nothing. Voice `new_thread` with a task makes it at once |
| Vault | SECRETS: secrets (names only), where the key lives (StrongBox / TEE), policy (ASK / ONCE / ALLOW / DENY), new value (secure keyboard), → MQH ENV, re-arm, delete, access log; approval dialogs for requests from Linux (`docs/secrets.md`). GIT (the panel id `git` opens it): the GitHub account (TOKEN… sealed as `GITHUB_TOKEN`, TEST asks api.github.com who it is, IDENTITY… sets user.name / email, GIT USES IT installs the credential helper `!mcx-secret git-credential GITHUB_TOKEN` for github.com, → AGENTS gives GH_TOKEN / GITHUB_TOKEN to MQH threads) and the repositories (status, branches, history, fetch / pull / commit / push) |
| System | TOOLS: a plain list in groups (agents, JavaScript, Python, native code, Android and Quest, git, terminal utilities, editors), one row per known tool: a status dot (lit = installed), its name, what it does in a few words, and its version or how to get it; no logos or icons; the list is also written for agents as `/mnt/quest/system/tools.json` (`mcx-ui system tools [json]`), so they know what is installed without probing the terminal; a tap installs (in a terminal, low priority), updates or removes it; PACKAGES… (Debian packages you chose, npm globals). STORAGE: the headset (Linux, AI models, shared folder, apps + system, free), Linux by folder with one-tap cleanups (apt, npm, bun, pip/uv, cargo, temp), a soft LINUX LIMIT (Android cannot reserve or cap an app's space: the app warns past it) |
| Voice | the local AI service (state, RAM), models (install with progress, loaded or not), TALK, TYPE… (a command as text), SAY… (text to speech), spoken replies, LLM fallback, auto stop, threads, the command list (shell + plugins), the last requests with timings (`docs/voice.md`) |
| Voice HUD | above the dock while a voice request runs: listening level, transcript, result; STOP |
| CONTROL ([control-center.md](control-center.md)) | one place for setup: a rail on the left of the shown window with ten categories in three groups and a live status each (THIS HEADSET: DISPLAY, LOOK & SOUND, FOCUS, HANDS; AI: VOICE, AGENTS, MQH, VAULT; LINUX & APP: SYSTEM, ABOUT), in the display font. One category at a time: another one takes the window's place (the six windows have one size, 0.76 x 0.66 m: same rectangle, the rail stays); drag the window or the rail's bar, the other follows; ✕ closes both; FLOAT gives the pair one slot. SETTINGS' pages have ids (display, look, focus, hands, about); `control` opens the last category; old ids still open their page (agents, mqh, vault, git, skills, gestures, daemon, storage, ...); voice: "control panel", "control center" |
| Settings (CONTROL's DISPLAY, LOOK & SOUND, FOCUS, HANDS and ABOUT; no tab bar of its own: a line says what the page is for) | HANDS (tab GESTURES: click by pinch/poke/both, move panels by header pinch or closed hand, ray mode, hand rays, PINCH AT, clap and its distance, plugin gestures on/off; SELECT AREA ⌖, GESTURE STUDIO ▸ (MQH > GESTURES), HORIZON HANDS ⚙; the live hand line), DISPLAY (layout fixed/float, DISTANCE (dimmed in FLOAT: "FIXED only"), FLOAT RADIUS (dimmed in FIXED: "FLOAT only"), UI scale, bending (both layouts), the main menu on the wrist, save / load named layouts, recenter), FOCUS (Ambient Focus: on/off, radius, intensity, softness, blur, real things in front, sharp depth, presets SUBTLE / FOCUS / BLACKOUT), LOOK & SOUND (opacity, foveation, sounds and volume, the orb), ABOUT (version, licences: LICENCES ▸ opens FILES at /usr/share/doc/metacodex; APP PERMISSIONS ⚙). The link to MQH is MQH > STATUS. CLIPBOARD's size is CLIPBOARD's own KEEP slider |

## Hands

- Ray: OVR's pointer pose; pinch clicks and drags. Poke: the index fingertip pushes buttons.
- Move a panel: pinch its header and move, or close the hand (fist) while pointing at it.
- Select tool: pinch and drag a rectangle on any panel; raises the `select` gesture.
- Built-in gestures: `pinch`, `grab`, `point`, `open`, `palm-up` (per hand, start/end), `clap` (two hands,
  tap), `select`. The engine also measures, per hand: five finger curls (thumb included), thumb direction,
  palm normal, finger direction, palm velocity in the head's yaw frame, hand size.

## Gesture plugins (no rebuild)

Folders in `/mnt/quest/plugins/<id>/`: `plugin.json`, `poses/*.json` (named hand shapes: target curl per
finger, palm / thumb direction, pinch range, hold, cooldown) and `main.lua` (Lua detectors with
`gesture.define`, reactions with `gesture.on` / `gesture.sequence`). The app reloads a plugin within a
second of any change. Detectors run as persistent coroutines about 36 times a second under the
instruction budget; a runaway detector stops only its plugin. A fresh install adds
one example, `core-poses` (thumbs-up, thumbs-down, victory, horns, call-me; data only), for the default binding of
the V to Ambient Focus (`EnsureVictoryFocus`). The other examples are opt-in: `swipes` (swipe left / right / up; Lua)
and the app plugins Timers and Calendar (MQH > GESTURES > ALL > EXAMPLES > + ADD, or `mcx-ui plugin example <id>`;
`mcx-ui plugin examples` lists them). Seeded copies of older installs: the first start removes
those copies once (marker `.examples-v2` in the plugins folder) when they are untouched (byte for byte the
shipped files) and unused (no binding, script or other plugin's Lua names one of their gestures); edited or
used copies stay, my-poses and other folders are never touched. New plugins: MQH > PLUGINS > + NEW PLUGIN…
(its template's reaction is a toast only). Any gesture can be bound to an action without code (GESTURES > BIND, or
`mcx-ui gesture bind <gesture> <action>`); actions are one vocabulary shared by bindings, Lua
(`app.action`) and the control port: `open:` `close:` `toggle:<panel>`, `toast:`, `run:`, `type:`, `url:`,
`layout:`, `spin:`, `select`, `keyboard`, `terminal:new`, `recenter`, `gesture:`, `lua:`, `voice:`, `focus`,
`view:`. BIND offers 12 of them as presets; bindings saved with older presets (`toggle:browser`, `spin:40`, …)
keep working.

A pose fires its bindings only after it is held for 2 s in a row (`BindingHold`); taps (clap,
swipes) fire at once; a binding never fires while that hand is on a window. No binding opens or closes a window by
default (`RetireWindowShortcuts`, once); one binding is made once by default (`EnsureVictoryFocus`, marker
`.victory-focus` in the plugins folder): the V (`victory`, from the core-poses example, installed when missing) turns
Ambient Focus on or off. The other poses have no binding.

Skills for agents (bundled into the userland at `/root/.claude/skills` and into MQH threads):
`quest-gesture-plugins` (procedure, formats, hand model, API, recipes, testing), `quest-scripting`,
`metacodex-context`, `quest-views`, `worktree-per-thread` (own git worktree when the project folder is busy), plus 36 Unity skills (github.com/Unity-Technologies/skills, refreshed by
`tools/sync-agent-skills.sh`) and 7 Meta skills for hand tracking, interaction and comfort.

## Live behaviours: single scripts

The Lua API of scripts and app plugins is in [scripting.md](scripting.md).

`/mnt/quest/scripts/*.lua` (skill `quest-scripting`): same Lua API, one file per behaviour. Settings are JSON
(mirrored to MQH as the UI preference `quest.settings`, which agents can edit through the MQH API; the MQH
token is not synced). Note: at the first sync after a start the app takes MQH's copy when its revision is not 0, so a
setting saved without `OnSettingsChanged` (which pushes) comes back old after a restart; state that changes on its
own (the threads used last, `recent-threads.txt`) lives in its own file instead.

## Agents and threads (MQH)

All data from MQH's public API on 127.0.0.1:38990: `/system/providers/state` (sign-in state and login
command per provider), `/system/execution-options?providerId=&hostId=` (models), `/projects`, `/threads`,
`/threads/:id/timeline`, `/threads/:id/send`, `/threads/:id/stop`, `/threads/:id/interactions`,
`/settings/machine-environment`. Sign-in never goes through MetaCodex: the provider CLI prints a link,
the terminal frame offers `⇗`, the Horizon browser opens it, and the CLI keeps its own credentials (a sign-in
page never opens in the in-app WEB window: `ShellWeb.IsSignInPage`).

## Agent views

Agents show things as panels instead of text (skill `quest-views`, the MCP-like context they read): a
fenced ```` ```mcx-view {json} ```` or ```` ```mermaid ```` block in a thread message (registered when the
message arrives, opened when it arrives while the thread is open; the timeline shows `[▣ title]`), or from
Linux `mcx-ui view show spec.json | view mermaid f.mmd | view list | view wait <id> | view schema`
(control port `view …`). One spec: `{id, title, size, columns, components:[…]}` with `text`, `stats`,
`chart` (bar, hbar, line, area, scatter, donut, pie, sparkline; one axis, the validated colour-blind-safe
palette in order, legend from 2 series, hover readout), `graph` / `mermaid` (flowchart with layered layout,
sequence, pie), `table` (drag to scroll), `code` / `diff` / `log`, `progress`, `checklist`, `timeline`,
`kanban`, `kv`, `choices` and `input` (the answer goes back as the user's next message, or to
`view wait`), `image` (PNG/JPG from Linux) and `scene3d` (a Unity physics stage on a shelf in front of the
view: rigid bodies with per-stage gravity, carried along when the panel moves, pushed by the index
fingertips; RUN / PAUSE / RESET). The same id replaces a view in place (live charts). The newest 12 view
panels are kept; older ones rebuild from their spec when opened again (workspace VIEWS).

## Ambient Focus

Settings > FOCUS, FOCUS on the dock, `focus:on|off|toggle` (actions), voice ("open ambient focus", "turn on
ambient focus and set intensity to 0", "set the blur to fifty percent"). A dome stands on the floor around you:
inside it the room stays visible, outside it the passthrough darkens or, with BLUR, turns into a soft image of
itself. The soft image is a panorama of the room (`Resources/Shaders/FocusBlurPano`, 1024 x 512, world-oriented):
each camera frame is painted into it through a round mask, so it has no rectangle, stays with the room when you
turn your head (what was seen is kept; what was never seen is the plain darkness), and starts over after you walk
1.5 m away. The camera runs at 15 fps (for the blur and for the sharp depth). A voice command does not stop it:
while speech is turned into text and the command decided, the camera keeps running and its frames are not used.
The depth and the camera stay on, idle, for 60 s after nothing needs them any more (a restart costs seconds of low
frame rate: measured 17.7 fps for ~15 s on the Quest 3S). RADIUS is the dome's size (from the floor under your head), SOFTNESS the width of its edge, INTENSITY
the darkness, BLUR how soft the room outside gets (0 = darkened only); BLACKOUT (radius 0, 100 %) leaves only the
windows and your hands. No room scan of the headset is needed.

- **The void.** Outside the dome it is
  not black but deep space: blue-black with a slow nebula drifting in two layers (a tiling noise drawn by code,
  world-oriented, so it stays with the room) and sparse stars that twinkle; on the room's surfaces just past the edge
  a faint holographic grid (50 cm, lines only across each surface, from its own slope), with a pulse running outwards
  now and then; where the room crosses the dome, a contour of light (a ring on the floor round you). With BLUR only
  the grid and the contour, over the blurred room. All of it is skipped inside the dome, where most of the picture is.
- **Live depth.** The headset's depth API (`EnvironmentDepthManager`, via `com.unity.xr.meta-openxr` and the OpenXR
  features "Meta Quest: Session" + "Occlusion"; permission "spatial data", `USE_SCENE`) runs while focus or "real
  things in front of windows" is on (and 60 s more, idle). Meta's occlusion shaders stay off (no extra depth pass).
- **Sharp depth** (`Resources/Shaders/DepthSharp`, once per new depth frame, both eyes): the sensor's 320 x 320 depth
  at 1.5 x the resolution (480 x 480) when the camera guides it, at the sensor's resolution without the camera. Each
  texel uses the 2 x 2 readings around it. Where the camera sees, its picture guides the result (joint bilateral
  upsampling: each reading weighs by distance and by how alike its colour is, so depth edges follow the picture's
  edges). Elsewhere an edge-preserving depth-only filter. Then a
  temporal filter (last frame's result reprojected, kept where it agrees within 2 cm + 2 %), so the cut-out stops
  flickering and its edge is cleaner. "Sharp depth (camera)" in FOCUS (on): off, the camera does not guide the depth
  (it still runs for BLUR). Costs: [perf.md](perf.md#depth-features).
- **The dome**: the focus sphere (`Resources/Shaders/AmbientFocus`) reads the sharp depth along each pixel's view ray
  and rebuilds the real surface's world position (inverse of `_EnvironmentDepthReprojectionMatrices`), so walls,
  furniture and hands near you stay and far ones fade.
- **The floor** is the tracking floor, corrected by the depth seen under your feet once three readings 0.3 s apart
  agree within 5 cm (the system's own floor can be 10-30 cm off): a 48 x 48 copy of the depth
  (`FocusDepthCopy`) read back, the lowest level near you with enough points (not a table, not a patch read too
  high). FOCUS shows depth, floor and camera status.
- **Blur.** BLUR above 0 starts the left passthrough camera (MR Utility Kit's `PassthroughCameraAccess`, 640 x 480,
  15 fps; permission `horizonos.permission.HEADSET_CAMERA`, asked once). Each frame is copied into a 256 x 192
  mip-mapped texture; outside the dome the sphere draws it at a high mip (4 taps), projected per pixel through the
  camera's pose and intrinsics (depth gives the right parallax) and darkened by INTENSITY. The camera sees less
  than the passthrough: where it has not looked yet, the colours of that part of the room (or of all it has seen,
  else a dim haze) cover the passthrough, frosted, so the whole room outside the dome is soft at once and detail
  fills in as you look round.
  Horizon OS composites the passthrough itself, so an app cannot blur the passthrough directly.
- **Without depth** (permission not given yet, the Editor) the floor plane stands in: the floor near you stays,
  everything above the horizon fades.
- **Hands** are cut out of everything (soft capsules along the tracked bones, `FocusMesh`, drawn after the windows and
  depth-tested against the opaque ones): a hand in front of a window, in the darkness or in the blur always shows,
  also during the system gesture (the gesture engine marks the hand untracked then; the cut-outs use the raw tracking).
  Always, whatever the settings.
- **Real things in front of windows** (FOCUS toggle, on): the room's depth, for what is not a hand (people, things):
  every UI shader (`Shaders/McxOcclusion.hlsl` in SciFiUI,
  TextBend, UiIcon, ViewImage, PanelUnlit, StageLit; the terminal's glyphs use TextBend now) fades where the sharp
  depth puts a real surface 2-6 cm or more in front of it, so people and objects in front of a window stay visible.
  Only windows that something real can be in front of pay for this test: every 0.3 s the room's 48 x 48 depth points
  are checked against each open window (a point between the eye and the window, inside its rectangle grown by 15 cm).
  A window is "clear" after 6 readings in a row that see all of it with nothing in front, and at least 4 s after the
  last thing seen in front. Its shaders then skip the depth test (`_McxPanelClear`, `McxVisibilityIn`). A window that
  opens, moves or turns is tested again until readings clear it; without readings for 2 s every window is tested.
  A thing thinner than ~2 degrees (the points' spacing) can be missed; a thing that comes in front of a clear window
  shows behind it for up to ~0.4 s (the hands always show: the cut-outs). The setting runs the depth sensor and the
  sharp depth; off saves that GPU work, and the hands still show.
- **The system menu**: while the app has lost input focus (a system menu is open), the windows and the focus
  darkness fade out (`_McxUiHide`), so the system menu shows; they come back after. The system gesture itself (palm
  towards you, pinch) hides nothing, although the headset reports it while the palm faces you.

## Layouts

- **Fixed**: panels are world-locked where you leave them, bent towards you when "Bend panels around you" is
  on (terminals stay flat). **Where a new window opens** (`ShellPlacement`: `PlaceSmart`, `OldPlaceStillGood`,
  `PlacementOpener`, `KeepCardsOffDock`; every open window is a rectangle in your view, bent or flat):
  1. A new window opens where you look, at the panel distance (DISPLAY > DISTANCE), its middle just below eye level,
     turned to you. "Where you look" is where your body faces when you look down at the DOCK or the wrist menu.
  2. It never covers another window, and it never sits on or under the DOCK: it keeps 3 degrees off the DOCK in your
     view, and its middle is never lower than the DOCK's top edge.
  3. It never covers the Whisp's menu or its messages while they show, nor the Whisp while it listens, thinks or
     speaks. The idle Whisp moves aside by itself when a window comes to its place.
  4. A window opened from another window (a thread from THREADS, a picker, a details window) opens beside that
     window, on its right when there is room. A window in a thread group counts as its group. Dialogs open over
     their window.
  5. A window opened again in the same session comes back where you left it, when that place is still in view and
     free.
  6. When nothing is free in view, the window opens a little further away (up to twice the distance, smaller in
     view). When even that is full, it opens at the nearest free place round you (at most 100 degrees to a side,
     never behind you), and the Whisp points to it.
  7. Only when nothing is free anywhere does it open in front of you over the others, a little nearer each time.
  8. Small cards never touch the DOCK: the tour card is raised when it comes within 2 degrees of it.
  9. While the room's depth runs, its last reading (the 48 x 48 points, every 0.3 s) says how far the real things
     are: a window opens at least 8 cm in front of a wall or another real thing, and a direction where something
     real is nearer than half a metre is a poor place (nothing nearer than 0.55 m counts: that is a hand).
  10. RECENTER turns the layout round you as one piece and moves only a window that would cover another.
  A CONTROL window keeps room for its rail on its left by the degrees
  the rail really covers (it is turned towards you), so the pair sits right beside its neighbour. THREAD's sidebar
  opens at the thread's right edge, so the pair spans about 95 degrees: when it opens, the pair glides round you
  (0.45 s) until it is centred where you face, as far as the other windows (2 degrees apart) and the room's walls
  (8 cm) let it; when it folds and you have not moved the thread meanwhile, the thread glides back. Named layouts (Settings > DISPLAY) store the open panels relative to the head and put
  them back around wherever the user is.
- **Float**: panels sit on an invisible cylinder around you that follows you horizontally (x/z), never
  your head's pitch or roll, 4° apart; when the open panels do not fit around it the cylinder grows, so
  they never overlap, and a panel you drop slides to the nearest free place at its height. Close your hand
  in empty space and swipe to spin it (inertia, no snapping). Panels bend to the cylinder; terminals stay
  flat. The dock stays low in front. The thread and its sidebar take one slot together: the sidebar
  sits at the thread's right edge, on its curve.
- Boundaryless: the app declares `com.oculus.feature.BOUNDARYLESS_APP` (passthrough only).

## Back from a native app: the panels stay in the room (`Shell/ShellRoomAnchor.cs`)

- Why it is needed: the app is resumed, not rebuilt (the activity is `singleTask`), and no code places panels on resume.
  The tracking space is set up again when the app resumes. The panels keep their Unity coordinates, so the room
  moves under them.
- Meta docs: a Floor Level or local space is not an app-independent reference; Meta recommends spatial anchors for it
  (`documentation/unity/unity-ovrcamerarig`, Tracking Origin Type). The Stage space is not changed by the user's
  re-centre; the boundary docs say to check it "when the app returns from a paused state"
  (`documentation/unity/unity-ovrboundary`). Boundaryless apps: Stage is "ill-defined" (`documentation/unity/unity-boundaryless`,
  World-locked contents). The Stationary origin is stable across sessions, but experimental: it needs the manifest flag
  `com.oculus.experimental.enabled` and a device property (`documentation/unity/unity-experimental-features`).
  Spatial anchors need the permission `com.oculus.permission.USE_ANCHOR_API` and OVRManager Anchor Support
  (`documentation/unity/unity-spatial-anchors-persist-content`).
- How: while the app is live, the shell reads the Stage origin (the room reference) every 0.25 s. On resume, once the
  head is tracked again (two frames later, at most 10 s), the whole shell (`_root`: every window, the Whisp, the wrist
  menu, the toasts) moves by the change of that reference. Each panel keeps its place relative to the room. No
  permission, no manifest flag, no PlaceSmart on a normal resume.
- Refused, nothing moves (logged): a reference move over 3 m, a turn over 120 deg, or no reference on resume.
- The log line on resume: `room anchor: back in the room after the native app: panels moved X cm (most Y cm), turned Z deg, N open`.
- The system's own re-centre does not change the Stage reference, so it is not undone.
- Not tested on a headset yet ([TEST_CHECKLIST.md](TEST_CHECKLIST.md#windows-placement-and-layouts)). If Stage is not stable here, switch
  `RoomReferenceOrigin` to `OVRPlugin.TrackingOrigin.Stationary` (then the manifest flag above) or use spatial anchors.
- Self-test: section `roomanchor` (`MetaCodexUiTestRoomAnchor.cs`).

## Testing without a headset

`tools/unity-batch.sh BatchCheck` = scene rebuild + terminal self-test + UI self-test
(`Bench/selftest/ui-report.txt` and `ui-*.png`). The UI test drives the router with test pointers and the
gesture engine with simulated hands (any pose and motion), runs the double-clap and gesture-plugin stories
end to end (including a script and a plugin written by real agents: `tools/agent-script-test.sh`,
`tools/agent-plugin-test.sh`), the vault (sealing, tamper check, policies, VR approval), sign-in link
detection, themes, saved layouts, git through WSL, the float carousel, and MQH (providers, models,
projects, a real thread, machine environment) when `tools/mqh-dev.sh start` runs. On the headset the
control port adds `ui`, `orb`, `script`, `plugin`, `gesture`, `hand`, `action` and `secret` commands (`tools/qterm`,
or `mcx-ui` / `mcx-secret` inside the userland).

## Stone-metal frames and buttons

In `Shaders/SciFiUI.shader` (no extra meshes): each corner of a window has a carved stone-metal piece (the outline
offset 3 mm, 9 mm thick, 45-degree tips, an engraved groove with energy, a node on the cut), slim stone rails run
between them, and a carved plate with an energy gem sits over the top centre (windows wider than 0.24 m); the blue
light leaks inward only. Normal and filled buttons are carved slate plates with an engraved channel 2.6 mm inside the
edge, energy in the button's colour (dim at rest, bright hovered, full when filled; red danger stays red). List rows
and ghost buttons keep their glass look. The grain and the glass's lattice come from two small textures that
`UiShapeMesh` makes at start (`_McxStoneNoise`, `_McxHexField`), and `ChamferNormal` is analytic
([perf.md](perf.md#ui-shader-scifiui)).

## Start splash

A splash in passthrough, so the user is never blind at start. `ShellSplash`: the transparent logo (`Resources/Brand/splash.png`, 1280 x 722) 0.60 m wide, a metre ahead of where the user
faces, at eye level, upright and facing them, fixed in the room (it does not follow the head), on top of everything
(`Resources/Shaders/Splash`, queue Overlay, no depth test). It shows in the first frame, comes in behind a
scan line running down (0.7 s), holds at least 1.4 s and until the shell is built behind it (at most 8 s more; see
[perf.md](perf.md#first-start)), then breaks up into light (0.9 s: a noise dissolve
with an accent edge, growing 5 %), and is destroyed with its texture. Not in batch runs.

