# Lua API (plugins and scripts)

Lua 5.2 (MoonSharp) in a sandbox: `string`, `table`, `math`, `coroutine`, `os.time/clock/date` are
there; `io`, `os.execute`, `load`, `require`, file access are not. Each plugin has its own state.
Every call is limited to 200 000 instructions; going over stops the plugin (error in the panels).

## gesture

| Call | Does |
|---|---|
| `gesture.on(name, fn(e))` | runs for every event of `name` (`"*"` = all). `e = {name, hand, phase, time, x, y, z, detail}`; phase `start` / `end` / `tap`; hand `left` / `right` / `both` |
| `gesture.sequence({n1, n2, ...}, window, fn(e))` | the names' start/tap events in order within `window` seconds |
| `gesture.define(name, opts, detect)` | a detector. `opts = {hand = "any"/"left"/"right"/"both", hold = 0.08, cooldown = 0.3, description = "..."}`; `detect(h)` (or `detect(left, right)` for `both`) returns true while the gesture happens |
| `gesture.pose(name, spec)` | a pose from Lua; `spec` has the pose-file fields (`fingers`, `tolerance`, `palmFacing`, `thumbPointing`, `pinch = {min, max}`, `hold`, `cooldown`, `hand`, `description`) |
| `gesture.emit(name, hand, phase, detail)` | raises an event yourself (state machines, timers); other plugins and bindings see it |
| `gesture.list()` / `gesture.names()` | every gesture name the app knows |
| `gesture.active(name [, hand])` | true while that gesture is active (built-in or custom) |

## hands, head

| Call | Returns |
|---|---|
| `hands.get("left"/"right")` (alias `hands.state`) | a hand table (see hand-model.md); `tracked = false` when not seen |
| `head.pose()` | `{x, y, z, fx, fy, fz}`: eye position and look direction, world |

## ui

| Call | Does |
|---|---|
| `ui.toast(text [, kind])` | kind `info` `ok` `warn` `error` |
| `ui.open(id)` `ui.close(id)` `ui.toggle(id)` | panels: `dock terminals files git web browser scripts gestures plugins agents threads thread thread-new vault settings` (`web` = the browser window, `browser` = its LINKS page); returns false for unknown ids |
| `ui.panels()` | list of panel ids |
| `ui.modal(title, text, {"A", "B"}, fn(index, label))` | a dialog in front of the user; index starts at 1 |
| `ui.select(true/false)` | the select tool (rectangle selection) on/off |

## terminal, browser, keyboard

| Call | Does |
|---|---|
| `terminal.run(cmd)` | types `cmd` + Enter into the focused terminal |
| `terminal.send(text)` | types text as is |
| `terminal.new()` | opens a new terminal |
| `browser.open(url)` | opens the Horizon OS browser at `url` (local servers on 127.0.0.1 work) |
| `keyboard.open()` | the system keyboard for the focused terminal |

## settings, store, app, log

| Call | Does |
|---|---|
| `settings.get(key)` / `settings.set(key, value)` | the shell settings (`layout`, `accent`, `uiScale`, `panelOpacity`, `motion`, `gestures.clapDistance`, `gestures.pinchOn`, ...) |
| `store.get(key)` / `store.set(key, value)` | strings that survive restarts, per plugin |
| `app.time()` | seconds since the app started |
| `app.after(seconds, fn)` / `app.every(seconds, fn)` | timers; return an id for `app.cancel(id)` |
| `app.action("verb:arg")` | same actions as bindings: `open:` `close:` `toggle:<panel>`, `toast:`, `run:`, `type:`, `url:`, `layout:`, `spin:`, `select[:on|off]`, `keyboard`, `terminal:new`, `recenter`, `gesture:<name>`, `lua:<code>`; returns true or an error string |
| `log(...)` / `print(...)` | a line in the plugin's log (`mcx-ui plugin log <id>`, CONTROL > PLUGINS) |
| `plugin.id` `plugin.name` `plugin.version` `plugin.dir` | (plugins only) |

## voice

A plugin can be driven by voice as well as by hands: `voice.command{...}` registers a command (owned by the
plugin; removed when it is disabled, reloaded or deleted). Full format and phrase templates: skill
`quest-scripting`, "Voice commands".

| Call | Does |
|---|---|
| `voice.command{name=, description=, params=, phrases=, synonyms=, examples=, llm=, confirm=, run=function(args) ... end}` | a command: spoken phrases run it at once, other phrasings reach it through the local LLM (it reads `description` and `params`) |
| `voice.remove(name)` | removes one of this plugin's commands |
| `voice.say(text)` | speaks text with the local voice (text-to-speech on the headset) |
| `voice.run(text)` | runs text as if the user said it |
| `voice.listen()` | starts listening (like TALK on the dock); bind it to a gesture for hands-free voice |
| `voice.list()` | names of all voice commands |

A gesture that starts listening: `gesture.on("palm-up", function(e) if e.hand == "left" and e.phase == "start" then voice.listen() end end)`
(or no code: Gestures > BIND `palm-up` -> `voice:toggle`).
