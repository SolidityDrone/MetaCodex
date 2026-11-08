# MetaCodex spatial UI: style, panels, gestures, plugins, agents, layouts

Status: v0.6, 2026-11-08. v0.5: the owner's bug list from the 0.4 test and faster voice. v0.6: threads
grouped by project with a linked workspace module (changes map, git, files, views), bb-like composer chips
(model, access, context) and FORK, agent views (charts, diagrams, tables, choices, physics), GIT moved into
the vault with the GitHub account, Ambient Focus, the SYSTEM panel (installed tools, storage). Tested in
the Editor; not yet on the headset.
Test list for the headset: `docs/TEST_CHECKLIST.md`.

## Style

A calm, standard look (owner, 0.5: no themes or accents for now): dark frosted-glass panels with rounded
corners, a fine border lit from above, soft shadows instead of glows, one blue accent for primary actions,
selections and toggles, Inter (SemiBold for titles and buttons, Medium for text) with DejaVu Sans Mono for
paths and code; labels written in capitals in code show in Title Case (acronyms kept). All OFL / free
licences, no game or brand assets. Short UI sounds are synthesised in code.

Everything is drawn by one shader, `MetaCodex/SciFiUI` (the name stays for references): each panel, button,
header, bar, line, dot and selection frame is a quad whose rounded shape is computed per pixel from vertex
data (size, radius, fill style, border, state), so a whole panel is one mesh and one draw call, crisp at any
distance. Text is TextMeshPro with runtime SDF fonts (OpenType features off: Inter's kerning tables cost
over 1 GB as glyphs were added). LOOK settings: sharpness (render scale), panel opacity, shadows,
foveated rendering (off by default; it blurs the edges of the view), sounds and volume.

## Code map (`unity/Assets/MetaCodex/Runtime`)

| Folder | What |
|---|---|
| `UI/` | `UiTheme` (colours, sizes, fonts), `UiShapeMesh` (batched shapes, bending, rotated strokes), `UiPanel` (frame, header, open animation, drag, bend), widgets (`UiButton`, `UiToggle`, `UiSegmented`, `UiSlider`, `UiTextField`, `UiList`, `UiTabs`, `UiProgress`, `UiDivider`), `UiViz` (canvases drawn from pooled shapes: `UiChart`, `UiFlow`, `UiSequence`, `UiTable`, `UiCodeView`, `UiRing`, `UiChangeMap`, `UiToolGrid`, `UiStorageBar`; the series palette), `UiMermaid` (flowchart / sequence / pie subset), `UiKeyboard` (Horizon OS system keyboard), `UiInputRouter` (pointers, beams, reticles, selection), `UiToasts`, `UiModal`, `UiSounds` |
| `Hands/` | `GestureEngine` (built-ins, pose templates, custom gestures with hold/cooldown, head-relative directions), `PoseSpec`, `OvrHandSource` (OVRHand/OVRSkeleton), `SimulatedHands` (any pose, for tests) |
| `Scripting/` | `ScriptHost`: Lua (MoonSharp) scripts and plugin folders, hot reload, sandbox, instruction budget, detectors as persistent coroutines, persisted state; `PoseJson` |
| `Security/` | `SecretVault` (Android Keystore sealer, Editor dev sealer, policies, access log) |
| `AI/` | `QvacHost` (starts the local AI service process, token, restarts), `QvacClient` (HTTP), `VoiceRecorder` (microphone, end of speech), `VoiceCommands` (registry, phrase templates, tool specs) |
| `Shell/` | `MetaCodexShell` (wiring, panels by id and aliases, control-port commands), `ShellPanels` (dock, terminals, files, git logic, browser, scripts, settings, saved layouts), `ShellGestures` (Gestures studio, bindings, actions, recording, starter plugins), `ShellAgents` (agents, threads grouped by project, thread with composer chips and fork, new thread, terminal links), `ShellWorkspace` (the thread's linked workspace module, the picker), `ShellViews` + `ViewStage` (agent views, the physics stage), `ShellVault` (vault: secrets and GIT with the GitHub account; secret broker), `ShellVoice` (voice pipeline, shell commands, VOICE panel, HUD), `ShellAmbient` (Ambient Focus), `ShellSystem` (installed tools, storage), `ShellLayout` (fixed / float carousel, the thread + workspace pair), `Services` (system deep links, settings, MQH client, git, files) |
| `../Resources/Shaders/` | `AmbientFocus`, `StageLit` (physics bodies), `ViewImage` (images in views): in Resources so they ship without material assets |
| `../Plugins/Android/MetaCodexVault.java` | the Keystore bridge (AES-256-GCM, StrongBox or TEE) |

## Panels

| Panel | Does |
|---|---|
| Dock (two rows of 9, a grab bar on top to move it) | TERM (shows the hidden terminal, else a new one), terminals, files, threads, agents, web, scripts, gestures, SYSTEM / TALK (push-to-talk), voice, vault, git (the vault's GIT tab), FOCUS (Ambient Focus on/off), select tool (one selection, then off), keyboard, recenter, setup |
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
| System | TOOLS: a tile per known tool (runtimes, package managers, compilers, git, editors, network, agent CLIs) with its version, plain category glyphs; a tap installs (in a terminal, low priority), updates or removes it; PACKAGES… (Debian packages you chose, npm globals). STORAGE: the headset (Linux, AI models, shared folder, apps + system, free), Linux by folder with one-tap cleanups (apt, npm, bun, pip/uv, cargo, temp), a soft LINUX LIMIT (Android cannot reserve or cap an app's space: the app warns past it) |
| Voice | the local AI service (state, RAM), models (install with progress, loaded or not), TALK, TYPE… (a command as text), SAY… (text to speech), spoken replies, LLM fallback, auto stop, language, threads, the command list (shell + plugins), the last requests with timings (`docs/voice.md`) |
| Voice HUD | above the dock while a voice request runs: listening level, transcript, result; STOP |
| Settings | GESTURES (click by pinch/poke/both, move panels by header pinch or closed hand, ray mode, pinch threshold, clap, plugin gestures on/off, select tool), DISPLAY (layout fixed/float, distance, carousel radius, scale, bending, save / load named layouts, recenter), FOCUS (Ambient Focus: on/off, radius, intensity, softness, presets SUBTLE / FOCUS / BLACKOUT), LOOK (sharpness, opacity, shadows, foveation, sounds), DAEMON (MQH URL and token, sync, status, plugins), ABOUT |

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

Settings > FOCUS, FOCUS on the dock, `focus:on|off|toggle` (actions), "focus mode on" (voice). A sphere
around the head (`Resources/Shaders/AmbientFocus`) is drawn before everything else and darkens the
passthrough: per pixel, by how far the view ray meets the floor plane (tracking is floor-level), with
RADIUS where the fade starts, SOFTNESS its width and INTENSITY the darkness; above the horizon there is no
distance, so it fades fully. Panels, terminals and views draw on top: BLACKOUT (radius 0, 100 %) leaves only
the windows. Why not real distances: Meta's Depth API needs the Unity OpenXR Meta package (and AR
Foundation) with this project's OpenXR loader; the floor plane needs nothing. Passthrough can be darkened,
not blurred (Horizon OS composites it).

## Layouts

- **Fixed**: panels are world-locked where you leave them, bent towards you when "Bend panels around you" is
  on (terminals stay flat); new panels open where you look, in the
  nearest free gap (within a head turn, ±110°); when none is left they cascade in front, each a little
  closer, higher and to the side. Named layouts (Settings > DISPLAY) store the open panels relative to the head and put
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
control port adds `ui`, `script`, `plugin`, `gesture`, `hand`, `action` and `secret` commands (`tools/qterm`,
or `mcx-ui` / `mcx-secret` inside the userland).
