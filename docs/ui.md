# MetaCodex spatial UI: style, panels, gestures, plugins, agents, layouts

Status: v0.4, 2026-11-07. v0.3 tested on the headset; v0.4 adds voice control (`docs/voice.md`), tested in the Editor against the AI service running on the headset.
Test list for the headset: `docs/TEST_CHECKLIST.md`.

## Style

An original sci-fi "command console" look: dark navy glass panels with cut corners (big cuts top-left and
bottom-right), bevelled glowing frames with brighter corner brackets, a light comet running round the frame,
a faint 2.5 cm grid and scanlines, cyan accents (amber warnings, green ok, red danger), condensed technical
type (Orbitron for titles and buttons, Rajdhani for text, DejaVu Sans Mono for paths and code; all OFL /
free licences). No game assets, names or logos. Short console sounds (click, open, close, toast, alert,
error) are synthesised in code; no audio files.

Everything is drawn by one shader, `MetaCodex/SciFiUI`: each panel, button, header, bar, line, dot and
selection frame is a quad whose shape is computed per pixel from vertex data (size, cut corners, border,
state), so a whole panel is one mesh and one draw call, crisp at any distance. Text is TextMeshPro with
runtime SDF fonts. Look settings (Settings > LOOK) are material values, so they cost nothing: theme
presets (COMMAND, STEALTH, ALERT, SOLAR, CLEAN), accent (9 colours), glow, panel opacity, motion speed,
scanlines, grid, sounds and volume.

## Code map (`unity/Assets/MetaCodex/Runtime`)

| Folder | What |
|---|---|
| `UI/` | `UiTheme` (colours, sizes, fonts), `UiShapeMesh` (batched shapes, bending), `UiPanel` (frame, header, open animation, drag, bend), widgets (`UiButton`, `UiToggle`, `UiSegmented`, `UiSlider`, `UiTextField`, `UiList`, `UiTabs`, `UiProgress`, `UiDivider`), `UiKeyboard` (Horizon OS system keyboard), `UiInputRouter` (pointers, beams, reticles, selection), `UiToasts`, `UiModal`, `UiSounds` |
| `Hands/` | `GestureEngine` (built-ins, pose templates, custom gestures with hold/cooldown, head-relative directions), `PoseSpec`, `OvrHandSource` (OVRHand/OVRSkeleton), `SimulatedHands` (any pose, for tests) |
| `Scripting/` | `ScriptHost`: Lua (MoonSharp) scripts and plugin folders, hot reload, sandbox, instruction budget, detectors as persistent coroutines, persisted state; `PoseJson` |
| `Security/` | `SecretVault` (Android Keystore sealer, Editor dev sealer, policies, access log) |
| `AI/` | `QvacHost` (starts the local AI service process, token, restarts), `QvacClient` (HTTP), `VoiceRecorder` (microphone, end of speech), `VoiceCommands` (registry, phrase templates, tool specs) |
| `Shell/` | `MetaCodexShell` (wiring, panels by id, control-port commands), `ShellPanels` (dock, terminals, files, git, browser, scripts, settings, themes, saved layouts), `ShellGestures` (Gestures studio, bindings, actions, recording, starter plugins), `ShellAgents` (agents, threads, thread, new thread, terminal links), `ShellVault` (vault panel, secret broker), `ShellVoice` (voice pipeline, shell commands, VOICE panel, HUD), `ShellLayout` (fixed / float carousel), `Services` (system deep links, settings, MQH client, git, files) |
| `../Plugins/Android/MetaCodexVault.java` | the Keystore bridge (AES-256-GCM, StrongBox or TEE) |

## Panels

| Panel | Does |
|---|---|
| Dock (two rows of 8) | new terminal, terminals, files, git, agents, threads, web, scripts / gestures, vault, TALK (push-to-talk), setup, keyboard, select tool, recenter, voice |
| Terminal frames | every terminal in a frame: drag by the header, `⇗` open the link the terminal printed (sign-in flows), `⎘` paste the clipboard, `⌨` keyboard, `+` new, `✕` close, tap to focus |
| Terminals | list of terminal instances: new, focus, close |
| Files | the Linux userland's folders (`/root`, `/mnt/quest`, `/`): open folders, cd / view / edit / paste path |
| Git | repositories under `/root`: branch, ahead/behind, upstream, changes, branches, history as metro lines; fetch, pull (ff-only), commit, checkout |
| Browser | URL field (system keyboard) opens the Horizon OS browser (`systemux://browser`), bookmarks, local servers on 127.0.0.1, shortcuts to Horizon settings and Files |
| Scripts | Lua scripts and plugins: status, enable/disable, reload, example, delete, log; gesture monitor |
| Gestures | studio: GESTURES (all gestures with source, live state, bindings, TEST, source, delete pose), LIVE (finger curls of both hands, palm/thumb direction, velocity, active gestures, recent events, Lua detector cost), BIND (gesture → action, no code), RECORD (3-2-1, the hand's pose becomes a named gesture in plugin `my-poses`; new plugin from a template), PLUGINS (enable/disable/reload/delete/folder) |
| Agents | MQH's providers with sign-in state, version, account; SIGN IN runs the provider's own login in a new terminal; INSTALL / UPDATE through MQH; enable/disable; NEW THREAD; OPEN LINK |
| Threads | MQH projects (+ PROJECT from a Linux folder) and their threads with status (running, error, needs you); new, open, stop |
| Thread | the timeline (you, agent, commands, tools, file edits, errors; newest at the bottom, ▲▼ to scroll), composer (system keyboard) + SEND, approvals (ALLOW ONCE / SESSION / DENY), STOP, jumps to the workspace's FILES, GIT and a TERMINAL there |
| New thread | provider, model (from MQH's catalogue for that provider), permissions (EDITS / AUTO / FULL), workspace (project checkout or a new git worktree), first message, START |
| Vault | secrets (names only), where the key lives (StrongBox / TEE), policy (ASK / ONCE / ALLOW / DENY), new value (secure keyboard), → MQH ENV, re-arm, delete, access log; approval dialogs for requests from Linux (`docs/secrets.md`) |
| Voice | the local AI service (state, RAM), models (install with progress, loaded or not), TALK, TYPE… (a command as text), SAY… (text to speech), spoken replies, LLM fallback, auto stop, language, threads, the command list (shell + plugins), the last requests with timings (`docs/voice.md`) |
| Voice HUD | above the dock while a voice request runs: listening level, transcript, result; STOP |
| Settings | GESTURES (click by pinch/poke/both, move panels by header pinch or closed hand, ray mode, pinch threshold, clap, plugin gestures on/off, select tool), DISPLAY (layout fixed/float, distance, carousel radius, scale, bending, save / load named layouts, recenter), LOOK (theme presets, accent, glow, opacity, motion, scanlines, grid, sounds), DAEMON (MQH URL and token, sync, status, plugins), ABOUT |

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

## Layouts

- **Fixed**: panels are world-locked where you leave them; new panels open where you look, in the
  nearest free gap (within a head turn, ±110°); when none is left they cascade in front, each a little
  closer, higher and to the side. Named layouts (Settings > DISPLAY) store the open panels relative to the head and put
  them back around wherever the user is.
- **Float**: panels sit on an invisible cylinder around you that follows you horizontally (x/z), never
  your head's pitch or roll. Close your hand in empty space and swipe to spin it (inertia, no snapping).
  Panels bend to the cylinder; terminals stay flat. The dock stays low in front.
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
