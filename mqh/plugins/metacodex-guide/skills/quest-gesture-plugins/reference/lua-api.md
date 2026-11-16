# Lua API (plugins and scripts)

Lua 5.2 (MoonSharp) in a sandbox: `string`, `table`, `math`, `coroutine`, `os.time/clock/date` are
there; `io`, `os.execute`, `load`, `require`, file access are not. Each plugin has its own state.
Every call is limited to 200 000 instructions; going over stops the plugin (error in the panels).
This page: the gesture and hand calls. The rest: skill `quest-scripting`.

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

## Everything else

The rest of the API (`ui.*`, `menu.*`, `terminal.*`, `browser.open`, `settings.*`, `store.*`, `json.*`, `app.*`,
`voice.*`, `log`, `plugin.*`) is the same for every plugin and script: its one home is the skill `quest-scripting`
("App API"); windows, menu tiles and time in detail: `quest-plugins`. The calls a gesture reaction uses most:
`ui.toast(text, kind)`, `ui.open(id)` / `ui.close(id)` / `ui.toggle(id)` (`threads`, `terminals`, `files`, `git`,
`web`...), `ui.modal(title, text, {"YES", "NO"}, function(i, label) ... end)` (i from 1), `app.action("verb:arg")`,
`voice.listen()`, `store.get` / `store.set`.

A gesture that starts listening: `gesture.on("palm-up", function(e) if e.hand == "left" and e.phase == "start" then voice.listen() end end)`
(or no code: GESTURES > BIND `palm-up` -> `voice:toggle`).
