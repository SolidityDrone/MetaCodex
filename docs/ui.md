# MetaCodex spatial UI: style, panels, gestures, live scripts, layouts

Status: v0, 2026-11-07. Built and tested headless in the Editor (`MetaCodexUiTest`); not yet on the headset.

## Style

An original sci-fi "command console" look: dark navy glass panels with cut corners (big cuts top-left and
bottom-right), bevelled glowing frames with brighter corner brackets, a light comet running round the frame,
a faint 2.5 cm grid and scanlines, cyan accents (amber warnings, green ok, red danger), condensed technical
type (Orbitron for titles and buttons, Rajdhani for text, DejaVu Sans Mono for paths and code; all OFL /
free licences). No game assets, names or logos.

Everything is drawn by one shader, `MetaCodex/SciFiUI`: each panel, button, header, bar, line, dot and
selection frame is a quad whose shape is computed per pixel from vertex data (size, cut corners, border,
state), so a whole panel is one mesh and one draw call, crisp at any distance. Text is TextMeshPro with
runtime SDF fonts. The accent colour, scanlines, distance and scale are settings.

## Code map (`unity/Assets/MetaCodex/Runtime`)

| Folder | What |
|---|---|
| `UI/` | `UiTheme` (colours, sizes, fonts), `UiShapeMesh` (batched shapes, bending), `UiPanel` (frame, header, open animation, drag, bend), widgets (`UiButton`, `UiToggle`, `UiSegmented`, `UiSlider`, `UiTextField`, `UiList`, `UiTabs`, `UiProgress`, `UiDivider`), `UiKeyboard` (Horizon OS system keyboard), `UiInputRouter` (pointers, beams, reticles, selection), `UiToasts`, `UiModal` |
| `Hands/` | `GestureEngine` (pinch, grab, point, open, palm-up, clap), `OvrHandSource` (Meta's OVRHand/OVRSkeleton), `SimulatedHands` (tests) |
| `Scripting/` | `ScriptHost`: Lua (MoonSharp) behaviours, hot reload, sandbox, instruction budget, persisted state |
| `Shell/` | `MetaCodexShell` (wiring, panels by id, control-port commands), `ShellPanels` (dock, terminals, files, git, browser, scripts, settings), `ShellLayout` (fixed / float carousel), `Services` (system deep links, settings, MQH client, git, files) |

## Panels

| Panel | Does |
|---|---|
| Dock | new terminal, terminals, files, git, web, scripts, setup, keyboard, select tool, recenter |
| Terminal frames | every terminal in a frame: drag by the header, `⌨` keyboard, `+` new, `✕` close, tap to focus (the key bar follows the focused one) |
| Terminals | list of terminal instances: new, focus, close |
| Files | the Linux userland's folders (`/root`, `/mnt/quest`, `/`): open folders, cd / view / edit / paste path into the terminal |
| Git | repositories under `/root`: branch, ahead/behind, upstream, changed files, branches with tracking state, history as metro lines; fetch, pull (ff-only), commit (keyboard for the message), checkout |
| Browser | URL field (system keyboard) opens the Horizon OS browser (system deep link `systemux://browser`), bookmarks, local servers found on 127.0.0.1 (dev ports, MQH), shortcuts to Horizon hand settings, app settings, Files app |
| Scripts | the Lua behaviours: status, enable/disable, reload, example, delete, log; live gesture monitor |
| Settings | GESTURES (click by pinch/poke/both, move panels by pinching the header or closing the hand, ray always/pointing pose, pinch threshold, clap, select tool), DISPLAY (layout fixed/float, distance, carousel radius, scale, accent, scanlines, bending), DAEMON (MQH URL and token, sync, status, plugins enable/disable), ABOUT |

## Hands (Meta's basics; more gestures later)

- Ray: OVR's pointer pose; pinch (OVR pinch strength, hysteresis) clicks and drags.
- Poke: the index fingertip pushes buttons; near a panel it takes over from that hand's ray.
- Move a panel: pinch its header and move, or close the hand (fist) while pointing at it.
- Select tool: pinch and drag a rectangle on any panel; raises the `select` gesture (for "point and say").
- Gestures for scripts: `pinch`, `grab`, `point`, `open`, `palm-up`, `clap`, `select`.

## Live behaviours (no rebuild)

Agents (or the user) write Lua files into `/mnt/quest/scripts` (the app's shared folder seen from the
userland). The app reloads a changed file within a second; scripts persist. Skill for agents:
`mqh/plugins/bb-guide/skills/quest-scripting/SKILL.md` (bundled in MQH and copied into the rootfs at
`/root/.claude/skills`). From the userland, `mcx-ui gesture clap` simulates a gesture and `mcx-ui script list`
shows errors, so an agent can test what it wrote. "Save as prefab" on a headset means config: Unity cannot
create prefabs at runtime, so behaviours are files and settings are JSON (also mirrored to MQH as the UI
preference `quest.settings`, which agents can edit through the MQH API).

## Layouts

- **Fixed**: panels are world-locked where you leave them; new panels open where you look, beside the ones
  already there.
- **Float**: panels sit on an invisible cylinder around you, like a circular screen. The cylinder follows
  you horizontally as you walk (x/z, smoothed), never your head's pitch or roll; panels keep their height.
  Close your hand in empty space and swipe sideways to spin it; it keeps turning and slows down smoothly
  (no snapping). Panels bend to the cylinder (radius = their distance from you); terminals stay flat for now
  (their text grid is not bent yet). The dock stays low in front and turns lazily to follow your heading.
- Boundaryless: the app declares `com.oculus.feature.BOUNDARYLESS_APP` (passthrough only, Meta docs
  "Boundaryless Mode for Mixed Reality in Unity"). Follow-up from the same doc: world-locked content should be
  anchored with a spatial anchor rather than the Stage space.

## Testing without a headset

`tools/unity-batch.sh BatchCheck` = scene rebuild + terminal self-test + UI self-test
(`Bench/selftest/ui-report.txt` and `ui-*.png`). The UI test drives the router with test pointers, the gesture
engine with simulated hands, the double-clap story end to end (including a script written by a real agent:
`tools/agent-script-test.sh`), git through WSL, the files view, the float carousel, and MQH when
`tools/mqh-dev.sh start` runs. On the headset, the control port adds `ui ...`, `script ...` and `gesture ...`
commands (`tools/qterm`, or `mcx-ui` inside the userland).
