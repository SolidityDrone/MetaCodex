# MetaCodex spatial UI: style, panels, gestures, plugins, agents, layouts

Status: v0.6, 2026-11-08. v0.5: the owner's bug list from the 0.4 test and faster voice. v0.6: threads
grouped by project with a linked workspace module (changes map, git, files, views), bb-like composer chips
(model, access, context) and FORK, agent views (charts, diagrams, tables, choices, physics), GIT moved into
the vault with the GitHub account, Ambient Focus, the SYSTEM panel (installed tools, storage). Tested in
the Editor; not yet on the headset. v0.7 (owner's 0.6 bug list): the sci-fi console look is back and
richer, opaque windows are truly opaque, bending is smooth and done on the GPU, Ambient Focus is a dome from
the floor that follows the real room, tools show their own logos, voice hears and chains commands better.
Test list for the headset: `docs/TEST_CHECKLIST.md`.

## Style

A real-time-strategy command console (owner: "keep the StarCraft 2 style, make it much better"; the design is
our own, no game assets): thick dark navy glass, chamfered corners (a big cut top-left and bottom-right, small
ones elsewhere), a steel frame lit from above with corner brackets, a stepped header energy bar with a lead
block before the title, a pulse running in it and a hatched edge, command-card buttons with a bevel, accent
ticks at their cuts and a sheen crossing them while hovered, quiet list rows (glass, a notch, a hair line) that
the accent floods into when chosen, segmented energy bars for progress and sliders, cyan accent (#35D0FF) with
amber / red / green for warning, danger and ok; the light that casts a window is a warm white.

**The glass has depth** (owner 2026-11-10: "much better aesthetics"). A hexagon lattice lies 12 mm behind the
surface and a grid of points 30 mm behind it: each is drawn where the eye's own ray meets that depth (the
vertex shader gives the view direction in the panel's frame, bent or flat), so they shift against the frame as
the head moves and each eye sees its own shift: real stereo depth for the price of two offsets. A sheen crosses
the glass with the head. A ruler runs down the left edge (a tick every 10 mm), a conduit of energy along the
bottom from the right cut with a pulse in it. **Your hands light it**: a fingertip within 14 cm of a window, or
the cursor of a hand's ray, is a point of light on the lattice and the frame (`_McxPointers`, set by the router
every frame); the pool tightens as the finger comes closer, so it also shows how far the glass still is. Lattice
lines fade out with distance before they get thinner than a pixel (no shimmer). Titles and buttons use Rajdhani SemiBold in capitals with letter spacing (the console voice), body text
Inter, code DejaVu Sans Mono. All OFL / free licences. Short UI sounds are synthesised in code.

Everything is drawn by one shader, `MetaCodex/SciFiUI`: each panel, button, header, bar, line, dot and
selection frame is a quad whose shape is computed per pixel from vertex data (size, chamfer, fill style,
border, state), so a whole panel is one mesh and one draw call, crisp at any distance. Text is TextMeshPro
with runtime SDF fonts drawn by `MetaCodex/TextBend` (TMP mobile SDF plus the panel bend); tool logos are
one atlas drawn by `MetaCodex/UiIcon`. LOOK settings (the render scale is fixed at 1.6): panel opacity, shadows,
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
`ShellOrb` what it does, `MetaCodex/Orb` how it is drawn). Settings > LOOK > Orb turns it off (key `orb`, voice
"turn the orb off"); then windows are cast from their left edge and messages float in the upper view as before.

- **How it looks.** One quad that faces each eye; every pixel follows its own ray through a unit sphere, so the
  orb has true depth per eye: a white-hot core, two shells of plasma filaments turning against each other (a
  sheared, twice-warped sine field, seen on the near and the far side of each shell), a dark glass body with a
  bright limb, three thin rings round it (two of warm gold, one dashed in its own colour; the distance between
  the ray and each circle, so a ring is a tube from every side and passes behind the sphere), motes with short
  tails, a halo. Premultiplied: its light is added over the room and its body darkens what is behind it, so it
  reads in a bright room; a depth pass on its disc keeps a hand that is behind it from cutting it out. Cost:
  the plasma only inside the sphere, the rings only where a ring can be (a ray further from the centre than the
  ring's radius cannot meet it), the motes within 1.8 radii, and all light is windowed to zero inside the quad.
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
- **What it says.** Every message of the app (`Toast`) is a callout: a card cast by the orb in a column that
  rises beside it, towards the middle of your view, from its own level; the newest lowest, up to four, joined to
  the orb by a rail of light; the voice HUD is the first card of that column while it is shown. Touch the orb
  with a fingertip, or point at it and pinch: its menu opens beside it and below (cast with the quick variant), with the last 40 messages
  (kind, text, how long ago) and four actions: TALK, SPEAK (spoken answers on or off), PLACE, CLEAR. A message
  not read yet rides round the orb as a larger gold spark (four at most) until the menu is opened; the menu's
  first line says how many are new. A hand that is at the orb or points at it does not also click the window
  behind it (`UiInputRouter.HandBlocked`). The first time the app runs with the orb, one message says what it
  is (`orbTipShown`).
- Control port: `orb on | off | menu | place | status`.

## Code map (`unity/Assets/MetaCodex/Runtime`)

| Folder | What |
|---|---|
| `UI/` | `UiTheme` (colours, sizes, fonts), `UiShapeMesh` (batched shapes, rotated strokes), `UiPanel` (frame, header, the cast, drag, GPU bend and cast slots, curved ray hits), `UiPanelFx` (the cast's timelines, its scan plane and its light), `UiOrb` (the orb's body: moods, motion, the uniforms of its shader), `UiIconMesh` (tool logos atlas, batched icons), widgets (`UiButton`, `UiToggle`, `UiSegmented`, `UiSlider`, `UiTextField`, `UiList`, `UiTabs`, `UiProgress`, `UiDivider`), `UiViz` (canvases drawn from pooled shapes: `UiChart`, `UiFlow`, `UiSequence`, `UiTable`, `UiCodeView`, `UiRing`, `UiChangeMap`, `UiToolGrid`, `UiStorageBar`; the series palette), `UiMermaid` (flowchart / sequence / pie subset), `UiKeyboard` (Horizon OS system keyboard), `UiInputRouter` (pointers, beams, reticles, selection), `UiToasts`, `UiModal`, `UiSounds` |
| `Hands/` | `GestureEngine` (built-ins, pose templates, custom gestures with hold/cooldown, head-relative directions), `PoseSpec`, `OvrHandSource` (OVRHand/OVRSkeleton), `SimulatedHands` (any pose, for tests) |
| `Scripting/` | `ScriptHost`: Lua (MoonSharp) scripts and plugin folders, hot reload, sandbox, instruction budget, detectors as persistent coroutines, persisted state; `PoseJson` |
| `Security/` | `SecretVault` (Android Keystore sealer, Editor dev sealer, policies, access log) |
| `AI/` | `QvacHost` (starts the local AI service process, token, restarts), `QvacClient` (HTTP), `VoiceRecorder` (microphone, end of speech), `VoiceCommands` (registry, phrase templates, tool specs) |
| `Shell/` | `MetaCodexShell` (wiring, panels by id and aliases, control-port commands), `ShellPanels` (dock, terminals, files, git logic, browser, scripts, settings, saved layouts), `ShellGestures` (Gestures studio, bindings, actions, recording, starter plugins), `ShellAgents` (agents, threads grouped by project, thread with composer chips and fork, new thread, terminal links), `ShellWorkspace` (the thread's linked workspace module, the picker), `ShellViews` + `ViewStage` (agent views, the physics stage), `ShellVault` (vault: secrets and GIT with the GitHub account; secret broker), `ShellVoice` (voice pipeline, shell commands, VOICE panel, HUD), `ShellAmbient` (Ambient Focus), `ShellSystem` (installed tools, storage), `ShellLayout` (fixed / float carousel, the thread + workspace pair), `ShellPlacement` (where a panel opens), `ShellOrb` (the orb: where it floats, hands, the voice, callouts, its menu), `Services` (system deep links, settings, MQH client, git, files) |
| `../Resources/Shaders/` | `AmbientFocus` (the dome: depth, floor plane, blur), `FocusMesh` (hand cut-outs), `FocusDepthCopy` (floor readback), `TextBend` (TMP SDF + bend), `UiIcon` (logos + bend), `StageLit` (physics bodies), `ViewImage` (images in views), `Orb` (the orb), `WristArt` (the wrist menu and the cast's light): in Resources so they ship without material assets; `../Shaders/McxPanelFx.hlsl` (the cast's scan plane, shared by every shader that draws inside a window) |
| `../Resources/ToolIcons/` | tool logos (Simple Icons, CC0 SVG data; each logo is its owner's trademark, shown only to name the tool), built by `tools/make-tool-icons.js` |
| `../Plugins/Android/MetaCodexVault.java` | the Keystore bridge (AES-256-GCM, StrongBox or TEE) |

## Panels

| Panel | Does |
|---|---|
| Dock (two rows of 9, a grab bar on top to move it) | TERM (shows the hidden terminal, else a new one), terminals, files, threads, agents, web, scripts, gestures, SYSTEM / TALK (push-to-talk), voice, vault, git (the vault's GIT tab), FOCUS (Ambient Focus on/off), select tool (one selection, then off), keyboard, recenter, setup |
| Wrist menu (MAIN MENU on the wrist: Settings > DISPLAY, or "turn on the wrist menu") | the dock's 18 actions on the left wrist (`UiWristMenu`, `ShellWrist`): look at the back of the wrist as at a watch and a watch face is projected over it (a beam from the wrist, a hexagon of glass with the time, sixty ticks and a seconds hand); touch it with the right index fingertip (no pointing, no pinch: the fingertip goes into the surface) and it unfolds: a shock ring, TALK / TERM / FILES / THREADS / AGENTS / FOCUS swinging in clockwise, the other twelve the other way, each drawing its line icon with a scan line and typing its name, under a bezel and drifting arcs. Touch a tile to run it (flash, ring, sparks); lit tiles (a strip of light) show what is on. Every change is animated both ways and plays back from where it is (never pops). Stable: a 1-euro filter on the wrist; while the right hand is at it, or over the left hand, it holds still and never closes (that hand hides the left one from the cameras); jumps of the tracked wrist are ignored until they persist; a lost left hand keeps it 1.5 s, a wrist turned away 0.45 s; a slide across it presses nothing. Folds after 7 s untouched. One procedural mesh (`WristArt` shader: glass tiles, lines, icons from `UiIconAtlas`, a signed-distance atlas drawn by code, the beam, the face), the glass's depth (`WristDepth`) and two small particle systems (`WristSpark`) |
| Terminal frames | every terminal in a frame: drag by the header, `⇗` open the link the terminal printed (sign-in flows), `⎘` paste the clipboard, `⌨` keyboard, `+` new, `–` hide (the shell keeps running), `✕` end it (the last one is hidden instead), tap to focus. The first terminal starts hidden: TERM on the dock shows it |
| Terminals | list of terminal instances (hidden ones marked): new, show, hide, close |
| Files | the Linux userland's folders (`/root`, `/mnt/quest`, `/`): open folders, cd / view / edit / paste path |
| Browser | URL field (system keyboard) opens the Horizon OS browser (`systemux://browser`), bookmarks, local servers on 127.0.0.1, shortcuts to Horizon settings and Files |
| Scripts | Lua scripts and plugins: status, enable/disable, reload, example, delete, log; gesture monitor |
| Gestures | studio: GESTURES (all gestures with source, live state, bindings, TEST, source, delete pose), LIVE (finger curls of both hands, palm/thumb direction, velocity, active gestures, recent events, Lua detector cost), BIND (gesture → action, no code), RECORD (3-2-1, the hand's pose becomes a named gesture in plugin `my-poses`; new plugin from a template), PLUGINS (enable/disable/reload/delete/folder) |
| Agents | MQH's providers with sign-in state, version, account; SIGN IN runs the provider's own login in a new terminal; INSTALL / UPDATE through MQH; enable/disable; NEW THREAD; OPEN LINK |
| Threads | like bb's sidebar: each project folder (+ PROJECT from a Linux folder) with its threads under it, newest first; the project row shows how many run or need you, a tap folds it; a tap on a thread opens it |
| Thread | the timeline (you, agent, commands, tools, file edits with line counts, views as `[▣ title]`; newest at the bottom, ▲▼), approvals (ALLOW ONCE / SESSION / DENY, only while one is pending), composer + SEND, and the chips under it: MODEL (+ effort), ACCESS (Edits / Auto / Full access), the workspace (project · checkout or worktree), the CONTEXT ring (use of the model's window; tap: compact or fork), STOP; on top FORK…, WORKSPACE (the module on/off), ⟳ |
| Workspace (linked to the thread) | right of the thread, following it (drag it: it keeps that place relative to the thread; in FLOAT it rides beside it on the ring; it closes with the thread). CHANGES: a live map of the files the thread touched (tile size = lines changed, colour = added / changed / deleted / moved, lit when just edited; grey = uncommitted but not from this thread), the list, the diff of the picked file (@ ASK puts its path in the composer). GIT: branch, ahead/behind, status, branches, history; fetch, pull, commit, push. FILES: the folder tree, preview, EDIT in a terminal, @ ASK. VIEWS: the thread's agent views, DEMO |
| Views | agent views (below): one panel per view, named "View · title"; the same id updates in place |
| Picker | a short list in front of the panel that asked: model, effort, access, fork target, context, packages |
| New thread | provider, model (from MQH's catalogue for that provider), permissions (EDITS / AUTO / FULL), workspace (project checkout or a new git worktree), first message, START |
| Vault | SECRETS: secrets (names only), where the key lives (StrongBox / TEE), policy (ASK / ONCE / ALLOW / DENY), new value (secure keyboard), → MQH ENV, re-arm, delete, access log; approval dialogs for requests from Linux (`docs/secrets.md`). GIT (was the GIT panel; `git` still opens it): the GitHub account (TOKEN… sealed as `GITHUB_TOKEN`, TEST asks api.github.com who it is, IDENTITY… sets user.name / email, GIT USES IT installs the credential helper `!mcx-secret git-credential GITHUB_TOKEN` for github.com, → AGENTS gives GH_TOKEN / GITHUB_TOKEN to MQH threads) and the repositories (status, branches, history, fetch / pull / commit / push) |
| System | TOOLS: a tile per known tool (runtimes, package managers, compilers, git, editors, network, agent CLIs) with its own logo on its brand colour (a category glyph when it has none) and its version; the list is also written for agents as `/mnt/quest/system/tools.json` (`mcx-ui system tools [json]`), so they know what is installed without probing the terminal; a tap installs (in a terminal, low priority), updates or removes it; PACKAGES… (Debian packages you chose, npm globals). STORAGE: the headset (Linux, AI models, shared folder, apps + system, free), Linux by folder with one-tap cleanups (apt, npm, bun, pip/uv, cargo, temp), a soft LINUX LIMIT (Android cannot reserve or cap an app's space: the app warns past it) |
| Voice | the local AI service (state, RAM), models (install with progress, loaded or not), TALK, TYPE… (a command as text), SAY… (text to speech), spoken replies, LLM fallback, auto stop, language, threads, the command list (shell + plugins), the last requests with timings (`docs/voice.md`) |
| Voice HUD | above the dock while a voice request runs: listening level, transcript, result; STOP |
| Settings | GESTURES (click by pinch/poke/both, move panels by header pinch or closed hand, ray mode, pinch threshold, clap, plugin gestures on/off, select tool), DISPLAY (layout fixed/float, distance, carousel radius, scale, bending, save / load named layouts, recenter), FOCUS (Ambient Focus: on/off, radius, intensity, softness, presets SUBTLE / FOCUS / BLACKOUT), LOOK (opacity, shadows, foveation, sounds), DAEMON (MQH URL and token, sync, status, plugins), ABOUT |

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
instruction budget; a runaway detector stops only its plugin. First run installs two starter plugins:
`core-poses` (thumbs-up, thumbs-down, victory, horns, call-me; data only) and `swipes` (swipe left / right /
up; Lua). Any gesture can be bound to an action without code (Gestures > BIND, or
`mcx-ui gesture bind <gesture> <action>`); actions are one vocabulary shared by bindings, Lua
(`app.action`) and the control port: `open:` `close:` `toggle:<panel>`, `toast:`, `run:`, `type:`, `url:`,
`layout:`, `spin:`, `select`, `keyboard`, `terminal:new`, `recenter`, `gesture:`, `lua:`.

Skills for agents (bundled into the userland at `/root/.claude/skills` and into MQH threads):
`quest-gesture-plugins` (procedure, formats, hand model, API, recipes, testing), `quest-scripting`,
`metacodex-context`, plus 36 Unity skills (github.com/Unity-Technologies/skills, refreshed by
`tools/sync-agent-skills.sh`) and 7 Meta skills for hand tracking, interaction and comfort.

## Live behaviours: single scripts

`/mnt/quest/scripts/*.lua` (skill `quest-scripting`): same Lua API, one file per behaviour. Settings are JSON
(mirrored to MQH as the UI preference `quest.settings`, which agents can edit through the MQH API; the MQH
token is not synced).

## Agents and threads (MQH)

All data from MQH's public API on 127.0.0.1:38990: `/system/providers/state` (sign-in state and login
command per provider), `/system/execution-options?providerId=&hostId=` (models), `/projects`, `/threads`,
`/threads/:id/timeline`, `/threads/:id/send`, `/threads/:id/stop`, `/threads/:id/interactions`,
`/settings/machine-environment`. Sign-in never goes through MetaCodex: the provider CLI prints a link,
the terminal frame offers `⇗`, the Horizon browser opens it, and the CLI keeps its own credentials.

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
- **Real things in front of windows** (FOCUS toggle, on): every UI shader (`Shaders/McxOcclusion.hlsl` in SciFiUI,
  TextBend, UiIcon, ViewImage, PanelUnlit, StageLit; the terminal's glyphs use TextBend now) fades where the sharp
  depth puts a real surface 2-6 cm or more in front of it, so people, hands and objects in front of a window stay
  visible.
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
  When nothing is free panels cascade in front, each a little closer, higher and to the side. Named layouts (Settings > DISPLAY) store the open panels relative to the head and put
  them back around wherever the user is.
- **Float**: panels sit on an invisible cylinder around you that follows you horizontally (x/z), never
  your head's pitch or roll, 4° apart; when the open panels do not fit around it the cylinder grows, so
  they never overlap, and a panel you drop slides to the nearest free place at its height. Close your hand
  in empty space and swipe to spin it (inertia, no snapping). Panels bend to the cylinder; terminals stay
  flat. The dock stays low in front. The thread and its workspace take one slot together: the workspace
  sits right beside the thread and comes back there after a drag.
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
