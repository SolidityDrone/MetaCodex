# Lua scripts and app plugins: the headset grows while it runs

The app's C# is compiled ahead of time and cannot load code at run time. The headset grows through Lua and data
instead: scripts, gesture plugins and app plugins. They run inside the app (`Runtime/Scripting/ScriptHost.cs`) in a
soft sandbox: no files and no processes (`os.time`, `os.date` and `os.clock` are there; plugins use `app.clock` and
`app.date`, which the test harness can fake). Every Lua call runs under an instruction budget, and an error stops
only its own script or plugin. Nothing needs an APK rebuild.

| Kind | Where | What it can do | Skill |
|---|---|---|---|
| Script | `/mnt/quest/scripts/<name>.lua` | react to events, run app actions, add voice commands | `quest-scripting` |
| Gesture plugin | `/mnt/quest/plugins/<id>/` (`plugin.json`, pose files, `main.lua`) | new poses and gestures, bindings to actions ([ui.md](ui.md#gesture-plugins-no-rebuild)) | `quest-gesture-plugins` |
| App plugin | `/mnt/quest/plugins/<id>/` (`plugin.json`, `main.lua`) | its own windows, menu tiles, voice commands, alarms and saved state | `quest-plugins` |

The app reloads a script or a plugin within a second of a change. MQH > SCRIPTS and CONTROL > PLUGINS list them; an
agent that changes one shows the change and asks the user first (AGENTS.md).

## App plugins

An app plugin makes windows drawn with the app's own UI kit, and tiles on the DOCK and on the wrist menu's outer ring.
It loads again at every start, and its `main.lua` makes its windows and tiles again from its saved `store`.

Two examples ship with the app: **Timers** (a focus timer in the tomato style, with alarms) and **Calendar** (a month
grid with entries and reminders). Install one from CONTROL > PLUGINS (EXAMPLES TO ADD, + ADD), by voice, or with
`mcx-ui plugin example timers`. Their files are in `Runtime/Scripting/Resources/PluginExamples/<id>/`.

### The API

| Call | Does |
|---|---|
| `ui.panel{ id?, title, components, columns?, size?, open?, on = function(e) end }` | Makes or replaces one of the plugin's windows (window id `plugin-<plugin id>-<id>`, `id` default `main`). `open` defaults to false: the tile or the user opens it, and it is built at the first open. At most 4 windows a plugin. |
| `ui.open(id)`, `ui.close(id)`, `ui.toggle(id)` | Also take the plugin's own windows, by local or full id. |
| `ui.notify{ text, tone?, sound?, speak?, panel? }` | A callout by the Whisp. `tone`: info, ok, warn, error. `speak`: true speaks the text, a string speaks that string. `panel`: a tap opens that window. |
| `ui.sound(name)` | `click`, `open`, `close`, `ping`, `toast`, `alert`, `error`. |
| `menu.add{ id, label, glyph?, icon?, panel = <window> or run = function() end }`, `menu.remove(id)` | A tile on the DOCK and the wrist menu. The label is upper case, 8 characters at most. A `panel` tile toggles that window and is lit while it is open. At most 6 plugin tiles in the whole menu; the menus rebuild once, 0.3 s after a change. |
| `app.clock()`, `app.clock{ year, month, day, hour?, min?, sec? }`, `app.date([t])` | Wall-clock seconds since 1970; a local time as seconds (fields overflow like `os.time`); the local date of a time. |
| `app.at(t, fn)`, `app.cancel(id)` | Runs `fn` once at the wall-clock time `t` (a time in the past runs at the next tick). Not saved: the plugin sets it again from `store` when it loads. |
| `json.encode(value)`, `json.decode(text)` | JSON text; decode returns `nil, message` for bad text and never raises. |

Window events reach `on(e)` with `e.type`, `e.id`, `e.value` and `e.window`: `button` (an item of `buttons`),
`choice`, `input` (text sent), `toggle` (the new state), `cell` (a grid cell with an id), `open` and `close`.

### Components

Plugin windows use the same view components as the agents' views (`text`, `stats`, `chart`, `table`, `progress`,
`checklist`, `kv`, `choices`, `input`, `image` and the others), plus:

- `buttons`: 1 to 12 items `{id, label, tone?}`, 4 a row (`tone`: accent or error);
- `toggle`: one switch `{id, label, value}`;
- `grid`: 1 to 7 columns, 1 to 49 cells `{id?, text, sub?, tone?}`; a cell without an id is not tappable;
- `text` with `style = "big"`: one short centred line, for a countdown or a clock.

In an agent's view, `buttons`, `toggle` and `cell` answer the thread, as `choices` do.

A `text` with an `id` is LIVE: when only such texts change, the app sets the labels and does not rebuild the window.
Any other change rebuilds the window at most once a second.

The spec is checked before anything is built (`Runtime/Scripting/ViewSpecCheck.cs`): an error (an unknown component,
a missing id, more than 16 components or 49 cells, a duplicate id) raises a Lua error that names the component and
the field; a warning (a long title or label, more than 8 components) goes to the plugin's log as `warn ...`.

### Lifetime

- Unloading a plugin (disable, delete, an error, a reload after an edit) removes its windows, tiles, timers, voice
  commands and gestures. A window made again within 10 s of its removal (a reload) comes back at the same place,
  open if it was open.
- An error in any callback stops the plugin (log `ERROR ...`, a callout); it loads again at the next start.
- `mcx-ui plugin check <id>`: loaded, disabled or error; its windows (builds, open), tiles, voice commands, timers and
  the last 10 warnings and errors.

## Tests

- `tests/plugins/run-plugin-tests.sh <plugin dir>... | --examples`: on the PC, no editor. The real `ScriptHost` and
  the Lua interpreter with a fake app: load, the tile, every button, three hours of clock, voice lint, a restart and
  the unload.
- `tests/plugins/device-test.sh [timers|calendar]`: the same plugin on the headset.
- Editor self-test sections `scripts`, `agentscript`, `gestureplugins` and `agentplugin`.
