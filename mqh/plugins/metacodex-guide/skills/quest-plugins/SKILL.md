---
name: quest-plugins
description: Add a new feature to the MetaCodex headset app with no rebuild - a plugin. Start here for every kind of plugin - an app (its own window drawn like every other window, its own button on the DOCK and the wrist menu, voice commands, alarms, state that survives a restart), gestures (named hand gestures and what they do; details in quest-gesture-plugins) or a behaviour (Lua that reacts to the app). Use when the user asks for a panel or tool the app does not have ("make me a tomato timer with a button in my menu", "a calendar window where I can add reminders", "a habit tracker", "a launcher for my scripts"), a new gesture, or to change or remove a plugin.
---

# Quest plugins: new windows and menu buttons, live

A plugin is a folder on the headset: **`/mnt/quest/plugins/<id>/`** with `plugin.json` and `main.lua`.
The app loads it within a second of a change and again at every start: no rebuild, no restart.
Lua runs in a sandbox (no `io`, `require`, `load`, network) and talks to the app only through the API below.
The app draws the window from your component list with its own UI kit, so it always looks like the app.

## Kinds of plugin

Every plugin has the same folder, manifest, checks and removal (this skill). `plugin.json` `"kind"` says which kind
it is; the user finds it under that kind in CONTROL > MQH > PLUGINS (SHOW: APPS, GESTURES, BEHAVIOURS).

| Kind | What it adds | Read |
|---|---|---|
| `app` | a window, a tile on the main menu, voice commands, alarms, saved state | this skill (below) |
| `gesture` | named hand gestures (pose files, Lua detectors, sequences) and what they do | `quest-gesture-plugins` |
| `behaviour` | Lua that reacts to the app, the hands or the voice, with no window | `quest-scripting` (the API) |

One kind per plugin, by what it is mainly for; any plugin can use every call (a gesture plugin may open a window).
A single-file script in `/mnt/quest/scripts/` is lighter than a behaviour plugin when one small reaction is enough.

Other skills: `quest-scripting` (the whole Lua API, voice command rules), `quest-views` (the same components shown
by an agent), `quest-gesture-plugins` (gesture plugins).
Two complete models ship with the app: `mcx-ui plugin example timers` and `mcx-ui plugin example calendar`
install them into `/mnt/quest/plugins/`; read their `main.lua` before you write a bigger plugin.

## Procedure

1. **Plan, then wait for the user's OK.** Say: the plugin id, what the window shows, the menu button's label,
   the voice commands, what is saved, and how to remove it. Change nothing before the OK.
2. Write `/mnt/quest/plugins/<id>/plugin.json` and `main.lua` (id: lower case, digits, `-`).
3. Check it: `mcx-ui plugin check <id>`. It must say `loaded`, list your window and tile, and show
   `warnings: 0`. Fix every `ERROR` and `warn` line, save, check again.
4. Try it: `mcx-ui view open plugin-<id>-main` (opens the window), `mcx-ui ui click view-plugin-<id>-main START`
   (presses a button by its label), `mcx-ui plugin check <id>` again (the event must not stop the plugin).
5. Try the voice commands: `mcx-ui voice decide "<a sample>"`, then `mcx-ui voice run "<a sample>"`.
6. Tell the user: the button in the menu, what to say, how to undo (`mcx-ui plugin disable <id>` or delete the
   folder).

## Files

`plugin.json`:
```json
{ "id": "water", "name": "Water", "version": "1.0.0", "kind": "app",
  "description": "Counts glasses of water today, reminds you every hour" }
```
`main.lua` runs once when the plugin loads (at every app start, and after every save). It must make the
window, the tile and the voice commands, and read its saved state. Everything it made goes away when the
plugin is disabled, deleted or stopped by an error, and comes back when it loads again.

## The window: `ui.panel`

```lua
local win = ui.panel{
  id = "main",                 -- default "main"; the window id is then "plugin-<plugin id>-main"
  title = "Water",             -- 40 characters at most
  components = { ... },        -- 1 to 8 (16 is the hard limit), drawn top to bottom
  columns = 1,                 -- optional: 2 puts small components side by side
  size = { 0.58, 0.40 },       -- optional, metres; leave it out: the app sizes the window to the content
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

## Components (what keeps the window consistent with the app)

| type | fields | use for |
|---|---|---|
| `text` | `text` (light markdown: `# title`, `**bold**`, `- item`), `tone?`, `id?`, `style?` | words, a status line |
| `text` + `style = "big"` | one short line, 24 characters at most | a countdown, a clock, a score |
| `buttons` | `items = { {id, label, tone?}, ... }` (1-12, 4 a row), `prompt?` | actions |
| `toggle` | `id`, `label`, `value` (bool) | an on / off option |
| `input` | `id`, `prompt?`, `placeholder?` | typed or dictated text |
| `grid` | `columns` (1-7), `cells = { {id?, text, sub?, tone?}, ... }` (1-49: a 7 x 7 month with its week day row), `title?` | a month, a launcher, a board |
| `kv` | `items = { {key, value}, ... }` | label / value lines |
| `stats` | `items = { {label, value, delta?, tone?}, ... }` | 1-4 big numbers |
| `progress` | `items = { {label, value 0..1, tone?}, ... }` | bars |
| `checklist` | `items = { {text, done}, ... }` | a read-only list with ticks |
| `timeline` | `items = { {time, text, tone?}, ... }` | an agenda, a history |
| `table` | `columns = {...}`, `rows = { {...}, ... }` | small tables |
| `choices` | `options = {...}`, `prompt?` | pick one of a few |
| `chart` | see `quest-views` | numbers over time |

Tones: `ok` (green), `warn` (amber), `error` (red), `accent` (the app's colour: today, the selected thing),
`dim` (grey: blanks, past). Ids: letters, digits, `-`, `_`; unique in the window.

Rules:
- Button labels: short verbs in capitals, 14 characters at most: `START`, `PAUSE`, `+5 MIN`, `✕ STRETCH`.
  At most one `accent` button in the window: the main action when there is one (START, +1, TODAY); `error`
  only for something that deletes.
- Up to 8 components (16 is refused).
- Values in `kv`, `stats`, `table` may be strings or numbers. A list longer than about 8 rows: show the first ones and say "+3 more".
- A cell without `id` is not tappable (week day headers, empty days: `{ text = "", tone = "dim" }`). Give tappable things stable ids
  (`"d-2026-11-20"`, `"alarm-3"`), never list positions that shift.
- Do not draw your own frame, header or close button: the app does. No emoji walls; one symbol in a label is fine.
- Text is English. No brand names.

### LIVE text: change a number every second for free

A `text` component **with an `id`** is LIVE. When the new spec differs from the shown one only in the `text`
of LIVE components, the app changes only those labels (no rebuild). Any other change rebuilds the window, at
most once a second. So for a countdown: the time in a LIVE text, and everything else exactly the same between
seconds (same components, same order, same labels).

```lua
{ type = "text", id = "clock", style = "big", text = string.format("%02d:%02d", m, s) }
```

## A tile in the main menu: `menu.add`

```lua
menu.add{ id = "water", label = "WATER", glyph = "◔", panel = "main" }   -- tap = open / close the window
menu.add{ id = "drink", label = "+1", glyph = "+", run = function() add_glass() end }  -- tap = run
menu.remove("drink")
```
`label`: capitals, 8 characters at most. `glyph`: one simple symbol for the dock, like the app's own
(`◆ ◉ ▤ ◫ ☰ ◎ ▣ ◐ ⌖ ◷ ◔ ✓ ★`). `icon`: optional wrist icon name.
Exactly one of `panel` (one of your windows: `ui.panel` may come before or after `menu.add` in `main.lua`) or
`run`. The menu holds 6 plugin tiles in
all: `menu.add` returns false when it is full (the log says so); then tell the user.

## Time: wall clock, not session time

| Call | Does |
|---|---|
| `app.clock()` | now, seconds since 1970 (saved times use this) |
| `app.clock{year=, month=, day=, hour=, min=, sec=}` | that local time in seconds; fields overflow: `day = 0` is the last day of the month before |
| `app.date([t])` | `{year, month, day, hour, min, sec, wday (1 = Sunday), yday}` in local time |
| `app.at(t, fn)` | `fn()` once at clock time `t` (a past `t` runs at once); returns an id for `app.cancel(id)` |
| `app.every(sec, fn)` / `app.after(sec, fn)` / `app.cancel(id)` | repeating (first call after `sec`, not at once) / one-shot timers (session time) |

Lua has `string` (`format`, `match`, `gsub`...), `table` (`insert`, `remove`, `sort`, `concat`), `math`, `pairs`,
`ipairs`, `pcall`. Use `app.clock()` and `app.date()`, not `os.time` / `os.date`. `app.time()` restarts at 0 with the app: never
save it. Timers are not saved: the plugin sets them again from its state when it loads.

Days in a month: `app.date(app.clock{year=y, month=m+1, day=0}).day`.
Week day of the 1st: `app.date(app.clock{year=y, month=m, day=1}).wday`.

## State that survives a restart

```lua
local S = { glasses = 0, day = "", reminders = true }
local function today() local d = app.date(); return string.format("%04d-%02d-%02d", d.year, d.month, d.day) end
local function load()
  local t = json.decode(store.get("state") or "")   -- nil (and a message) when empty or broken: keep the defaults
  if type(t) == "table" then for k, v in pairs(t) do S[k] = v end end
end
local function save() store.set("state", json.encode(S)) end
```
- Save after every change. Keep the state small (a few KB): cap lists (for example 200 entries).
- Save END times (`ends = app.clock() + 25 * 60`), not "seconds left": after a restart the countdown goes on.
- On load, handle what ended while the app was closed: tell the user once (`ui.notify`) and move on.
- `json.encode`: tables with keys 1..n become lists; an empty table becomes `[]`.

## Telling the user: callouts and sounds

```lua
ui.notify{ text = "Break over: back to work", tone = "ok", sound = "alert", speak = true, panel = "main" }
ui.sound("click")   -- click open close ping toast alert error
```
`ui.notify` is a callout by the Whisp; a tap opens `panel`. Its `tone` is `info` (default), `ok`, `warn` or
`error`. It works at any time, also while `main.lua` loads (for "ended while MetaCodex was closed"). `speak = true` says the text with the local voice
(or give a shorter string). Use it for things that end or need the user, not for every button.

## Voice commands

Each command is a complete entry (rules in `quest-scripting`, "Voice commands"): `name` (snake_case, starts
with your plugin id), `description` (what it does and every option), typed `params` (`type`, `description`,
`required`; `min`/`max`/`unit` for numbers), `phrases`, 4 or more `samples` in English covering every option,
`run` returning a short sentence. A command without options leaves `params` out. `number` and `integer`
parameters get spoken numbers already parsed ("twenty minutes" arrives as 20), but a `{param}` capture can
still hold extra words: check the type and fall back (`tonumber(args.minutes)`, a default) instead of failing.
`mcx-ui plugin check <id>` lists them; any `voice.command ...` warning there
must be fixed.

```lua
voice.command{
  name = "water_add",
  description = "Count glasses of water drunk now (count: how many, default 1)",
  params = { count = { type = "integer", description = "glasses", required = false, min = 1, max = 5 } },
  phrases = { "i drank [a|one] glass of water", "add {count} glasses [of water]" },
  samples = { "I drank a glass of water", "add two glasses of water", "log one more glass",
              "count a water", "I just had some water" },
  run = function(args) add(args.count or 1); return "Water: " .. S.glasses .. " today" end,
}
```

## A complete small plugin (model)

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
  if S.next <= app.clock() then S.next = app.clock() + EVERY end   -- missed while closed: next one from now
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
menu.add{ id = "water", label = "WATER", glyph = "◔", panel = "main" }
voice.command{
  name = "water_add",
  description = "Count glasses of water drunk now (count: how many, default 1)",
  params = { count = { type = "integer", description = "glasses", required = false, min = 1, max = 5 } },
  phrases = { "i drank [a|one] glass of water", "add {count} glasses [of water]" },
  samples = { "I drank a glass of water", "add two glasses of water", "log one more glass",
              "count a water", "I just had some water" },
  run = function(args) add(args.count or 1); return "Water: " .. S.glasses .. " today" end,
}
```
(Here `progress` changes with each glass, so a glass rebuilds the window: fine, it happens on a tap, not every
second.)

## Patterns

- **Countdown**: save `ends`; one `app.every(1, tick)` only while something runs (cancel it when idle); `tick`
  computes `left = ends - app.clock()`, updates only the LIVE text through `render()`, and when `left <= 0`
  moves to the next state and calls `ui.notify`.
- **Alarms at a time**: save `{at, text}` items; on load and after each change, `app.cancel` the old ids and
  `app.at` every future one; past ones: notify once ("... while MetaCodex was closed") and drop them.
- **Input**: the field empties when the window is rebuilt (after a change of the state); when you reject the
  text, change nothing in the spec (the text stays to fix) and `ui.toast` the right form.
- **Parsing typed text** ("20 stretch", "in 20 minutes to stretch", "at 15:30 call mum"): `string.match` with
  patterns like `"(%d+)%s*min"`, `"(%d%d?):(%d%d)"`; when nothing matches, `ui.toast("Say for example: 20 stretch", "warn")`.
- **Month grid**: 7 header cells without id, then blanks without id (tone `dim`), then one cell a day with id
  `"d-YYYY-MM-DD"`, `tone = "accent"` for today, `sub = "•"` for days with entries.
- **More than one window**: `ui.panel{id="settings", ...}` and a button that calls `ui.open("settings")`.

## Change or remove a plugin

- Change: edit the files; the app reloads within a second, the window stays where it was. Keep the store's
  format, or convert old state in `load()` (old keys may be missing: give defaults). Raise `version` in
  `plugin.json` (1.0.0 -> 1.1.0). Change only what was asked; keep the voice commands in step with the window
  (a new button that does something new gets a voice command too).
- Any change of the spec other than LIVE texts (a label, a new row) rebuilds the window: fine after a tap or a
  phase change, never every second.
- Disable / enable: `mcx-ui plugin disable <id>` / `enable <id>` (tile, window, commands go and come back).
- Remove: `mcx-ui plugin delete <id>` (the folder goes; its store is kept until a plugin with that id comes back).

## Checklist before you report

- [ ] `mcx-ui plugin check <id>`: `loaded`, the window(s), the tile, the voice commands, `warnings: 0`.
- [ ] Every button pressed once with `mcx-ui ui click`; `check` still says `loaded`.
- [ ] A countdown or clock is a LIVE text; nothing else in the spec changes every second.
- [ ] State in `store` as JSON; saved times are `app.clock()` times; load handles a missing or old store.
- [ ] Alarms and timers are set again on load; what ended while the app was closed is told once.
- [ ] Voice: every command complete, 4+ samples, no warnings.
- [ ] No loop that can run long (200,000 instructions per call stop the plugin); no secrets; English only.
- [ ] Told the user: the menu button, what to say, how to undo.
