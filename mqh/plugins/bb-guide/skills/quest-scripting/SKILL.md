---
name: quest-scripting
description: Change how the MetaCodex headset app behaves while it runs - gestures (double clap, pinch, fist, palm up...), panels, toasts, terminal actions, layout and settings - by writing a Lua script, with no rebuild. Use when the user asks things like "make it so if I double clap X happens", "when I show my palm open the files", "move panels with a fist instead of a pinch", or wants a new hands-free shortcut.
---

# Quest scripting: live behaviours in the MetaCodex app

The app (Unity, on the headset) runs Lua scripts from **`/mnt/quest/scripts/*.lua`**. Write or edit a
file there and the app loads it within a second: no rebuild, no restart. Files persist across restarts.
Each script is sandboxed (no `io`, `os`, `require`, `load`) and talks to the app only through the API below.

## Procedure

1. Pick a short file name for the behaviour: `/mnt/quest/scripts/double-clap-files.lua`.
2. First line: `-- description: <what it does>` (shown in the Scripts panel).
3. Use `gesture.sequence` for multi-step gestures (double clap), `gesture.on` for single ones.
4. Test without moving: `mcx-ui gesture clap` twice (simulates recognised claps), then `mcx-ui script list`
   (shows `error=` if the script failed) and `mcx-ui ui list` (which panels are open).
5. Tell the user what to do ("clap twice within 0.8 s") and how to undo it (delete the file, or the Scripts panel).

## Example: "if I double clap, open the files"

```lua
-- description: double clap opens the Files panel
gesture.sequence({"clap", "clap"}, 0.8, function(e)
  ui.toast("Files")
  ui.open("files")
end)
```

## Gestures

Names: `pinch`, `grab` (closed hand), `point` (index out), `open` (open palm), `palm-up`, `clap` (two hands),
`select` (an area marked with the select tool; `e.detail` = "panel x y w h" in metres).
Handler argument `e`: `name`, `hand` (`left` | `right` | `both`), `phase` (`start` | `end` | `tap`), `time`
(seconds), `x y z` (world position), `detail`. One-hand gestures fire `start` and `end`; check
`e.phase == "start"`. `clap` and `select` fire `tap`. Sequences count `start`/`tap` events only.

| Call | Does |
|---|---|
| `gesture.on(name, fn)` | `fn(e)` on every event of that gesture (`"*"` = all) |
| `gesture.sequence({names...}, seconds, fn)` | `fn(e)` when the gestures happen in order within `seconds` |
| `gesture.names()` | the list above |
| `hands.state("left"\|"right")` | `{tracked, pinch (0..1), pinching, grabbing, pointing, open, palmUp, x, y, z}` |

## App API

| Call | Does |
|---|---|
| `ui.toast(text[, "info"\|"ok"\|"warn"\|"error"])` | a short message in front of the user |
| `ui.open(id)` `ui.close(id)` `ui.toggle(id)` | panels: `dock`, `terminals`, `files`, `git`, `browser`, `scripts`, `settings`, `term-1`... |
| `ui.panels()` | ids of all panels |
| `ui.modal(title, text, {"YES","NO"}, fn)` | a dialog; `fn(index, label)` with the answer |
| `ui.select(true\|false)` | the select tool (pinch-drag a rectangle on a panel) |
| `terminal.send(text)` / `terminal.run(cmd)` / `terminal.new()` | type into the active terminal (`run` adds Enter) / open a new one |
| `browser.open(url)` | the Horizon OS browser at a URL (also `http://127.0.0.1:<port>` for local web apps) |
| `keyboard.open()` | the system keyboard for the terminal |
| `settings.get(key)` / `settings.set(key, value)` | app settings (strings in and out), saved and synced to MQH |
| `store.get(key)` / `store.set(key, value)` | this script's own persistent strings |
| `app.after(sec, fn)` / `app.every(sec, fn)` / `app.cancel(id)` / `app.time()` | timers |
| `log(...)` / `print(...)` | lines in the script log (`mcx-ui script log <name>`) |

Settings keys: `gestures.clickMode` (0 pinch, 1 poke, 2 both), `gestures.grabMode` (0 pinch the header,
1 closed hand, 2 both), `gestures.pointMode` (0 ray always, 1 only in the pointing pose),
`gestures.pinchOn` (0.6-0.98), `gestures.clapEnabled`, `gestures.clapDistance` (m), `gestures.rayVisible`,
`layout` (`fixed` | `float`), `carouselRadius`, `panelDistance`, `uiScale`, `accent`
(`cyan` `amber` `green` `violet` `red`), `scanlines`, `bendPanels`.
Example: "move panels by closing my hand instead of pinching" = `settings.set("gestures.grabMode", "1")`
(a one-line script, or the Settings panel).

## Rules

- One behaviour per file; keep callbacks short. A callback that runs more than 200,000 Lua instructions is
  stopped and the script disabled (shown as ERROR in the Scripts panel): no busy loops, use `app.every`.
- Never put secrets in scripts. Scripts can type into the terminal, so they run with the user's rights.
- Prefer gestures that cannot fire by accident: sequences (double clap) or a hold, not a single pinch.
