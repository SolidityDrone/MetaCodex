---
name: quest-plugins
description: Make or change a plugin of the MetaCodex headset app, with no rebuild. A plugin is a project of type plugins with a fixed kind - an app (its own window drawn like every other window, a tile on the main menu, voice commands, alarms, state that survives a restart, data fetched over https from the hosts it declares), a gesture plugin (named hand gestures and what they do; details in quest-gesture-plugins) or a behaviour (Lua that reacts to the hands, the voice or the time, with no window). Start here for every plugin. Use when you work in a folder under /root/metacodex-projects/plugins, when the user asks for a panel or tool the app does not have ("make me a tomato timer with a button in my menu", "a habit tracker", "a calendar window", "a panel with the weather / an exchange rate / data from a public API"), a new gesture or reaction, or to change or remove a plugin.
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

Lua runs in a sandbox (no `io`, `require`, `load`; `os` has only `time`, `clock`, `date`) and talks to the app only
through its API. The network is there for plugins through `net.get` / `net.post`, over https, to the hosts the plugin
lists in `plugin.json` `"network"` (part APP, "Data from the network"). The app draws windows from your component list
with its own UI kit, so they look like the app.

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
Models: the Timers, Calendar and Weather examples are complete app plugins (Weather: data from the network).
`mcx-ui plugin examples` says which are installed; installed ones are projects in
`/root/metacodex-projects/plugins/apps/timers`, `.../apps/calendar` and `.../apps/weather`: read their `main.lua` before a
bigger plugin (read only: they are the user's projects).

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

`plugin.json` (`name` is the window title and the list's name; `description` one line; `network` only when the plugin
fetches data: the hosts it calls, see "Data from the network"):
```json
{ "id": "water", "name": "Water", "version": "1.1.0", "kind": "app",
  "description": "Counts glasses of water today, reminds you every hour" }
```
`main.lua` runs once when the plugin loads (at every app start, after every save, after ENABLE). It makes the windows,
the tiles, the gestures and the voice commands, reads the saved state and sets the timers again. Everything it made
goes away when the plugin is disabled, deleted or stopped by an error, and comes back when it loads again.
`plugin.id`, `plugin.name`, `plugin.version`, `plugin.kind`, `plugin.dir` (its Linux folder) and `plugin.network` (the
declared hosts) are set for it.

---

## APP: performance rules (read before you write a window)

The headset draws 72 frames a second: 13.9 ms each for everything. A plugin that rebuilds its spec every second
costs a dropped frame every second (an alarm plugin did: `app.every(1, render)` with a 36-button picker, 28 ms frames).

1. **A per-second tick never calls `ui.panel`.** It changes LIVE texts with `ui.set(window, id, text)`, only when
   the text changed, and returns at once when `ui.shown(window)` is false.
2. **`ui.panel` only on real changes**: a tap, a typed text, new data, a phase or an alarm that ends, the window's
   `open` event (render it fresh there: the tick drew nothing while it was closed).
3. **No work in timers while the window is closed** except the logic that must run: an alarm that fires, a phase
   that ends on time. Prefer `app.at(t, fn)` for a moment in time over polling every second.
4. **Pick the library control for the value, never a wall of buttons**: a time is a `time`, a day a `date`, a
   number a `stepper` or `slider`, a long choice a `select` (`reference/components.md`). A grid of 36 buttons to
   pick hours and minutes is the example of what not to do.
5. One `app.every` a plugin at most, running only while something runs (cancel it when idle).

The host warns when a timer calls `ui.panel` more than 20 times a minute and only LIVE texts change:
`warn ui.panel main: called every second with only LIVE texts changing: use ui.set(...) and ui.shown(...)`. Fix it
(rule 1); `check` must show `warnings: 0`.

```lua
-- the tick of a countdown: logic first, then one label, and only while it shows
app.every(1, function()
  if app.clock() >= S.ends then finish(); render(); return end   -- the logic runs, shown or not
  if not ui.shown("main") then return end                         -- nothing shows: nothing to draw
  local t = fmt(S.ends - app.clock())
  if t ~= shown then ui.set("main", "clock", t); shown = t end    -- one LIVE text, no spec
end)
```

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
`ui.set(window, id, text)` changes one LIVE text (below) without a spec; `ui.shown(window)` is true while the window
shows.

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
- Symbols: the ones in the glyph list below (`◆ ◉ ▤ ◫ ☰ ◎ ▣ ◐ ⌖ ◷ ◔ ✓ ★`) show everywhere, and so do `·`, `…`, `°`,
  `▸` and `◀ ▶` in text and labels; others may show as boxes. Keep cell and button text to a few words.
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

### LIVE text: `ui.set` for what changes every second

A `text` component (`text`, `markdown`, `note`) **with an `id`** is LIVE, at the top level or inside a `card`, `row`
or tab. Build the window with `ui.panel` once and on real changes; per second set the LIVE texts with `ui.set`:

```lua
{ type = "text", id = "clock", style = "big", text = fmt(left) }      -- in render(): the text it has now

if ui.shown("main") then ui.set("main", "clock", fmt(left)) end       -- in the tick: that one label
```
- `ui.set(window, id, text)` (text: a string or a number) sets the label and keeps the new text in the window's spec;
  it returns false when the window or a LIVE text with that id does not exist (yet): no error. The set text stays
  through later `ui.panel` calls that still give the old text for that id; a new text in the spec wins.
- `ui.shown(window)`: true while the window shows. While it is closed nothing is drawn; when it opens it shows the
  newest texts, and its `open` event comes (render it fresh there).
- The text rules hold for `ui.set` too (a `big` text: one line, 24 characters at most; a warning otherwise).
- A `ui.panel` whose spec differs only in LIVE texts is cheap too (the app changes those labels, no rebuild), but it
  still runs your whole `render()` every time: never every second (the host warns). Any other change rebuilds the
  window (at most once a second).

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

## APP: data from the network (`net`)

Use it when the window shows data a web service has: the weather, an exchange rate, a timetable, a status page, a
feed. Not for: files over 1 MB, streams or sockets, pages to look at (`browser.open(url)` shows a page), or anything
that needs the user to sign in in a browser.

**1. Pick the service before you plan.** It must be https, answer in JSON (or short text), be free, need no key if
at all possible, and allow this use: read its terms page (who may use it, the request limits, whether it asks for a
credit). Say in the plan which host you call, why it fits (free, no key, the terms), how often the plugin calls it,
and the credit line if the licence asks for one. Try the URL first when you can (`curl -s '<url>' | head -c 600` in a
terminal) and write the reply's field names down: you parse exactly those.

**2. Declare every host** the plugin calls in `plugin.json`:
```json
{ "id": "weather", "name": "Weather", "version": "1.0.0", "kind": "app",
  "description": "Weather now and the next 3 days for a city you pick (online)",
  "network": ["api.open-meteo.com", "geocoding-api.open-meteo.com"] }
```
- Host names only: no `https://`, no path, no port (any port of a declared host is allowed). `"*.example.com"` covers
  its subdomains at any depth but not `example.com` itself (list both when you need both). `"*"` is refused.
- https only. Plain http only to `127.0.0.1` and `localhost` (this headset's own servers), and those must be declared too.
- A request to a host that is not declared fails: its callback gets `res.error` =
  `net: host not declared in plugin.json "network": <host>`, and `mcx-ui plugin check <id>` shows that warn line.
  A bad entry in `"network"` is a warn line too. The user sees the hosts in MQH > PLUGINS ("uses the network: ...").

**3. The calls** (plugins only; a single script has no network):

| Call | Does |
|---|---|
| `net.get(url, [opts], fn)` | starts a GET and returns its id at once (or `nil, why` when it is refused); `fn(res)` runs later |
| `net.post(url, body, [opts], fn)` | the same with a body: a text (sent as `text/plain`) or a table (sent as JSON, `application/json`) |
| `net.cancel(id)` | drops a request: its `fn` never runs (true when it was still out) |
| `opts` | `headers = { Accept = "application/json" }` (yours, sent as you set them; a `Content-Type` here is the body's), `timeout = 15` (seconds, 1 to 30; default 15) |
| `res.ok` | true when a reply came with status 200-299 |
| `res.status` | the HTTP status; 0 when no reply came |
| `res.body` | the reply as text ("" when none) |
| `res.json` | the reply decoded (a table) when it is JSON, else nil; JSON `null` is nil, every number a double |
| `res.error` | nil when ok; else why: `status 404`, `timeout`, `too large`, a network error, or `net: ...` (refused) |

- It never waits: `net.get` returns at once, and `fn` runs in a later frame, never inside the `net.get` call. So set
  your "loading" state first, render, and let `fn` render the result.
- Every request gets exactly one call of `fn`, a refused one too (with `res.error`), unless you cancelled it or the
  plugin unloaded: a disable, a delete, an error or a reload after a save drops every request still out, and their
  `fn` never runs. So handle every failure in `fn` (the model below does: its failure branch sets the retry timer);
  the `nil, why` that `net.get` returns for a refusal is only for a log line or a toast.
- `fn` runs under the same budget as any callback: an error or a long loop in it stops the plugin.
- Limits a plugin: 4 requests in flight, 60 started a minute, a reply of 1 MB at most (a longer one is cut:
  `res.error = "too large"`), the same 1 MB for a body you send. Over a limit the request is refused (`res.error` says
  which): keep one request in flight per thing you fetch.
- The app keeps no cookies and adds no credentials or keys of its own; a redirect is followed only to a declared host
  over https, and your `Authorization` header is not sent to another host. It sends `User-Agent: MetaCodex-plugin/1.0
  (<plugin id>)` unless you set one (some services ask for a User-Agent that says who calls).
- Build URLs with every value encoded: `local function enc(s) return (tostring(s):gsub("[^%w%-%._~]", function(c)
  return string.format("%%%02X", string.byte(c)) end)) end`; numbers with `string.format("%.4f", lat)`.

**4. Errors: keep what you had.** A failure (offline, `timeout`, `status 500`, a reply without the fields) must not
empty the window: keep the last good data, say why in a status line (`"Updated 2 h ago · no update: timeout"`), and
try again later with a timer (5 minutes), never at once in a loop. Check every field you read from `res.json`
(`type(v) == "number"`): a service can change its reply.

**5. Cache in `store`.** Save the parsed result (not the whole reply) with the `app.clock()` time it came. When
`main.lua` loads, show the cached result at once with its age ("Updated 12 min ago") and fetch only when it is older
than the refresh period. The user sees data at once after a restart, and the service gets fewer calls.

**6. Refresh with a wall-clock timer.** One `app.at(at + EVERY, fetch)` (`at` = when the result came), set again after
every reply and at load; a failure sets it to now + 5 minutes. A REFRESH button calls `fetch()` now (do nothing while
one is out; for data that changes slowly it may also wait a minute after the last try). Show the age as a LIVE text (a `text` with an `id`) set once a minute with
`app.every(60, function() if ui.shown("main") then ui.set("main", "status", status()) end end)`: no rebuild, and
nothing while the window is closed (its `open` event renders it fresh). Call as seldom as the data changes: the weather every 30 minutes, a rate every hour.
More than one call a minute on average is too often (the PC test fails it).

**7. Credits.** When the data's licence asks for a credit (CC BY: "a credit and a link next to the data"), put a dim
`text` line in the window (`"Weather data by Open-Meteo.com (CC BY 4.0)"`) and a `SOURCE` button that calls
`browser.open("https://...")`. No logos. The credit line is the one place the user sees the service's name: the
`plugin.json` description, the title and the other texts stay generic ("online", "a public API"); a code comment may
name it. A service whose terms ask for no credit gets no credit line.

**8. Keys.** Never put an API key, a token or a password in a plugin's files: they are in git, and agents read them.
Prefer a service that needs no key. When one needs a key, the user types it: an `input` (`prompt = "Your key"`), the
plugin keeps it in `store` and sends it in a header (`headers = { Authorization = "Bearer " .. key }`), and the plan
says so. Tell the user that `store` is not a vault: it is a plain file of the app's that any agent with access to the
app's files can read; a key that must stay secret belongs in VAULT, which plugins cannot read.

**9. Try it.** `mcx-ui plugin check <id>` shows `network: <hosts> (n in flight, m in the last minute)` and every
warn line; `mcx-ui plugin log <id>` shows the first reply (`net: GET https://host/path -> 200 (2.1 KB)`, never the
query) and each failure; `mcx-ui plugin event <id> button refresh` presses REFRESH. Try the failure path too: a city
that does not exist, the network off.

### The model: Weather, condensed

The shipped Weather example (`.../apps/weather`: 8 preset cities, a typed city through the place search, 3 days,
units, voice) has this shape; here it is cut to one city:

```lua
-- the weather now in Paris; updated every 30 minutes
-- service: Open-Meteo (free, no key, data CC BY 4.0); plugin.json: "network": ["api.open-meteo.com"]
local EVERY, RETRY = 30 * 60, 5 * 60
local URL = "https://api.open-meteo.com/v1/forecast?latitude=48.8534&longitude=2.3488" ..
            "&current=temperature_2m,weather_code,wind_speed_10m&timezone=auto"
local SKY = { [0] = "Clear sky", [1] = "Mainly clear", [2] = "Partly cloudy", [3] = "Overcast", [45] = "Fog",
              [61] = "Slight rain", [63] = "Rain", [65] = "Heavy rain", [71] = "Slight snow", [80] = "Rain showers",
              [95] = "Thunderstorm" }
local S = { wx = nil, at = 0 }     -- the last good result and when it came (saved)
local reqId, problem, timer = nil, nil, nil
local render

local function save() store.set("state", json.encode(S)) end
local function load()
  local t = json.decode(store.get("state") or "")
  if type(t) == "table" and type(t.wx) == "table" and type(t.wx.temp) == "number" and type(t.at) == "number" then
    S.wx, S.at = { temp = t.wx.temp, code = t.wx.code, wind = t.wx.wind }, t.at
  end
end
local function ago()
  local s = math.max(0, app.clock() - S.at)
  if s < 90 then return "just now" elseif s < 3600 then return math.floor(s / 60 + 0.5) .. " min ago" end
  return math.floor(s / 3600 + 0.5) .. " h ago"
end

local fetch
local function arm()                                      -- the next update: 30 min after the result, 5 min after a failure
  if timer then app.cancel(timer) end
  timer = app.at(problem and app.clock() + RETRY or S.at + EVERY, function() timer = nil; fetch() end)
end
fetch = function()
  if reqId then return false end                         -- one at a time
  reqId = net.get(URL, { timeout = 15 }, function(res)
    reqId = nil
    local c = res.ok and type(res.json) == "table" and res.json.current or nil
    if type(c) == "table" and type(c.temperature_2m) == "number" then
      S.wx = { temp = c.temperature_2m, code = c.weather_code, wind = c.wind_speed_10m }
      S.at, problem = app.clock(), nil
      save()
    else
      problem = res.error or "the reply had no weather in it"    -- keep the old data
    end
    arm(); render()
  end)
  render()
  return true
end

local function status()
  local w = S.wx
  return not w and (reqId and "Getting the weather…" or "No weather yet") or
         ("Updated " .. ago() .. (reqId and " · updating…" or "") .. (problem and (" · no update: " .. problem) or ""))
end

render = function()
  local w = S.wx
  ui.panel{ id = "main", title = "Weather", components = {
    { type = "text", id = "now", style = "big", text = w and string.format("%d°C", math.floor(w.temp + 0.5)) or "--" },
    { type = "text", id = "sky", text = w and ((SKY[w.code] or "-") .. " · wind " .. math.floor((w.wind or 0) + 0.5) .. " km/h · Paris") or "Paris" },
    { type = "buttons", items = { { id = "refresh", label = "REFRESH", tone = "accent" }, { id = "source", label = "SOURCE" } } },
    { type = "text", id = "status", text = status() },
    { type = "text", tone = "dim", text = "Weather data by Open-Meteo.com (CC BY 4.0)" },
  }, on = function(e)
    if e.type == "button" and e.id == "refresh" then if not fetch() then ui.toast("Already updating") end
    elseif e.type == "button" and e.id == "source" then browser.open("https://open-meteo.com/")
    elseif e.type == "open" then render() end
  end }
end

menu.add{ id = "weather", label = "WEATHER", glyph = "◐", icon = "bolt", panel = "main" }
load(); render()
if not S.wx or app.clock() - S.at >= EVERY then fetch() else arm() end   -- cached and fresh: show it, wait
app.every(60, function()                                                  -- the age line: one LIVE text,
  if ui.shown("main") then ui.set("main", "status", status()) end         -- only while the window shows
end)
```

## APP: patterns

- **Countdown**: save `ends`; one `app.every(1, tick)` only while something runs (cancel it when idle); `tick`
  computes `left = ends - app.clock()`; when `left <= 0` it moves to the next state, calls `ui.notify` and `render()`;
  else it returns when `ui.shown("main")` is false and sets the LIVE text with `ui.set("main", "clock", ...)` when the
  text changed (the performance rules' example above). Never `render()` every second.
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
- Any change of the spec other than LIVE texts rebuilds the window: fine after a tap, never every second. A plugin
  whose tick calls `render()` / `ui.panel` every second (the host's `warn ui.panel ...: called every second` line):
  change the tick to `ui.set` + `ui.shown` (performance rules), keep `render()` for real changes and the `open` event.

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
- [ ] Performance: no timer calls `ui.panel` / `render()` every second; a countdown or clock is a LIVE text set with
      `ui.set` only when it changed; the tick returns at once when `ui.shown` is false (only logic that must run,
      like an alarm firing, runs while closed); `render()` runs on real changes and on the `open` event.
- [ ] Values are picked with the library's controls (`time`, `date`, `select`, `stepper`), not grids of buttons.
- [ ] `check` has no `warn ui.panel ...: called every second` line.
- [ ] State in `store` as JSON; saved times are `app.clock()` times; `load()` handles a missing or older store.
- [ ] Alarms and timers are set again on load; what ended while the app was closed is told once.
- [ ] Voice: every command complete, 4+ samples, no `voice.command` warning in `check`.
- [ ] Only built-in gestures or the plugin's own; no tile on a gesture or behaviour plugin.
- [ ] Network: every host in `plugin.json` `"network"`, https, one request in flight per thing fetched, the result
      cached in `store` with its time, a wall-clock refresh no more than needed, failures keep the old data and say
      why, every `res.json` field checked, the credit and SOURCE when the licence asks; no key in any file.
- [ ] No loop that can run long; no secrets; English only; no brand names.
- [ ] Committed (`git add -A && git commit`); told the user what to do, what changed and how to undo it.
