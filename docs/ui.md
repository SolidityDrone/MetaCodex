# MetaCodex spatial UI: style, panels, gestures, plugins, agents, layouts

Status: v0.6, 2026-11-08. v0.5: the owner's bug list from the 0.4 test and faster voice. v0.6: threads
grouped by project with a linked workspace module (changes map, git, files, views; since 2026-11-11 the thread sidebar, docs/thread-sidebar.md), bb-like composer chips
(model, access, context) and FORK, agent views (charts, diagrams, tables, choices, physics), GIT moved into
the vault with the GitHub account, Ambient Focus, the SYSTEM panel (installed tools, storage). Tested in
the Editor; not yet on the headset. v0.7 (owner's 0.6 bug list): the sci-fi console look is back and
richer, opaque windows are truly opaque, bending is smooth and done on the GPU, Ambient Focus is a dome from
the floor that follows the real room, voice hears and chains commands better. Since 2026-11-10 the app shows
no logos at all: SYSTEM > TOOLS is a plain list in groups (owner; contest rules §4), see `docs/LICENSES.md`.
2026-11-12 (branch feat/polish): menus and layout tidied (docs/control-center.md): one table for the dock and the
wrist, CONTROL's pages by id and in one size, buttons that say the one thing they do, setup links where they belong.
Test list for the headset: `docs/TEST_CHECKLIST.md`.

## Style

A real-time-strategy command console (owner: "keep the StarCraft 2 style, make it much better"; the design is
our own, no game assets): thick dark navy glass, chamfered corners (a big cut top-left and bottom-right, small
ones elsewhere), a steel frame lit from above with corner brackets, a stepped header energy bar with a lead
block before the title, a pulse running in it and a hatched edge, command-card buttons with a bevel, accent
ticks at their cuts and a sheen crossing them while hovered, quiet list rows (glass, a notch, a hair line) that
the accent floods into when chosen, segmented energy bars for progress and sliders, cyan accent (#35D0FF) with
amber / red / green for warning, danger and ok; the light that casts a window is a warm white.

**The glass** (owner 2026-11-10: "much better aesthetics"; then "I like the texture, not that the panels seem to have a
depth"). A hexagon lattice and a grid of points between its cells are printed on the glass, flat: 0.7.9 drew them
behind the surface with parallax (each eye's ray met them 12 and 30 mm deep, and a sheen moved with the head), and
the windows looked deep; 0.7.10 keeps the texture and drops the depth. A ruler runs down the left edge (a tick every 10 mm), a conduit of energy along the
bottom from the right cut with a pulse in it. **Your hands light it**: a fingertip within 14 cm of a window, or
the cursor of a hand's ray, is a point of light on the lattice and the frame (`_McxPointer0` / `_McxPointer1`, set by
the router every frame); the pool tightens as the finger comes closer, so it also shows how far the glass still is. Lattice
lines fade out with distance before they get thinner than a pixel (no shimmer). Titles and buttons use Rajdhani SemiBold in capitals with letter spacing (the console voice), body text
Inter, code DejaVu Sans Mono. All OFL / free licences. Short UI sounds are synthesised in code.

Everything is drawn by one shader, `MetaCodex/SciFiUI`: each panel, button, header, bar, line, dot and
selection frame is a quad whose shape is computed per pixel from vertex data (size, chamfer, fill style,
border, state), so a whole panel is one mesh and one draw call, crisp at any distance. A change of colour or state
only (a hover, a press, a selection, the flash after a click) rewrites that shape's colours and states in place and
uploads those two streams; only a change of size, place or visibility rebuilds the panel's geometry (0.7.10: every
hover and every frame of a click's flash rebuilt the whole mesh, thousands of vertices on a big panel). Text is TextMeshPro
with runtime SDF fonts drawn by `MetaCodex/TextBend` (TMP mobile SDF plus the panel bend). No logos or
pictures of third-party software anywhere. LOOK settings (the render scale is fixed at 1.6): panel opacity, shadows,
foveated rendering (off by default; it blurs the edges of the view), sounds and volume.

**Opacity.** Passthrough is an underlay: the eye buffer's alpha decides how much of the room shows. Every UI
shader blends its alpha as `One OneMinusSrcAlpha` (coverage only adds), so a shadow or a glow over a window
can never cut a see-through fissure. At PANEL OPACITY 75 % and above, a depth-only pass writes the panel's
body (1.5 mm behind its face), so text, icons and buttons of windows behind it are hidden, not shown through.

**Bending.** BEND PANELS curves each window around a vertical axis on the GPU: the panel's radius goes into
a global table (`_PanelBend`, 128 slots) and every renderer of the panel (shapes, text, sub-meshes, icons)
carries its slot, so text that changes every frame bends with the panel and never flashes flat. The
curvature eases in and out (about 0.3 s) instead of jumping. Rays and pokes hit the curved surface (ray-
cylinder intersection), so the reticle sits where the beam meets the window.

**Opening and closing: a window is cast** (owner 2026-11-10; `UiPanelFx`, `McxPanelFx.hlsl`). It is drawn with
light, like a hologram being printed, by its caster (`UiPanelFx.Caster`: the orb, when there is one). Opening
(0.64 s): four corner brackets fly in and lock on where the window will be, with a faint outline of it; a fan of
light opens from the caster to the edge nearest to it; a hot
warm-white line, beads of light running in it, sweeps across between the brackets with the fan on it. Behind
the line the window exists: first a glowing hexagon lattice whose cells close from their rims inwards and cool
into glass, the frame, then the controls and icons coming online hot, the texts flickering for a moment; before
the line nothing is there yet. The line runs on 16 cm past the far edge, so everything has cooled when it
stops, a last pulse goes through the lattice and the frame, and the final frames are the window at rest (the
flying brackets hand over to the frame's own, same shape and light). Closing (0.44 s): a surge, the line runs
back and takes the window with it, the brackets let go, and a spark of what is left flies home to the caster. An
open window asked to open again pings: the caster lights it up, its frame flashes, the brackets close in round it
once. Small passing panels (messages, the voice HUD) use a quick variant (0.30 s in, 0.22 s out, no brackets). Both directions play back from where they are when the user changes their mind. For
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
brackets, the scan line, the fan and its two rays, the spark), bent like the window; on a bent window a
horizontal line is an arc and the fan follows it. Captures render a window at rest. Sounds follow: two ticks as
the brackets lock on, a rising sweep with a shimmer, a low thump and a clear note; a falling sweep, a quick
rise as the spark flies home.

## The orb

A sphere of energy that floats by the user (owner 2026-11-10: "a protoss energy sphere ... the origin that casts
panels ... the source of the speech ... a billboard side notification system menu"; `UiOrb` is its body,
`ShellOrb` what it does, `MetaCodex/Orb` how it is drawn). CONTROL > LOOK & SOUND > Orb turns it off (key `orb`, voice
"turn the orb off"); then windows are cast from their left edge and messages float in the upper view as before.

- **How it looks.** One quad that faces each eye; every pixel follows its own ray through a unit sphere, so the
  orb has true depth per eye: a white-hot core, two shells of plasma filaments turning against each other (a
  sheared, twice-warped sine field; the inner shell seen on its near and far side, the outer on its near side), a
  dark glass body with a bright limb, three thin rings round it (two of warm gold, one dashed in its own colour; the
  distance between the ray and each circle, so a ring is a tube from every side and passes behind the sphere), four
  motes, a halo. Premultiplied: its light is added over the room and its body darkens what is behind it, so it
  reads in a bright room; a depth pass on its disc keeps a hand that is behind it from cutting it out.
- **Why the shader is plain straight-line code** (0.7.10). On the headset 0.7.9's orb showed flat cream blocks,
  stripes and half discs over the sphere in two frames out of three, never in the Editor; they flickered frame to
  frame, in a fixed colour whatever the orb's mood, in screen-aligned blocks: the Quest's GPU going wrong on that
  shader, not its light (the depth was not even running then). That version had nested loops reading uniform
  arrays, ternaries choosing between arrays, its inputs as globals outside any constant buffer, and a derivative
  taken after an early return. Now: no loops, no arrays, every input an ordinary material property in
  `UnityPerMaterial` (SRP Batcher compatible), the pixel-size derivative first and no early return. It is also
  about 40 % cheaper per frame: three plasma evaluations instead of four, four motes without tails instead of twelve
  points, and a quad of 2.3 radii instead of 2.7 (27 % fewer pixels).
- **Moods.** Idle: calm cyan, slow. Listening: it turns gold and its three rings turn to face you, one inside the
  other. Thinking: violet, the plasma and the rings spin up. Speaking: its core pulses with the loudness of the
  words and a ripple leaves it at every syllable; the spoken answer comes from its place (the AudioSource sits
  on it, 80 % spatial). A message: it flashes in the message's colour and the plates of a hexagon shield run
  over it. Casting: its rings turn to face the window and the fan goes out; a hand near it or a ray on it: it
  brightens and turns its rings to that hand. It comes and goes with its own ignition (0.7 s / 0.4 s).
- **Where it floats.** Up to the right of where you face (30 degrees right, 17 up, 0.52 m), in a frame that
  follows you lazily: it stays where it is in the room while you only glance round, and comes along when you
  turn (more than 34 degrees for 0.7 s; sooner while it has something to say) or walk away (25 cm), moving round
  you, never through your head. It never sits in front of a window: when one covers its place for half a second
  it takes the next free one (ten candidates, both sides, higher up) and goes back three seconds after its place
  is free again (it looks at one candidate a frame, and stays put while it casts). Pinch it and put it anywhere:
  it keeps that place by you (`orbPinned`, `orbYaw`, `orbPitch`, `orbDist`); its menu's PLACE lets it choose again.
- **What it says.** Every message of the app (`Toast`) is a callout: a card cast by the orb on an arc round its
  **right** side (owner 2026-11-11: "always on its left and notification on right", never swapped), concentric with
  it: the stack is centred on the orb's level, the newest lowest, up to four, each card's corner nearest the orb on
  one circle round it (so the ends bow round it), every card turned to you, joined to the orb by a rail of light that
  runs out of its rings and along the arc; the voice HUD is the first card while it is shown. Touch the orb with a
  fingertip, or point at it and pinch: its menu opens round its **left** side (owner 2026-11-11: "more like wrapped
  around the circle of the orb"): four plain squares on one arc that hugs the orb (from half past ten to half past
  seven on a clock, clear of its rings), each turned to you, cast with the quick variant: ◉ TALK, ♫ SPEAK (spoken
  answers on or off), ✥ PLACE, and ✉ LOG ("✉ n NEW" while there are unread messages), which opens LOG · MESSAGES, a
  window like the others (placed where there is room; also "open messages"): the last 40 messages (kind, text, how
  long ago) and CLEAR ALL (red; asks first, as CLIPBOARD's does). Pinning it (let go) and PLACE save its place once, on
  release, through the settings path (`OnSettingsChanged`: pushed to MQH when quiet, so MQH's copy does not bring an
  old place back).
  Labels 13.5 mm: about a degree at the menu's half a metre (Meta's VR text guidance). A message not read yet rides
  round the orb as a larger gold spark (four at most) until MESSAGES is opened. A hand that is at the orb or points at
  it does not also click the window behind it (`UiInputRouter.HandBlocked`). The first time the app runs with the
  orb, one message says what it is (`orbTipShown`).
- **The menu's squares** (owner 2026-11-13: "remove the outer box and leave the square"). Each item is a `UiPanel`
  made `frameless`: it has no frame, shadow or halo of its own. Only its button shows, and the button fills the
  square (60 × 54 mm at scale 1: `OrbActW`, `OrbActH`). The squares are 8 mm apart, the same all round (`OrbActGap`).
  The top and bottom ones sit 45 degrees off the level. The two between them sit where the gap is even
  (`OrbActRadius`, `OrbActCentre`: sin(inner) = sin(outer) / 3). The light still crosses a frameless panel, because
  `UiPanelFx` reads the panel's size, not its frame. `UiPanel.SetHighlight` and `SetPlainBody` do nothing on one.
- **It keeps off the window you watch** (owner 2026-11-11: "avoid any weird position ... cover my view on the panels
  i'm watching ... less invasive but always available"; `Shell/ShellOrbAvoid.cs`). The watched window is the first
  one your gaze meets (looking at the orb, its menu or a callout keeps the one before), for 2 s after your gaze
  leaves it. What the orb takes of your view (its disc, its open menu's arc on the left, its callouts' arc on the
  right, as ShellPlacement's view rectangles) must not cover it: when it would, the orb glides aside or below (above
  costs a little more) to the nearest place where nothing of it covers that window, its disc in front of no other
  window, within 60 degrees of where you look; slowly (0.4 s), never while a hand is at it or at its items (nothing
  moves under a finger), and back to its own place 1.5 s after that is free again. Idle and not looked at it is
  80 % of its size and dimmer; a look or a hand brings it back.
- **No thread list on the orb.** Its menu had cards beside the squares, one per recently used thread (owner
  2026-11-11). Owner 2026-11-13: "remove open thread dials next to the orb, they are bad ux". They are removed, with
  their code and their checks. The threads used last stay in `recent-threads.txt` (beside the settings, not in them:
  MQH's copy of the settings would bring an old list back): `LastUsedThread` (`Shell/ShellReturn.cs`) reads them for
  the way back from an app. The self-test `orbplace` keeps off the watched window and tests the thread + sidebar pair in FIXED.
- Control port: `orb on | off | menu | place | status`.

## Code map (`unity/Assets/MetaCodex/Runtime`)

| Folder | What |
|---|---|
| `UI/` | `UiTheme` (colours, sizes, fonts), `UiShapeMesh` (batched shapes, rotated strokes), `UiPanel` (frame, header, the cast, drag, GPU bend and cast slots, curved ray hits), `UiPanelFx` (the cast's timelines, its scan plane and its light), `UiOrb` (the orb's body: moods, motion, the uniforms of its shader), widgets (`UiButton`, `UiToggle`, `UiSegmented`, `UiSlider`, `UiTextField`, `UiList`, `UiTabs`, `UiProgress`, `UiDivider`), `UiViz` (canvases drawn from pooled shapes: `UiChart`, `UiFlow`, `UiSequence`, `UiTable`, `UiCodeView`, `UiRing`, `UiChangeMap`, `UiToolList` (SYSTEM > TOOLS: groups and rows, no icons), `UiStorageBar`; the series palette), `UiMermaid` (flowchart / sequence / pie subset), `UiKeyboard` (Horizon OS system keyboard), `UiInputRouter` (pointers, beams, reticles, selection), `UiToasts`, `UiModal`, `UiSounds` |
| `Hands/` | `GestureEngine` (built-ins, pose templates, custom gestures with hold/cooldown, head-relative directions), `PoseSpec`, `OvrHandSource` (OVRHand/OVRSkeleton), `SimulatedHands` (any pose, for tests) |
| `Scripting/` | `ScriptHost`: Lua (MoonSharp) scripts and plugin folders, hot reload, sandbox, instruction budget, detectors as persistent coroutines, persisted state; `PoseJson` |
| `Security/` | `SecretVault` (Android Keystore sealer, Editor dev sealer, policies, access log) |
| `AI/` | `QvacHost` (starts the local AI service process, token, restarts), `QvacClient` (HTTP), `VoiceRecorder` (microphone, end of speech), `VoiceCommands` (registry, phrase templates, tool specs), `ThreadPulse` (a thread's timeline and state, docs/thread-view.md), `WorkspaceMap` (checkouts and worktrees, docs/workspaces.md) |
| `Shell/` | `MetaCodexShell` (wiring, panels by id and aliases, control-port commands), `ShellPanels` (dock, terminals, files, git logic, LINKS (id browser), MQH's SCRIPTS tab, SETTINGS' pages, saved layouts), `ShellControl` (CONTROL's rail and categories, the one size of its windows), `ShellWrist` (the main menu table `MainMenuItems`, the wrist menu), `ShellGestures` (Gestures studio, bindings, actions, recording, the gesture examples and the one-time cleanup of the old starter copies), `ShellAgents` (agents, threads grouped by project, thread with composer chips and fork, new thread, terminal links, the threads used last), `ShellWorkspace` (the thread's linked workspace module, the picker), `ShellViews` + `ViewStage` (agent views, the physics stage), `ShellVault` (vault: secrets and GIT with the GitHub account; secret broker), `ShellVoice` (voice pipeline, shell commands, VOICE panel, HUD), `ShellAmbient` (Ambient Focus), `ShellSystem` (installed tools, storage), `ShellLayout` (fixed / float carousel, the thread + workspace pair), `ShellPlacement` (where a panel opens), `ShellOrb` (the orb: where it floats, hands, the voice, callouts, its menu of squares), `Services` (system deep links, settings, MQH client, git, files) |
| `../Resources/Shaders/` | `AmbientFocus` (the dome: depth, floor plane, blur), `FocusMesh` (hand cut-outs), `FocusDepthCopy` (floor readback), `TextBend` (TMP SDF + bend), `UiIcon` (textured quads + bend; no tool logos any more), `StageLit` (physics bodies), `ViewImage` (images in views), `Orb` (the orb), `WristArt` (the wrist menu and the cast's light): in Resources so they ship without material assets; `../Shaders/McxPanelFx.hlsl` (the cast's scan plane, shared by every shader that draws inside a window) |
| `../Plugins/Android/MetaCodexVault.java` | the Keystore bridge (AES-256-GCM, StrongBox or TEE) |

## Panels

| Panel | Does |
|---|---|
| Dock (two rows of 6, a grab bar on top to move it; owner 2026-11-11: fewer, docs/control-center.md) | TALK, TERM (shows the hidden terminal, else a new one), FILES, THREADS, WEB, CONTROL; TERMS, CLIP, FOCUS (on / off), SELECT, KEYS, CENTER: the wrist menu's table (`MainMenuItems`) in its order, without TABLE. A tile is selected while what it opens or switches is on, as the wrist's tiles are lit (checked 4 times a second, colours only). Setup (agents, MQH, vault and git, voice, system, settings) is in CONTROL |
| Wrist menu (MAIN MENU on the wrist: Settings > DISPLAY, or "turn on the wrist menu") | the dock's 12 actions and TABLE (inner ring: TALK, TERM, FILES, THREADS, WEB, CONTROL; outer: TERMS, CLIP, FOCUS, SELECT, KEYS, TABLE, CENTER) (MetaCodex's keyboard for the terminal, with how to lay it on a table) on the left wrist (`UiWristMenu`, `ShellWrist`): look at the back of the wrist as at a watch and a watch face is projected over it (a beam from the wrist, a hexagon of glass with the time, sixty ticks and a seconds hand); touch it with the right index fingertip (no pointing, no pinch: the fingertip goes into the surface) and it unfolds: a shock ring, TALK / TERM / FILES / THREADS / WEB / CONTROL swinging in clockwise, the other seven the other way, each drawing its line icon with a scan line and typing its name, under a bezel and drifting arcs. Touch a tile to run it (flash, ring, sparks); lit tiles (a strip of light) show what is on. Every change is animated both ways and plays back from where it is (never pops). Stable: a 1-euro filter on the wrist; while the right hand is at it, or over the left hand, it holds still and never closes (that hand hides the left one from the cameras); jumps of the tracked wrist are ignored until they persist; a lost left hand keeps it 1.5 s, a wrist turned away 0.45 s; a slide across it presses nothing. Folds after 7 s untouched. One procedural mesh (`WristArt` shader: glass tiles, lines, icons from `UiIconAtlas`, a signed-distance atlas drawn by code, the beam, the face), the glass's depth (`WristDepth`) and two small particle systems (`WristSpark`) |
| Keyboard (`UiSurfaceKeyboard`, `ShellKeyboard`; every text field, the terminal, WEB's ⌨) | MetaCodex's own: Horizon OS's keyboard is never opened (owner 2026-11-10: "I don't ever wanna see the default one"; apps cannot turn on or summon the Experimental Surface Keyboard, so this does what it does). It opens floating in front, tilted like a lectern (poke or pinch its keys). Rest both hands flat on a table for a moment: it lies down on the table under them, F under the left index tip and J under the right, its keys scaled to the hands' spread (16 to 25 mm), and stays locked there (the next text finds it there while you are within a metre). Typing: a fingertip that lifts above 13 mm and taps back down to 5.5 mm over where that hand rests, moving down, types the key under it; resting fingers type nothing; three fingers of one hand landing within 80 ms are a hand put down, not keys; thumbs type the bottom row only; ⌫ and the arrows repeat while held; ⇧ once, twice for ⇪; CTRL and ALT for the next key (terminal chords); ⎘ pastes the clipboard; ⌨↓ puts it away. The strip on its far edge says what is being typed into, the text so far (masked for secrets) and the modifiers. On the right, the TOUCHPAD: one finger moves a cursor over the window you look at when the touch starts (with acceleration), a tap without moving clicks there, two fingers scroll a list. Fields end with ⏎ (or ESC: back to what was there); the terminal and WEB keep it until ⌨↓ or KEYS |
| Terminal frames | every terminal in a frame: drag by the header, `⇗` open the link the terminal printed (sign-in flows), `⎘` paste the clipboard, `⌨` keyboard, `+` new, `–` hide (the shell keeps running), `✕` end it (the last one is hidden instead: "TERM on the menu shows it"), tap to focus. The first terminal starts hidden: TERM on the menu (dock or wrist) shows it |
| Terminals | list of terminal instances (hidden ones marked): new, show, hide, close |
| Files | the Linux userland's folders (`/root`, `/mnt/quest`, `/`): open folders, cd / view / edit / paste path; FILES APP (the end of its row) opens Horizon's Files app |
| WEB (the in-app browser, `ShellWeb`) | a window like the others, beside them (Horizon OS cannot show its own browser next to an immersive app: even Web Tasks send the app to the background). The page is Android's WebView (`Plugins/Android/MetaCodexWebView.java`): laid out off screen at 1280 × 800, drawn into an `ImageReader` only when the page changes (30 fps at most, nothing while the window is closed), copied into one buffer that Unity uploads into the page's texture (`AndroidJNI.GetDirectByteBuffer`: a view, no copy, no unsafe code). Toolbar ◀ ▶ ⟳, the address (localhost:5173 → http, a word with a dot → https, words → a search), GO, ⌨ (types into the page's focused field), ⏎ ⌫, ☰ (LINKS), ⇗ (this page in Horizon's browser). A pinch or a fingertip on the page is a touch, a drag scrolls, the pointer hovers; the window never bends (`ShellLayout.StaysFlat`). Opens on a start page with the bookmarks and the local servers found. Remote debugging is on: `chrome://inspect` on the PC (USB adb) shows the page's console and DOM. Every page the app opens goes here (GO, bookmarks, local servers, terminal links, voice `open_url`, Lua `browser.open`) except sign-in pages (`SignInHosts`, `SignInPaths`: claude.ai, anthropic, openai, Google, GitHub /login, …/oauth…): those open in Horizon's browser, also when a page links to one, so no account's web session ever stays in the app's cookies |
| LINKS (panel id `browser`, alias `links`; titled LINKS, WEB is the browser) | the address field, bookmarks (add / remove), local servers on 127.0.0.1 (⟳ SCAN of the usual dev ports); a tap opens the page in WEB. Horizon's system pages moved where they belong: HORIZON HANDS ⚙ to HANDS, APP PERMISSIONS ⚙ to ABOUT, FILES APP to FILES |
| Scripts (MQH > SCRIPTS) | the Lua scripts in /mnt/quest/scripts only (plugins are in MQH > PLUGINS): status (ON / OFF / ERROR), ENABLE, ⟳ RELOAD, + EXAMPLE (double clap toggles TERMINALS), FOLDER, DELETE, log; empty: one line "No scripts · + EXAMPLE, or ask an agent (skill quest-scripting)". The hands' live view is GESTURES > LIVE only |
| Gestures (MQH > GESTURES) | studio: ALL (grouped like SKILLS: YOURS = my-poses and every plugin or script gesture that is not an untouched example, "none · RECORD a pose or ASK AN AGENT" when empty; SYSTEM (7) = the built-ins with their role: pinch click, grab move · spin, point ray, open, palm-up, clap two hands, select select tool; EXAMPLES, folded (tap to open): `core-poses` and `swipes`, + ADD installs one, ADDED lists its gestures under it. TEST ▶ and BIND → for any gesture; SOURCE and DELETE POSE only for YOURS, + ADD only for an example not installed; the list refreshes by itself), LIVE (finger curls of both hands, palm/thumb direction, velocity, active gestures, recent events, Lua detector cost), BIND (gesture → action, no code; 12 presets: TALK, TERMINALS, NEW TERMINAL, THREADS, FILES, WEB, GIT, KEYBOARD, SELECT, RECENTER, FOCUS, CTRL-C; CUSTOM ACTION… for any other action), RECORD (3-2-1, the hand's pose becomes a named gesture in plugin `my-poses`; one line points to MQH > PLUGINS for new plugins) |
| Agents | GET (owner 2026-11-10): OPENCODE, CLAUDE CODE, CODEX (each: its official npm package installed in a terminal you watch, `npm i -g --allow-scripts=<pkg> <pkg>@latest`; ✓ and the version once it is on the PATH) and MORE · COMING SOON (not clickable). Then the supported agents only (owner 2026-11-10: Claude Code, Codex, OpenCode, in that order; Cursor, Pi and any other provider MQH has are not listed and are switched off in MQH, so no thread, model list or probe uses them), each with sign-in state, version, account (OpenCode, shown by MQH only once installed, comes from its execution options); ⚿ API KEY: SUPPORTED PROVIDERS for that agent (OpenCode: OpenCode Zen / Go, Anthropic, OpenAI, OpenRouter, Google Gemini, xAI, DeepSeek; Claude Code: Anthropic Console; Codex: OpenAI), ⎘ PASTE or type, SAVE: sealed in the vault (policy allow) and put into MQH's machine environment, so every agent MQH starts finds it (OpenCode reads those variables itself); a key MQH did not take yet is tried again every 20 s and when MQH comes up. No links to providers' sites anywhere (owner: never). SIGN IN runs the agent's own login in a new terminal (⇗ on that terminal opens its link in Horizon's browser); each button says the one thing it does now: ENABLE or DISABLE (in MQH), UPDATE through MQH only for an installed agent (GET is how one is installed); NEW THREAD (Codex and OpenCode). The rail's status: how many are ready, else "sign in". Claude Code runs in a terminal for now (TERMINAL): its MQH provider plugin carries Anthropic's SDK, so it is not in the APK (installed at first use, next) |
| Threads | like bb's sidebar: each project folder (+ PROJECT from a Linux folder) with its threads under it, newest first; the project row shows how many run or need you, a tap folds it; a tap on a thread opens it. A project with a worktree shows its folders as rows (▣ checkout, ⎇ each worktree with its base; 1 WAITING, 2 WORKING ⚠), the threads under the folder they work in (owner 2026-11-11, docs/workspaces.md); ⎇ WORKSPACES, ◆ AGENTS |
| Workspaces | every folder threads work in, per project: who works there and who waits, its git state against its base; MERGE INTO base (dry run first: git merge-tree; conflicts go to the worktree's agent), NEW THREAD HERE, REMOVE (archive + delete the worktree, the branch stays), OPEN THREAD, FILES, TERMINAL HERE. MQH holds a turn while another thread works in the same folder (SEND NOW in THREAD overrides it) |
| Thread | a chat (owner 2026-11-11, docs/thread-view.md): your prompt in a bubble (pinned on top once it scrolls away), the agent's work under one header per exchange (open while it works, closed after: thoughts with their first sentence, commands with ✓ / ✕ and time, edits with line counts, reads grouped; a tap opens a thought, an output, a closed header), the answer in full (markdown-lite, streaming with a caret; views as `[▣ title]`); drag or ▲▼, ↓ NEW when scrolled up. The status line under it: what it does now, for how long, quiet / may be stuck, waiting for you, answered. The meta row: where it works, and the plan and usage limits (5h / week / month). Approvals (ALLOW ONCE / SESSION / DENY, only while one is pending), composer + ◉ (say it: speech to text into the composer) + SEND, and the chips under it: MODEL (+ effort), ACCESS (Edits / Auto / Full access), the workspace (project · checkout or worktree), the CONTEXT ring (use of the model's window; tap: compact or fork, the usage windows with their resets), STOP; on top FORK…, ⟳; on its right edge the sidebar's handle |
| Thread sidebar | the handle on THREAD's right edge opens it, attached at the thread's right edge (moves, bends and closes with it; each thread remembers it and its tab; docs/thread-sidebar.md). CHANGES: every file the thread changed (git against HEAD or the worktree's base, untracked included; outside git the agent's edits), a VS Code-like diff (numbers, +/- gutter, green / red lines, syntax colours, folds, wrap or sideways scroll). FILES: the project folder with a breadcrumb, a read-only code view. GIT: branch, status, commits; no repo: INIT GIT only. STATUS: model, context, what runs, approvals, usage, MQH. VIEWS: the thread's agent views |
| Views | agent views (below): one panel per view, named "View · title"; the same id updates in place |
| Picker | a short list in front of the panel that asked: model, effort, access, fork target, context, packages |
| New thread | provider, model (from MQH's catalogue for that provider), permissions (EDITS / AUTO / FULL), workspace (project checkout or a new git worktree), first message, START |
| Vault | SECRETS: secrets (names only), where the key lives (StrongBox / TEE), policy (ASK / ONCE / ALLOW / DENY), new value (secure keyboard), → MQH ENV, re-arm, delete, access log; approval dialogs for requests from Linux (`docs/secrets.md`). GIT (was the GIT panel; `git` still opens it): the GitHub account (TOKEN… sealed as `GITHUB_TOKEN`, TEST asks api.github.com who it is, IDENTITY… sets user.name / email, GIT USES IT installs the credential helper `!mcx-secret git-credential GITHUB_TOKEN` for github.com, → AGENTS gives GH_TOKEN / GITHUB_TOKEN to MQH threads) and the repositories (status, branches, history, fetch / pull / commit / push) |
| System | TOOLS: a plain list in groups (agents, JavaScript, Python, native code, Android and Quest, git, terminal utilities, editors), one row per known tool: a status dot (lit = installed), its name, what it does in a few words, and its version or how to get it; no logos or icons; the list is also written for agents as `/mnt/quest/system/tools.json` (`mcx-ui system tools [json]`), so they know what is installed without probing the terminal; a tap installs (in a terminal, low priority), updates or removes it; PACKAGES… (Debian packages you chose, npm globals). STORAGE: the headset (Linux, AI models, shared folder, apps + system, free), Linux by folder with one-tap cleanups (apt, npm, bun, pip/uv, cargo, temp), a soft LINUX LIMIT (Android cannot reserve or cap an app's space: the app warns past it) |
| Voice | the local AI service (state, RAM), models (install with progress, loaded or not), TALK, TYPE… (a command as text), SAY… (text to speech), spoken replies, LLM fallback, auto stop, threads, the command list (shell + plugins), the last requests with timings (`docs/voice.md`) |
| Voice HUD | above the dock while a voice request runs: listening level, transcript, result; STOP |
| CONTROL (owner 2026-11-11, docs/control-center.md) | one place for setup: a rail on the left of the shown window with ten categories in three groups and a live status each (THIS HEADSET: DISPLAY, LOOK & SOUND, FOCUS, HANDS; AI: VOICE, AGENTS, MQH, VAULT; LINUX & APP: SYSTEM, ABOUT), in the display font. One category at a time: another one takes the window's place (the six windows have one size, 0.76 x 0.66 m: same rectangle, the rail stays); drag the window or the rail's bar, the other follows; ✕ closes both; FLOAT gives the pair one slot. SETTINGS' pages have ids (display, look, focus, hands, about); `control` opens the last category; old ids still open their page (agents, mqh, vault, git, skills, gestures, daemon, storage, ...); voice: "control panel", "control center" |
| Settings (CONTROL's DISPLAY, LOOK & SOUND, FOCUS, HANDS and ABOUT; no tab bar of its own: a line says what the page is for) | HANDS (tab GESTURES: click by pinch/poke/both, move panels by header pinch or closed hand, ray mode, hand rays, PINCH AT, clap and its distance, plugin gestures on/off; SELECT AREA ⌖, GESTURE STUDIO ▸ (MQH > GESTURES), HORIZON HANDS ⚙; the live hand line), DISPLAY (layout fixed/float, DISTANCE (dimmed in FLOAT: "FIXED only"), FLOAT RADIUS (dimmed in FIXED: "FLOAT only"), UI scale, bending (both layouts), the main menu on the wrist, save / load named layouts, recenter), FOCUS (Ambient Focus: on/off, radius, intensity, softness, blur, real things in front, sharp depth, presets SUBTLE / FOCUS / BLACKOUT), LOOK & SOUND (opacity, shadows, foveation, sounds and volume, the orb), ABOUT (version, licences: LICENCES ▸ opens FILES at /usr/share/doc/metacodex; APP PERMISSIONS ⚙). The link to MQH is MQH > STATUS. CLIPBOARD's size is CLIPBOARD's own KEEP slider |

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
instruction budget; a runaway detector stops only its plugin. Nothing is installed on first run (2026-11-12):
the two examples `core-poses` (thumbs-up, thumbs-down, victory, horns, call-me; data only) and `swipes` (swipe
left / right / up; Lua) are opt-in (GESTURES > ALL > EXAMPLES > + ADD, or `mcx-ui plugin example <id>`;
`mcx-ui plugin examples` lists them). Builds before then seeded them: the first start of a newer build removes
those copies once (marker `.examples-v2` in the plugins folder) when they are untouched (byte for byte the
shipped files) and unused (no binding, script or other plugin's Lua names one of their gestures); edited or
used copies stay, my-poses and other folders are never touched. New plugins: MQH > PLUGINS > + NEW PLUGIN…
(its template's reaction is a toast only). Any gesture can be bound to an action without code (GESTURES > BIND, or
`mcx-ui gesture bind <gesture> <action>`); actions are one vocabulary shared by bindings, Lua
(`app.action`) and the control port: `open:` `close:` `toggle:<panel>`, `toast:`, `run:`, `type:`, `url:`,
`layout:`, `spin:`, `select`, `keyboard`, `terminal:new`, `recenter`, `gesture:`, `lua:`, `voice:`, `focus`,
`view:`. BIND offers 12 of them as presets; bindings saved with older presets (`toggle:browser`, `spin:40`, …)
keep working.

Skills for agents (bundled into the userland at `/root/.claude/skills` and into MQH threads):
`quest-gesture-plugins` (procedure, formats, hand model, API, recipes, testing), `quest-scripting`,
`metacodex-context`, plus 36 Unity skills (github.com/Unity-Technologies/skills, refreshed by
`tools/sync-agent-skills.sh`) and 7 Meta skills for hand tracking, interaction and comfort.

## Live behaviours: single scripts

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
1.5 m away. The camera runs at 15 fps when only the blur needs it (30 with sharp depth) and pauses while a voice
command is transcribed and decided, so speech-to-text keeps its cores. RADIUS is the dome's size (from the floor under your head), SOFTNESS the width of its edge, INTENSITY
the darkness, BLUR how soft the room outside gets (0 = darkened only); BLACKOUT (radius 0, 100 %) leaves only the
windows and your hands. No Space Setup scan is needed (branch `feat/spatial-dome`, `docs/SPATIAL_DOME_PLAN.md`).

- **The void** (owner 2026-11-09: "in the fog some interesting effect, not simple black"). Outside the dome it is
  not black but deep space: blue-black with a slow nebula drifting in two layers (a tiling noise drawn by code,
  world-oriented, so it stays with the room) and sparse stars that twinkle; on the room's surfaces just past the edge
  a faint holographic grid (50 cm, lines only across each surface, from its own slope), with a pulse running outwards
  now and then; where the room crosses the dome, a contour of light (a ring on the floor round you). With BLUR only
  the grid and the contour, over the blurred room. All of it is skipped inside the dome, where most of the picture is.
- **Live depth.** Meta's Depth API (`EnvironmentDepthManager`, via `com.unity.xr.meta-openxr` and the OpenXR
  features "Meta Quest: Session" + "Occlusion"; permission "spatial data", `USE_SCENE`) runs while focus or "real
  things in front of windows" is on. Meta's occlusion shaders stay off (no extra depth pass).
- **Sharp depth** (`Resources/Shaders/DepthSharp`, every frame, both eyes): the sensor's 320 x 320 depth at twice the
  resolution, guided by the passthrough camera's picture where the camera sees (joint bilateral upsampling: each of
  the 3 x 3 readings around a texel weighs by distance and by how alike its colour is, so depth edges follow the
  picture's edges; SandyBridge `docs/DEPTH_UPSCALE_PLAN.md`, option A), an edge-preserving depth-only filter
  elsewhere, and a temporal filter (last frame's result reprojected, kept where it agrees within 2 cm + 2 %), so the
  cut-out stops flickering and its edge is cleaner. "Sharp depth (camera)" in FOCUS (on).
- **The dome**: the focus sphere (`Resources/Shaders/AmbientFocus`) reads the sharp depth along each pixel's view ray
  and rebuilds the real surface's world position (inverse of `_EnvironmentDepthReprojectionMatrices`), so walls,
  furniture and hands near you stay and far ones fade.
- **The floor** is the tracking floor, corrected by the depth seen under your feet once three readings 0.3 s apart
  agree within 5 cm (SandyBridge's rule: the Guardian floor can be 10-30 cm off): a 48 x 48 copy of the depth
  (`FocusDepthCopy`) read back, the lowest level near you with enough points (not a table, not a patch read too
  high). FOCUS shows depth, floor and camera status.
- **Blur.** BLUR above 0 starts the left passthrough camera (MR Utility Kit's `PassthroughCameraAccess`, 640 x 480,
  30 fps; permission `horizonos.permission.HEADSET_CAMERA`, asked once). Each frame is copied into a 256 x 192
  mip-mapped texture; outside the dome the sphere draws it at a high mip (4 taps), projected per pixel through the
  camera's pose and intrinsics (depth gives the right parallax) and darkened by INTENSITY. The camera sees less
  than the passthrough: beyond its image the plain darkness stays. Horizon OS composites the passthrough itself,
  so an app cannot blur the passthrough directly.
- **Without depth** (permission not given yet, the Editor) the floor plane stands in: the floor near you stays,
  everything above the horizon fades.
- **Hands** are cut out of everything (soft capsules along the tracked bones, `FocusMesh`, drawn after the windows and
  depth-tested against the opaque ones): a hand in front of a window, in the darkness or in the blur always shows,
  also during the system gesture (the gesture engine marks the hand untracked then; the cut-outs use the raw tracking).
  Always, whatever the settings (0.7.10: until then they were drawn only with Focus or "Real things in front of
  windows" on, and with that toggle off, as on the owner's headset, every window covered the hands).
- **Real things in front of windows** (FOCUS toggle, on): the room's depth, for what is not a hand (people, things):
  every UI shader (`Shaders/McxOcclusion.hlsl` in SciFiUI,
  TextBend, UiIcon, ViewImage, PanelUnlit, StageLit; the terminal's glyphs use TextBend now) fades where the sharp
  depth puts a real surface 2-6 cm or more in front of it, so people and objects in front of a window stay visible.
  It runs the depth sensor and a sharpening pass every frame; off saves that GPU work, and the hands still show.
- **The Horizon OS menu**: while the app has lost input focus (a system menu is open), the windows and the focus
  darkness fade out (`_McxUiHide`), so the system menu shows; they come back after. The system gesture itself (palm
  towards you, pinch) hides nothing (0.7.3: Horizon OS reports it while the palm faces you, and the windows used to
  vanish until the hands were lowered).

## Layouts

- **Fixed**: panels are world-locked where you leave them, bent towards you when "Bend panels around you" is
  on (terminals stay flat). **Where a new panel opens** (`ShellPlacement`, owner 2026-11-10: "smart positions"):
  every open panel is a rectangle in your view (the yaw and pitch its corners span, bent or flat), and the new one
  takes the free place that costs least: in front of where you face, just below eye level, when that is free; else
  the nearest free place round it, to the side or, when that is nearer than a far turn of the head, above a window
  (below only as a last resort: the hands and the dock are there; never higher than 50 degrees or lower than 42),
  tilted to face you. A panel opened from a window's button (a thread from THREADS, VOICE from SYSTEM, a view from
  a thread) opens right beside that window, to its right when there is room. A panel that was open before comes
  back where you left it when that place is still free and within 70 degrees of where you face; else it is placed
  anew. "In front" is where your body faces when you look down at the wrist menu or the dock to press (the frame
  the orb floats in), not where your nose points. And never inside a wall: while the room's depth runs, its last
  reading (the 48 x 48 points the floor is found from, every 0.3 s) says how far the real things are in each
  direction; a panel opens at least 8 cm in front of them, and a direction where something real is nearer than
  half a metre is a poor place (a hand in front of the eyes is not the room: nothing nearer than 0.55 m counts).
  When nothing is free at the panel distance, a panel opens further away (up to twice as far, so smaller in the
  view) in the nearest gap, tilted to face you; only when nothing is free even there do panels cascade in front,
  each a little closer, higher and to the side. A CONTROL window keeps room for its rail on its left by the degrees
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

## Stone-metal frames and buttons (2026-11-12, branch feat/stone-ui)

Owner: "metallish-stone with blueish glowing, in the style of an RTS console, just an improvement to our current UI", "for panels wrapping and buttons only". In `Shaders/SciFiUI.shader` (no new meshes or textures): the panel's corner brackets became carved stone-metal pieces round each corner (the outline offset 3 mm, 9 mm thick, 45-degree tips, an engraved groove with energy, a node on the cut), slim stone rails between them and a carved plate with an energy gem over the top centre (windows wider than 0.24 m); the blue leaks inward only. Normal and filled buttons are carved slate plates with an engraved channel 2.6 mm inside the edge, energy in the button's colour (dim at rest, bright hovered, full when filled; red danger stays red). List rows and ghost buttons keep their look. Helpers `Stone`, `StoneGrain`, `ChamferNormal`; prototype `~/mcxwork/proto/frame.py` (frame-v4 / v5).
