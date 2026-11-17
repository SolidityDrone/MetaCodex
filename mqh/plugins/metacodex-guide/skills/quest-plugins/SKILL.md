---
name: quest-plugins
description: Make or change a plugin of the MetaCodex headset app, with no rebuild. A plugin is a project of type plugins with a fixed kind - an app (its own window drawn like every other window, a tile on the main menu, voice commands, alarms, state that survives a restart), a gesture plugin (named hand gestures and what they do; details in quest-gesture-plugins) or a behaviour (Lua that reacts to the hands, the voice or the time, with no window). Start here for every plugin. Use when you work in a folder under /root/metacodex-projects/plugins, when the user asks for a panel or tool the app does not have ("make me a tomato timer with a button in my menu", "a habit tracker", "a calendar window"), a new gesture or reaction, or to change or remove a plugin.
---

# Quest plugins: new windows, menu tiles, gestures and reactions, live

## What a plugin is

A plugin is a **project of type plugins with a fixed kind**, one folder:

```
/root/metacodex-projects/plugins/apps/<id>/          kind app
/root/metacodex-projects/plugins/gestures/<id>/      kind gesture
/root/metacodex-projects/plugins/behaviours/<id>/    kind behaviour
  .metacodex/project.json   the marker {"type": "plugins", "kind": "app"}: read-only, NEVER change it
  plugin.json               id, name, version, kind, description
  main.lua                  the plugin (runs at every load)
  poses/<name>.json         gesture plugins: named hand poses (optional)
  AGENTS.md, CLAUDE.md      what an agent in this project reads first (made with the project)
  .git/                     the project's history: one commit per working step
```

- The folder's name is the plugin id (lower case letters, digits, `-`, `_`). The app runs the plugin from there,
  loads it again within a second of every save, and at every start. No rebuild, no restart.
- **The kind is fixed** by the folder and the marker together. The app loads a project only when both say the same
  kind. It never reads the kind from `plugin.json`: keep `"kind"` there equal to the project's kind (a different value
  is a warning in `mcx-ui plugin check`). Never edit, delete, chmod or move the marker or the folder; another kind is
  another project.
- Who makes one: the user, with THREADS > + PROJECT > PLUGIN (or CONTROL > MQH > PLUGINS > + NEW PLUGIN), picks
  APP, GESTURE or BEHAVIOUR and a name. The app writes a working scaffold of that kind (the files above), starts git,
  and adds the project to THREADS (group PLUGINS). The user then opens a thread on it and asks an agent: that is you.
  An agent can make one too, after the user's OK: `mcx-project new plugins --kind app|gesture|behaviour <name>`
  (it asks the app; never make the folders yourself: without the app's marker the folder does not load).
- **When you work in a plugin project** (your folder has `.metacodex/project.json` with `"type": "plugins"`), write
  only in that folder. Read its `AGENTS.md`, `plugin.json` and `main.lua` first.
- Older plugins may still be folders in `/mnt/quest/plugins/<id>/` (no marker, no project); they load as before (the
  V shortcut's `core-poses` is one). Never make new plugins there. When a project and an older folder have the same
  id, the project wins and the log says why.

Lua runs in a sandbox (no `io`, `require`, `load`, network; `os` has only `time`, `clock`, `date`) and talks to the app
only through its API. The app draws windows from your component list with its own UI kit, so they look like the app.

| Kind | What it adds | Read |
|---|---|---|
| `app` | a window, a tile on the main menu, voice commands, alarms, saved state | this skill, part APP |
| `gesture` | named hand gestures (pose files, Lua detectors, sequences) and what they do | this skill, then `quest-gesture-plugins` |
| `behaviour` | reactions to built-in gestures, the voice or the time; no window | this skill, part BEHAVIOUR; the API: `quest-scripting` |

Any kind may use every call (a gesture plugin may open a window), but only **app** plugins get tiles on the main menu:
a gesture or behaviour plugin's `menu.add` is never shown. A single small reaction can also be a script
(`/mnt/quest/scripts/<name>.lua`, skill `quest-scripting`) when the user does not want a project.

Other skills: `quest-scripting` (the whole Lua API, voice command rules), `quest-gesture-plugins` (gestures),
`quest-views` (the components as agent views), `metacodex-context` (the headset).
Models: the Timers and Calendar examples are complete app plugins. `mcx-ui plugin examples` says which are installed;
installed ones are projects in `/root/metacodex-projects/plugins/apps/timers` and `.../apps/calendar`: read their
`main.lua` before a bigger plugin (read only: they are the user's projects).

## The loop (every new plugin and every change)

1. **Plan, then wait for the user's OK.** Say what the user will see and do: the window and its buttons, the menu
   tile's label, the gestures (in body words), the voice commands, what is saved, and how to undo it. Write nothing
   before the OK.
2. Write complete files in the project folder (no placeholders). Raise the minor number of `version` in `plugin.json`
   (0.1.0 -> 0.2.0, 1.0.0 -> 1.1.0).
3. Check: `mcx-ui plugin check <id>` must say `loaded`, show your windows, tiles and voice commands, and
   `warnings: 0`. Fix every `ERROR` and `warn` line, save, check again (`mcx-ui plugin log <id>`: the whole log).
4. Try it:
   - a window: `mcx-ui view open plugin-<id>-main` opens it; `mcx-ui ui click view-plugin-<id>-main START` presses
     a button by its label; `mcx-ui plugin event <id> <type> <component id> [value]` sends what a tap or a sent text
     sends: `plugin event todo input add buy milk`, `plugin event todo cell item-3`, `plugin event todo toggle sound
     true`, `plugin event todo button start` (`<id>`: the plugin, for its main window, or a full window id); then
     `mcx-ui plugin check <id>` again (it must still say `loaded`);
   - a gesture: `mcx-ui gesture list` shows its name; `mcx-ui gesture <name> right start` (or `both start`; a clap:
     `mcx-ui gesture clap`) simulates it: the reaction must happen (`mcx-ui ui list` shows open panels);
   - a voice command: `mcx-ui voice decide "<a sample>"` picks it; `mcx-ui voice run "<a sample>"` runs it;
   - a restart: `mcx-ui plugin disable <id>` then `mcx-ui plugin enable <id>` runs `main.lua` again from the saved
     store, as at a start (timers set again, windows made again).
5. Commit: `git add -A && git commit -m "<what changed>"`. One commit per working step: the user can go back.
6. Tell the user in a few short lines: the tile in the menu, the gesture to make, what to say; what changed; how to
   undo (`git revert`, `mcx-ui plugin disable <id>`).

## Files

`plugin.json` (`name` is the window title and the list's name; `description` one line):
```json
{ "id": "water", "name": "Water", "version": "1.1.0", "kind": "app",
  "description": "Counts glasses of water today, reminds you every hour" }
```
`main.lua` runs once when the plugin loads (at every app start, after every save, after ENABLE). It makes the windows,
the tiles, the gestures and the voice commands, reads the saved state and sets the timers again. Everything it made
goes away when the plugin is disabled, deleted or stopped by an error, and comes back when it loads again.
`plugin.id`, `plugin.name`, `plugin.version`, `plugin.kind` and `plugin.dir` (its Linux folder) are set for it.

---

## APP: the window, `ui.panel`

```lua
local win = ui.panel{
  id = "main",                 -- default "main"; the window id is then "plugin-<plugin id>-main"
  title = "Water",             -- 40 characters at most; default: plugin.json "name"
  components = { ... },        -- 1 to 8, drawn top to bottom (more than 8: a warning; more than 16: refused)
  columns = 1,                 -- optional: 2 puts small components side by side
  size = { 0.58, 0.40 },       -- optional, metres (0.3..1.2 x 0.18..0.9); leave it out: the app sizes it
  open = false,                -- default false: made, not shown (the tile or the user opens it)
  on = function(e) ... end,    -- the user's actions
}
```
Call `ui.panel` again with the same `id` to change the window: write ONE `render()` function that builds the
whole component list from your state, and call it after every change. At most 4 windows a plugin.

Events (`e.type`, `e.id`, `e.value`, `e.window`):

| e.type | when | e.id | e.value |
|---|---|---|---|
| `button` | a `buttons` item is pressed | the item's id | its label |
| `input` | text is sent (SEND or Enter) | the component's id | the text |
| `toggle` | a `toggle` is flipped | the component's id | `true` / `false` |
| `cell` | a `grid` cell with an id is tapped | the cell's id | its text |
| `choice` | a `choices` option is picked | the component's id | the option |
| `open` / `close` | the window opens / the user closes it | "" | "" |

`ui.open(id)`, `ui.close(id)`, `ui.toggle(id)` take your window's id (`"main"`) as well as the app's panels.

### Components

| type | fields | use for |
|---|---|---|
| `text` | `text` (light markdown: `# title`, `**bold**`, `- item`), `tone?`, `id?`, `style?` | words, a status line |
| `text` + `style = "big"` | one short line, 24 characters at most | a countdown, a clock, a score |
| `buttons` | `items = { {id, label, tone?}, ... }` (1-12, 4 a row), `prompt?` | actions |
| `toggle` | `id`, `label`, `value` (bool) | an on / off option |
| `input` | `id`, `prompt?`, `placeholder?` | typed or dictated text |
| `grid` | `columns` (1-7), `cells = { {id?, text, sub?, tone?}, ... }` (1-49), `title?` | a month, a launcher, a board |
| `kv` | `items = { {key, value}, ... }` | label / value lines |
| `stats` | `items = { {label, value, delta?, tone?}, ... }` | 1-4 big numbers |
| `progress` | `items = { {label, value 0..1, tone?}, ... }` | bars |
| `checklist` | `items = { {text, done}, ... }` | a read-only list with ticks |
| `timeline` | `items = { {time, text, tone?}, ... }` | an agenda, a history |
| `table` | `columns = {...}`, `rows = { {...}, ... }` | small tables |
| `choices` | `options = {...}`, `prompt?` | pick one of a few |
| `chart` | see `quest-views` | numbers over time |

Tones: `ok` (green), `warn` (amber), `error` (red), `accent` (the app's colour: today, the selected thing), `dim`
(grey: blanks, past). Ids: letters, digits, `-`, `_`; unique in the window (a repeated id is refused).

Rules:
- Button labels: short verbs in capitals, 14 characters or fewer (style; `check` warns only above 18): `START`,
  `+5 MIN`, `SKIP`.
  At most one `accent` button in the window: the main action (START, +1, TODAY); `error` only for something that deletes.
- The app does not cut lists: your `render()` shows the first rows (about 8) and a dim "+3 more" line. Text over
  600 characters is a warning.
- Any rebuild (after a button, a toggle, a new item) empties the `input` fields: a half-typed text is lost, so keep
  the input for whole entries sent with SEND.
- Symbols: the ones in the glyph list below (`◆ ◉ ▤ ◫ ☰ ◎ ▣ ◐ ⌖ ◷ ◔ ✓ ★`) show everywhere; others may show as boxes.
  Keep cell and button text to a few words.
- A cell without `id` is not tappable (week day headers, blanks: `{ text = "", tone = "dim" }`). Give tappable things
  stable ids (`"d-2026-11-20"`, `"alarm-3"`), never list positions that shift.
- Do not draw your own frame, header or close button: the app does. One symbol in a label is fine; no emoji walls.
- Text is English. No brand names.

### The component library: read `reference/components.md` before you design a window

More components than the table above: controls `checkbox`, `radio`, `segmented`, `select` (a dropdown with search),
`slider`, `stepper`, `field`; pickers `date` (a month grid, `time = true` adds hour and minute), `time`, `color`; a
`list` of rows to pick (items with ids); layout `card`, `row`, `tabs`, `divider`, `spacer`; and `canvas` (draw rects,
circles, lines, polygons, arcs and texts in your own units; a tap tells you the shape). **Pick the control for the
value, never a wall of buttons**: a day is a `date` (not a grid of day buttons), a number a `stepper` or `slider`, a
long choice a `select`, anything spatial (a map, a floor plan, a gauge) a `canvas`; buttons only for 1-4 actions.
They send `toggle` (a checkbox, boolean), `radio`, `segmented`, `select`, `slider`, `stepper`, `date`, `time`, `color`,
`list`, `tab`, `shape` (e.value is text: `tonumber` numbers), and values you pass back update in place. Fields,
events, examples, design rules and a checklist: `reference/components.md`; the example `components`
(`mcx-ui plugin example components`) shows every one working.

### LIVE text: a number every second, for free

A `text` component **with an `id`** is LIVE. When a new spec differs from the shown one only in the `text` of LIVE
components, the app changes those labels and nothing else. Any other change rebuilds the window (at most once a
second). So for a countdown: the time in a LIVE text, everything else exactly the same between seconds (same
components, same order, same labels).

```lua
{ type = "text", id = "clock", style = "big", text = string.format("%02d:%02d", m, s) }
```

## APP: a tile in the main menu, `menu.add`

```lua
menu.add{ id = "water", label = "WATER", glyph = "◔", icon = "bell", panel = "main" }   -- tap = open / close the window
menu.add{ id = "drink", label = "+1", glyph = "+", run = function() add_glass() end }   -- tap = run
menu.remove("drink")
```
- `label`: capitals, 8 characters at most (longer is cut, with a warning).
- `glyph`: one simple symbol for the DOCK (`◆ ◉ ▤ ◫ ☰ ◎ ▣ ◐ ⌖ ◷ ◔ ✓ ★`); default `◆`.
- `icon`: the wrist menu's line icon, one of `timer`, `calendar`, `bell`, `star`, `heart`, `note`, `check`, `chart`,
  `book`, `bolt`, `music`, `plugin`; any other name (or none) shows `plugin`, a puzzle piece. The user can pick another
  icon in CONTROL > WRIST MENU; that choice wins.
- Exactly one of `panel` (one of your windows; `ui.panel` may come before or after `menu.add`) or `run`.
- `menu.add` returns true; false when the menu is full (6 plugin tiles in all; the log says so): tell the user.
- Keep a tile's `id` the same across versions: the user's menu layout remembers tiles by it (its label, glyph and
  icon may change).

## APP: time (wall clock, not session time)

| Call | Does |
|---|---|
| `app.clock()` | now, seconds since 1970 (saved times use this) |
| `app.clock{year=, month=, day=, hour=, min=, sec=}` | that local time in seconds; fields overflow: `day = 0` is the last day of the month before |
| `app.date([t])` | `{year, month, day, hour, min, sec, wday (1 = Sunday), yday}` in local time |
| `app.at(t, fn)` | `fn()` once at clock time `t` (a past `t` runs at once); returns an id for `app.cancel(id)` |
| `app.every(sec, fn)` / `app.after(sec, fn)` / `app.cancel(id)` | repeating (first call after `sec`, at least 0.05 s) / one-shot timers, session time |

Use `app.clock()` and `app.date()`, not `os.time` / `os.date`. `app.time()` restarts at 0 with the app: never save
it. Timers are not saved: set them again from the saved state when `main.lua` loads.
Days in a month: `app.date(app.clock{year=y, month=m+1, day=0}).day`.
Week day of the 1st: `app.date(app.clock{year=y, month=m, day=1}).wday`.

## APP: state that survives a restart

```lua
local S = { glasses = 0, day = "", reminders = true }
local function load()
  local t = json.decode(store.get("state") or "")   -- nil (and a message) when empty or broken: keep the defaults
  if type(t) == "table" then for k, v in pairs(t) do S[k] = v end end
end
local function save() store.set("state", json.encode(S)) end
```
- `store.get(key)` / `store.set(key, text)`: strings, per plugin. Save after every change; keep it small (cap lists,
  for example 200 entries).
- Save END times (`ends = app.clock() + 25 * 60`), not "seconds left": after a restart the countdown goes on.
- On load, handle what ended while the app was closed: tell the user once (`ui.notify`) and move on.
- `json.encode`: tables with keys 1..n become lists; an empty table becomes `[]`.

## APP: telling the user

```lua
ui.notify{ text = "Break over: back to work", tone = "ok", sound = "alert", speak = true, panel = "main" }
ui.sound("click")   -- click open close ping toast alert error
ui.toast("Saved", "ok")
```
`ui.notify` is a callout by the Whisp; a tap opens `panel` (one of your windows). `tone`: `info` (default), `ok`,
`warn`, `error`; `sound` (default `ping`, `false` for none); `speak = true` says the text (or give a shorter string).
It works at any time, also while `main.lua` loads. Use it for things that end or need the user, not for every tap.

## APP: voice commands

Each command is complete (rules: `quest-scripting`, "Voice commands"): `name` (`^[a-z][a-z0-9_]{1,47}$`, starting
with your plugin id, `-` as `_`: plugin `to-do-list` -> `to_do_list_add`; another name is refused with an error),
`description` (what it does and every option), typed `params` (`type`, `description`, `required`; `min` / `max`
/ `unit` for numbers; `enum` for closed sets with `synonyms`), `phrases`, 4 or more `samples` in English covering
every option, `run` returning a short sentence (`"error: why"` when it fails). A command without options leaves
`params` out. Spoken numbers arrive parsed ("twenty minutes" -> 20), but a `{param}` capture can still hold extra
words: check the type and fall back (`tonumber(args.minutes) or 20`) instead of failing.

```lua
voice.command{
  name = "water_add",
  description = "Count glasses of water drunk now (count: how many, default 1)",
  params = { count = { type = "integer", description = "glasses", required = false, min = 1, max = 5 } },
  phrases = { "i drank [a|one] glass of water", "add {count} glasses [of water]" },
  samples = { "I drank a glass of water", "add two glasses of water", "log one more glass",
              "count a water", "I just had some water" },
  run = function(args) add(tonumber(args.count) or 1); return "Water: " .. S.glasses .. " today" end,
}
```

## APP: a complete small plugin (model)

```lua
-- description: counts glasses of water today; a reminder every hour while it is on
local S = { glasses = 0, day = "", reminders = true, next = 0 }
local GOAL, EVERY = 8, 60 * 60
local remind_id = nil

local function today() local d = app.date(); return string.format("%04d-%02d-%02d", d.year, d.month, d.day) end
local function save() store.set("state", json.encode(S)) end
local function load()
  local t = json.decode(store.get("state") or "")
  if type(t) == "table" then for k, v in pairs(t) do S[k] = v end end
  if S.day ~= today() then S.day = today(); S.glasses = 0 end   -- a new day starts at 0
end

local render  -- defined below (used by the functions above it)

local function arm()
  if remind_id then app.cancel(remind_id); remind_id = nil end
  if not S.reminders then return end
  if S.next <= app.clock() then S.next = app.clock() + EVERY end   -- missed while closed: the next one from now
  remind_id = app.at(S.next, function()
    ui.notify{ text = "Time for a glass of water", tone = "info", sound = "ping", panel = "main" }
    S.next = app.clock() + EVERY; save(); arm()
  end)
end

local function add(n)
  if S.day ~= today() then S.day = today(); S.glasses = 0 end
  S.glasses = math.max(0, S.glasses + n); save(); render()
end

render = function()
  ui.panel{ id = "main", title = "Water", components = {
    { type = "text", id = "count", style = "big", text = S.glasses .. " / " .. GOAL },
    { type = "progress", items = { { label = "today", value = math.min(1, S.glasses / GOAL), tone = S.glasses >= GOAL and "ok" or "accent" } } },
    { type = "buttons", items = { { id = "add", label = "+1 GLASS", tone = "accent" }, { id = "undo", label = "UNDO" } } },
    { type = "toggle", id = "reminders", label = "Remind me every hour", value = S.reminders },
  }, on = function(e)
    if e.type == "button" and e.id == "add" then add(1)
    elseif e.type == "button" and e.id == "undo" then add(-1)
    elseif e.type == "toggle" then S.reminders = e.value; save(); arm(); render() end
  end }
end

load(); render(); arm()
menu.add{ id = "water", label = "WATER", glyph = "◔", icon = "bell", panel = "main" }
voice.command{
  name = "water_add",
  description = "Count glasses of water drunk now (count: how many, default 1)",
  params = { count = { type = "integer", description = "glasses", required = false, min = 1, max = 5 } },
  phrases = { "i drank [a|one] glass of water", "add {count} glasses [of water]" },
  samples = { "I drank a glass of water", "add two glasses of water", "log one more glass",
              "count a water", "I just had some water" },
  run = function(args) add(tonumber(args.count) or 1); return "Water: " .. S.glasses .. " today" end,
}
```
(`progress` changes with each glass, so a glass rebuilds the window: fine, it happens on a tap, not every second.)

## APP: patterns

- **Countdown**: save `ends`; one `app.every(1, tick)` only while something runs (cancel it when idle); `tick`
  computes `left = ends - app.clock()`, updates only the LIVE text through `render()`, and when `left <= 0` moves to the
  next state and calls `ui.notify`.
- **Alarms at a time**: save `{at, text}` items; on load and after each change, `app.cancel` the old ids and `app.at`
  every future one; past ones: notify once ("... while MetaCodex was closed") and drop them.
- **Input**: the field empties when the window is rebuilt; when you reject the text, change nothing in the spec (the
  text stays to fix) and `ui.toast` the right form.
- **Parsing typed text** ("20 stretch", "at 15:30 call mum"): `string.match` with `"(%d+)%s*min"`,
  `"(%d%d?):(%d%d)"`; when nothing matches, `ui.toast("Say for example: 20 stretch", "warn")`.
- **Month grid**: 7 header cells without id, blanks without id (tone `dim`), one cell a day with id `"d-YYYY-MM-DD"`,
  `tone = "accent"` for today, `sub = "•"` for days with entries.
- **More than one window**: `ui.panel{id="settings", ...}` and a button that calls `ui.open("settings")`.
- **A list you act on** (to-do, shopping, habits): a `grid` with `columns = 1`, one cell a row, its id from a number
  kept in the item (`"item-" .. it.n`, `S.next` counts up; never the list position); a tap on a cell (`e.type ==
  "cell"`) ticks it (`tone = "dim"` and a `✓` for done ones); an `input` adds an item; delete with a toggle "Delete mode"
  (a tap then removes) or a `CLEAR DONE` button (`error` tone), and an `UNDO` button for the last delete.

---

## GESTURE

Read `quest-gesture-plugins` next: pose files, Lua detectors (`gesture.define`), sequences, reactions, bindings and
how to test without hands. The short rules:
- Name your gestures after the plugin id (`<id>-palms-up`): gesture names are shared by every plugin.
- React only to the built-ins (`pinch grab point open palm-up clap select`) and to gestures this plugin defines: the
  examples' poses (thumbs-up, victory...) are not on every headset.
- A gesture plugin has no menu tile. It may open the app's panels (`ui.open("threads")`) or its own window.

## BEHAVIOUR

Lua that reacts, with no window and no tile: to built-in gestures (`gesture.on`, `gesture.sequence`), to the time
(`app.every`, `app.at`), to the voice (`voice.command`), and that acts through `ui.toast`, `ui.notify`, `ui.open`,
`app.action("verb:arg")`, `settings.set`, `terminal.run` (the whole API: `quest-scripting`).

```lua
-- description: a triple clap shows the threads; a stretch reminder every 45 minutes while the app runs
gesture.sequence({ "clap", "clap", "clap" }, 1.5, function(e) ui.open("threads") end)
app.every(45 * 60, function()
  ui.notify{ text = "Stand up and stretch", tone = "info", sound = "ping" }
end)
```
- Prefer reactions that cannot fire by accident: a sequence or a hold, never a single pinch or fist.
- A reaction that changes things (closes windows, runs a command) asks first: `ui.modal(title, text, {"DO IT",
  "CANCEL"}, function(i) if i == 1 then ... end end)`.
- No busy loops: one call over 200,000 instructions stops the plugin.

---

## Change an existing plugin

1. Read `plugin.json`, all of `main.lua` and `git log --oneline` first. Find the state (`store` keys and the table
   saved in them), the window ids, the tile ids, the gesture names and the voice command names.
2. Change only what was asked. When the plugin already does part of it, say so in the plan and change only what is
   missing (relabel or extend what is there; no second button for the same thing). Keep ids the same: window ids,
   tile ids (the user's menu layout keeps them), gesture names (the user's bindings use them), voice command names.
3. Keep the saved state readable: add new fields with defaults (`load()` copies saved fields over the defaults, so
   an old store just lacks the new ones); never rename or change the type of a saved field without converting the
   old value in `load()`. A saved value made from a constant you change (a phase's length saved with the phase) keeps
   the old number: recompute or convert it in `load()`.
4. Keep the voice commands in step with the window: a new button that does something new gets a command (or a new
   option of a command); a removed feature takes its command with it.
5. Raise `version` (1.0.0 -> 1.1.0), then the loop: check, try (every new or changed button, cell, toggle, input,
   and the voice commands that changed), commit, report.
- Any change of the spec other than LIVE texts rebuilds the window: fine after a tap, never every second.

## Turn off or remove

- Off and on: `mcx-ui plugin disable <id>` / `enable <id>`: its windows, tiles, gestures and voice commands go and come
  back; its files and saved state stay.
- Undo a change: `git revert <commit>` in the project; the app reloads at once.
- Remove: the user does it in CONTROL > MQH > PLUGINS > DELETE: the project folder (files and git history), its
  project in THREADS with its threads, and its saved state go. `mcx-ui plugin delete <id>` does the same: only when
  the user asked for it in so many words.

## Checklist before you report

- [ ] The user said OK to the plan; you wrote only in the plugin's project folder; the marker is untouched.
- [ ] `plugin.json`: valid JSON, `id` = the folder's name, `kind` = the project's kind, `version` raised.
- [ ] `mcx-ui plugin check <id>`: `loaded`, the windows, the tile (apps only), the voice commands, `warnings: 0`.
- [ ] Every button pressed once (`mcx-ui ui click`), every input, cell and toggle sent once (`mcx-ui plugin event`),
      every gesture simulated; `check` still says `loaded`.
- [ ] A countdown or clock is a LIVE text; nothing else in the spec changes every second.
- [ ] State in `store` as JSON; saved times are `app.clock()` times; `load()` handles a missing or older store.
- [ ] Alarms and timers are set again on load; what ended while the app was closed is told once.
- [ ] Voice: every command complete, 4+ samples, no `voice.command` warning in `check`.
- [ ] Only built-in gestures or the plugin's own; no tile on a gesture or behaviour plugin.
- [ ] No loop that can run long; no secrets; English only; no brand names.
- [ ] Committed (`git add -A && git commit`); told the user what to do, what changed and how to undo it.
