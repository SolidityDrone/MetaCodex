# Window components: the catalogue and how to design with them

Read this before you design any plugin window (and any agent view: `quest-views` uses the same components).
The app draws every component with its own UI kit, so a window looks like the rest of the app. Your job is the
same as a front-end developer's: pick the right control for each piece of data, group and order them, and keep
the window calm.

**The one rule: a control for each kind of value, never a wall of buttons.**

| The user must... | Use | Never |
|---|---|---|
| pick a day (and a time) | `date` (`time = true` for both) | a `grid` of 30 day buttons |
| pick a time of day | `time` | 24 hour buttons |
| set a number (minutes, a count, a level) | `stepper` (small steps) or `slider` (a range) | `+1` / `-1` / `+5` buttons |
| switch something on or off | `toggle` (a setting) or `checkbox` (an item, a form) | an ON / OFF pair of buttons |
| choose one of 2 to 5 | `segmented` (a mode, a view) or `radio` (with longer labels) | a row of buttons |
| choose one of many (6 to 200) | `select` (a dropdown with search) | a grid of options |
| choose a colour | `color` | coloured buttons |
| type a name or a note | `field` (a form) or `input` (a send box) | |
| pick one row of a list (to-dos, files, people) | `list` with ids | a column of buttons |
| see or tap anything spatial: a map, a floor plan, a seat plan, a gauge, a diagram you draw, a board | `canvas` | buttons laid out as a map |
| show numbers | `stats`, `progress`, `chart`, `kv` | text with numbers in it |
| group related controls | `card` | blank text lines as separators |
| show several pages of one window | `tabs` | 4 windows, or a `buttons` row that rebuilds everything |
| put two things side by side | `row` | `columns = 2` for a pair |
| act (start, add, delete, send) | `buttons`: 1 to 4 verbs | buttons that hold a value |

## Contents

1. Design: how to lay out a window
2. Events: what `on(e)` gets
3. The catalogue: layout, controls, pickers, lists, canvas, display, dialogs
4. Updating a window live
5. Recipes: a form, a settings page, a map or floor plan, a picker window
6. Limits
7. Checklist before you ship a window

---

## 1. Design: how to lay out a window

Brief, as to a junior front-end developer:

- **Start from the task, not the data.** What does the user come to this window to do? That is the primary action:
  exactly one `buttons` item with `tone = "accent"` (ADD, START, SAVE). Everything else is secondary.
- **Hierarchy, top to bottom.** 1) the current state in one glance (a `text` with `style = "big"`, `stats`, or the
  canvas); 2) the inputs; 3) the primary action; 4) secondary actions. The eye reads down; the last thing it sees
  is what to do next.
- **Group with cards.** Related controls go in a `card` with a short title ("Alarm", "Sound", "When"). Two to four
  cards per window. A card with one control in it is not a group: leave it out.
- **One control per value.** Each value the user can change is ONE component that shows the value and changes it
  (a stepper shows 25 min and has - and +). Never a value in a text plus buttons to change it.
- **Pages, not long windows.** More than about 8 components: split them with `tabs` (2 to 6 short labels: "Today",
  "Week", "Settings"). Switching a tab is instant and never rebuilds the window.
- **Side by side only when they belong together** (`row`): a stepper and its unit, a time and a colour, a field and
  its ADD button. Give the wide one more `weight`. Never more than 3 things in a row in a 0.6 m window.
- **Consistent tones.** `accent` for the chosen thing and the one primary action; `ok` / `warn` / `error` only for
  status (done, late, failed); `error` for the button that deletes; `dim` for the past or the empty. Same meaning
  in every component of the window.
- **Short text.** Labels of one or two words ("Work", "Break", "Sound"). Button labels are verbs in capitals.
  Explain with a dim `text` line at most, never a paragraph.
- **Spatial things are drawn.** If the data has a place (rooms, districts, seats, a board, a dial, a timeline you
  want to see), use a `canvas`: the user taps the thing itself. A list of 40 place names as buttons is wrong.
- **Feedback in place.** A tap changes the control at once; your `on(e)` saves the value and calls `render()`
  with the new value: the app updates the built controls in place (no rebuild, no flash; section 4).
- **Empty states.** A list with nothing in it shows a dim line that says what to do ("No reminders: pick a day and
  ADD").

## 2. Events: what `on(e)` gets

`e.type`, `e.id`, `e.value` (`e.window`: your window's id). `e.value` is a **string** for every type except
`toggle` (a boolean). Numbers come as text: `tonumber(e.value)`.

| component | e.type | e.id | e.value |
|---|---|---|---|
| `buttons` | `button` | the item's id | its label |
| `toggle`, `checkbox` | `toggle` | the component's id | `true` / `false` (boolean) |
| `radio` | `radio` | the component's id | the option's value |
| `segmented` | `segmented` | the component's id | the option's value |
| `select` | `select` | the component's id | the option's value |
| `slider` | `slider` | the component's id | the number as text ("40"), when the thumb is let go |
| `stepper` | `stepper` | the component's id | the number as text ("25"), each tap |
| `date` | `date` | the component's id | `"YYYY-MM-DD"`, with `time = true` `"YYYY-MM-DDTHH:MM"` |
| `time` | `time` | the component's id | `"HH:MM"` (24 h) |
| `color` | `color` | the component's id | `"#RRGGBB"` |
| `field`, `input` | `input` | the component's id | the text (when the keyboard closes / SEND) |
| `list` (with ids) | `list` | the row's id | the row's text |
| `tabs` | `tab` | the component's id | the tab's id |
| `canvas` | `shape` | the tapped shape's id (`""`: no shape) | the point in canvas units, `"x,y"` |
| `grid` | `cell` | the cell's id | its text |
| `choices` | `choice` | the component's id | the option |
| the window | `open` / `close` | `""` | `""` |

```lua
on = function(e)
  if e.type == "stepper" and e.id == "work" then S.work = tonumber(e.value) or S.work
  elseif e.type == "toggle" and e.id == "sound" then S.sound = e.value          -- a boolean
  elseif e.type == "date" then S.when = e.value                                  -- "2026-11-20T09:30"
  elseif e.type == "shape" and e.id ~= "" then S.room = e.id
  end
  save(); render()
end
```
A canvas point: `local x, y = e.value:match("^(.-),(.-)$"); x, y = tonumber(x), tonumber(y)`.

Every component also takes `title` (a small bold line above it) and, except the canvas, `height` (metres) to
force its height. Ids: letters, digits, `-`, `_`, unique in the window (inside cards, rows and tabs too).

---

## 3. The catalogue

### Layout

**`card`**: a titled box that groups components. `title?`, `tone?` (a bar by the title), `components` (1-12).
```lua
{ type = "card", title = "Alarm", components = {
  { type = "time", id = "at", label = "At", value = S.at },
  { type = "toggle", id = "on", label = "Enabled", value = S.on },
} }
```

**`row`**: components side by side. `components` (1-6), each may have `weight` (share of the width, default 1);
`align` = `"top"` (default), `"center"`, `"bottom"`.
```lua
{ type = "row", align = "center", components = {
  { type = "field", id = "text", label = "Note", weight = 3 },
  { type = "buttons", items = { { id = "add", label = "ADD", tone = "accent" } }, weight = 1 },
} }
```

**`tabs`**: pages of one window; the bar switches them at once (no rebuild). `id`, `value` (the open tab's id),
`tabs = { {id, label, components}, ... }` (2-6 tabs, labels under 14 characters). Event `tab` (save it in your
state and pass it back as `value`, so a rebuild keeps the page).
```lua
{ type = "tabs", id = "pages", value = S.tab, tabs = {
  { id = "today", label = "Today", components = { ... } },
  { id = "settings", label = "Settings", components = { ... } },
} }
```

**`divider`**: a thin line. **`spacer`**: empty room (`height` in metres; in a `row` it takes `weight`: a spacer
first pushes the rest to the right).

### Controls

**`checkbox`**: a box with a tick and its label: an item or a form option. `id`, `label`, `value` (bool).
Event `toggle`, boolean.
```lua
{ type = "checkbox", id = "remind", label = "Remind me", value = S.remind }
```

**`toggle`**: an on / off switch: a setting that applies at once. `id`, `label`, `value` (bool). Event `toggle`.
```lua
{ type = "toggle", id = "sound", label = "Sound", value = S.sound }
```

**`radio`**: one of 2-8, each on its own line with a dot (`inline = true`: side by side). `id`, `options`
(strings or `{label, value}`), `value`, `label?`. Event `radio`.
```lua
{ type = "radio", id = "again", options = { "Once", "Every day", "Weekdays" }, value = S.again }
```

**`segmented`**: one of 2-5 in a single bar: a mode or a view. `id`, `options`, `value`, `label?`. Event `segmented`.
```lua
{ type = "segmented", id = "view", label = "Show", options = { "Day", "Week", "Month" }, value = S.view }
```

**`select`**: a dropdown: shows the chosen option; a tap opens a list in front of the window (with a search field
above 15 options). For 6-200 options. `id`, `label?`, `options` (strings or `{label, value}`), `value`,
`placeholder?`. Event `select`.
```lua
{ type = "select", id = "sound", label = "Alarm", value = S.sound,
  options = { { label = "Bell", value = "bell" }, { label = "Chime", value = "chime" }, { label = "Beep", value = "beep" } } }
```

**`slider`**: a number in a range, dragged. `id`, `label?`, `min`, `max`, `step?`, `value`, `unit?`, `live?`
(true: also sends while dragging, at most 5 a second). Event `slider` when let go.
```lua
{ type = "slider", id = "volume", label = "Volume", min = 0, max = 100, step = 5, value = S.volume, unit = "%" }
```

**`stepper`**: a number with - and +: counts, minutes, small exact values. `id`, `label?`, `min?`, `max?`,
`step?` (default 1), `value`, `unit?`. Event `stepper` on each tap.
```lua
{ type = "stepper", id = "work", label = "Work", min = 5, max = 90, step = 5, value = S.work, unit = "min" }
```

**`field`**: a labelled one-line text box; a tap opens the system keyboard. `id`, `label?`, `value?`,
`placeholder?`, `secure?` (dots), `clear?` (empties itself after sending). Event `input` with the text when the
keyboard closes (the user closes it before tapping anything else in the window).
- In a **form** (the text plus other values, then an ADD or SAVE button): keep the text in your state
  (`input` -> `S.text = e.value`), pass it back as `value = S.text`, read `S.text` when ADD is tapped, then set
  `S.text = ""` and `render()`: the field empties in place. No `clear` here (it would empty the box before ADD).
- As a **send box** (the keyboard's Enter is the action: a quick note, a search): `clear = true`, act in `on(e)`.
- For a send box with its own SEND button use `input` (`id`, `prompt?`, `placeholder?`).
```lua
{ type = "field", id = "title", label = "What", value = S.text, placeholder = "call mum" }   -- a form's text
```

**`buttons`**: 1-12 actions `{id, label, tone?}`, 4 a row; `tone = "accent"` for the one primary action,
`"error"` for one that deletes. Event `button`. Buttons act; they never hold a value.
```lua
{ type = "buttons", items = { { id = "add", label = "ADD", tone = "accent" }, { id = "clear", label = "CLEAR", tone = "error" } } }
```

### Pickers

**`date`**: a month grid: weekday header (Monday first), today ringed, the chosen day lit, ◀ ▶ months; days
outside `min` / `max` are dim and cannot be picked. `id`, `value?` (`"YYYY-MM-DD"`), `min?`, `max?`, `time?`
(true: an hour and a minute stepper under the grid; value `"YYYY-MM-DDTHH:MM"`), `minute_step?` (default 5).
Event `date` when a day or the time changes (the arrows only turn the page).
```lua
local d = app.date()
local today = string.format("%04d-%02d-%02d", d.year, d.month, d.day)
{ type = "date", id = "when", time = true, min = today, value = S.when }   -- S.when = "2026-11-20T09:30" or nil
```

**`time`**: a time of day, - and + for hours and minutes. `id`, `label?`, `value` (`"HH:MM"`), `step?`
(minutes, default 5). Event `time`.
```lua
{ type = "time", id = "wake", label = "Wake up", value = S.wake, step = 15 }
```

**`color`**: swatches; the chosen one ringed. `id`, `value` (`"#RRGGBB"`), `options?` (1-24 colours; default 12
that read well on the glass). Event `color`.
```lua
{ type = "color", id = "tint", value = S.tint }
```

### Lists

**`list` with ids**: rows to pick (a title, a dim second text on the right, a tone bar, a badge); the chosen row
lit; scrolls by dragging when there are more rows than fit (so a `list` holds all its rows, up to 100: no "+3
more" line, unlike other components). `items = { {id, text, sub?, tone?, badge?}, ... }`
(1-100), `selected?` (a row id), `rows?` (rows shown, default 6). Event `list` (e.id = the row's id). A `list`
without ids is the old read-only bullet list.
```lua
{ type = "list", id = "todo", selected = S.picked, items = {
  { id = "t1", text = "Buy bread", sub = "today" }, { id = "t2", text = "Call the plumber", tone = "warn", badge = "late" } } }
```
`grid` (`columns` 1-7, `cells = { {id?, text, sub?, tone?} }`) is for boards and launchers where position
matters; for a list of things use `list`.

### Canvas: draw anything, tap the shapes

A 2D drawing in its own coordinate space. Use it for maps, floor plans, seat plans, boards, gauges, any custom
picture. Coordinates are canvas units: `width` x `height` (default 100 x 100), origin at the top left, **y down**
(like SVG). The canvas keeps its aspect ratio and fills the window's width (`draw_height` in metres to make it
smaller). For geography: `fit = "lonlat"`, `bounds = {west, south, east, north}` (degrees); then `x` is the
longitude and `y` the latitude (north up, equirectangular), and the tap's point comes back as `"lon,lat"`.

Fields: `id`, `width`, `height` (or `fit` + `bounds`), `shapes`, `selected?` (a shape id: drawn lit),
`select?` (default true: a tap lights the shape it hit), `background?` (a tone: a soft backdrop).

Shapes (all coordinates in canvas units; `id` makes a shape tappable):

| type | fields |
|---|---|
| `rect` | `x`, `y` (top left), `w`, `h`, `r?` (corner radius) |
| `circle` | `x`, `y` (centre), `r` |
| `line` | `x1`, `y1`, `x2`, `y2` (or `points`) |
| `polyline` | `points = { {x, y}, ... }` (2-64) |
| `polygon` | `points = { {x, y}, ... }` (3-64), closed |
| `text` | `x`, `y`, `text`, `size?` (mm, default 11), `align?` (`left`, `center`, `right`) |
| `arc` | `x`, `y` (centre), `r`, `from`, `to` (degrees, 0 = up, clockwise: a gauge is from = -120, to = 120) |

Style, on any shape: `tone` (its colour, default accent), `fill` (`false`, or a tone; rect, circle and polygon
are filled softly by default), `stroke` (`false`, or a tone; default the tone), `stroke_width` (mm, 0.5-8;
default 1.4, arcs 3), `opacity` (the fill, 0-1, default 0.28), `label` (a short text in the middle of the
shape), `size` (the label's size, mm).

A tap sends `e.type = "shape"`, `e.id` = the topmost shape with an id under the finger (inside a rect, circle or
polygon; within 6 mm of a line, polyline or arc; on a text), `""` when no shape was hit, and `e.value = "x,y"`
in canvas units. Hovering a shape lights it; the tapped one stays lit (accent) until another is tapped or you
set `selected`.

```lua
{ type = "canvas", id = "plan", width = 100, height = 60, selected = S.room, shapes = {
  { id = "living", type = "rect", x = 2, y = 2, w = 56, h = 36, label = "Living room" },
  { id = "kitchen", type = "rect", x = 60, y = 2, w = 38, h = 22, label = "Kitchen", tone = "ok" },
  { id = "bath", type = "polygon", points = { {60,26}, {98,26}, {98,58}, {80,58}, {80,40}, {60,40} }, label = "Bath", tone = "violet" },
  { type = "text", x = 50, y = 56, text = "tap a room", size = 8, tone = "dim" },
} }
```
How it draws: outlines are strokes in the panel's own mesh; soft fills are flat boxes (rects, circles) or
triangles (polygons, split by ear clipping), all bent with the window. Limits: 120 shapes, 64 points a shape,
1200 points in all, 60 texts and labels. Simplify shapes to what the eye needs at 60 cm: 8-30 points per area is plenty.
Change only tones, labels, fills and `selected` to highlight things: that updates in place; moving or adding
shapes rebuilds the window (at most once a second).

### Display

`text` (`style = "big"` for one short big line), `stats` (1-4 big numbers), `progress` (bars), `kv` (key /
value lines), `checklist`, `timeline`, `table`, `chart`, `kanban`, `image`: see `quest-views` for their fields.
Their values update in place (section 4).

### Dialogs and short messages (calls, not components)

```lua
ui.modal("Delete", "Delete this reminder?", { "DELETE", "CANCEL" }, function(i, label)   -- i: 1 = the first button
  if i == 1 then remove(S.picked); save(); render() end
end)
ui.modal("Saved", "Your settings are saved.", { "OK" })                                  -- an alert
ui.toast("Added", "ok")                                                                   -- a short line that fades
ui.notify{ text = "Break over", tone = "ok", sound = "ping", panel = "main" }            -- a callout; a tap opens the window
```
A dialog opens over your window and closes when a button is tapped. Ask before anything that deletes. To ask for
a text, put a `field` in the window (there is no text prompt dialog).

---

## 4. Updating a window live

Write ONE `render()` that builds the whole window from your state, and call it after every change. The app
compares the new spec with the built one:

- only **values** changed (a control's `value`, a list's `selected` and row texts, the items of `stats`,
  `progress`, `kv`, `checklist`, `timeline`, a canvas's `selected` and its shapes' tones, fills, labels, texts,
  any `text`): the built window takes them **in place**: no rebuild, no flash, a press in progress is kept;
- anything else (a component added or removed, a different label or option list, a shape moved): the window is
  rebuilt (at most once a second).

So keep the structure stable: the same components in the same order, the same options; change values. A
countdown is a `text` with an `id` (LIVE: only that label changes every second).

A control the user just changed keeps the user's value for a second, so a slow `render()` never makes it jump back.

## 5. Recipes

**A form: pick a day and a time, then add** (a reminder, a booking)
```lua
render = function()
  local items = {}
  for _, r in ipairs(S.list) do items[#items + 1] = { id = r.id, text = r.text, sub = (r.when:gsub("T", " ")) } end
  ui.panel{ id = "main", title = "Reminders", on = on, components = {
    { type = "card", title = "New reminder", components = {
      { type = "date", id = "when", time = true, min = today(), value = S.when },
      { type = "row", align = "center", components = {
        { type = "field", id = "text", label = "What", value = S.text, placeholder = "call mum", weight = 3 },
        { type = "buttons", items = { { id = "add", label = "ADD", tone = "accent" } }, weight = 1 },
      } },
    } },
    #items > 0 and { type = "list", id = "list", items = items, rows = 4 }
                or { type = "text", text = "No reminders yet: pick a day, type what, ADD.", tone = "dim" },
  } }
end
```
(`on`: `date` -> `S.when = e.value`; `input` -> `S.text = e.value` (sent when the keyboard closes, before the
tap on ADD); `button add` -> check both, append, clear
`S.text`, set the alarm with `app.at`, `ui.toast`.)

**A settings page**: one card per topic, the right control per value, no buttons except a reset.
```lua
{ type = "card", title = "Timer", components = {
  { type = "stepper", id = "work", label = "Work", min = 5, max = 90, step = 5, value = S.work, unit = "min" },
  { type = "stepper", id = "rest", label = "Break", min = 1, max = 30, value = S.rest, unit = "min" },
  { type = "toggle", id = "auto", label = "Start breaks by themselves", value = S.auto },
} },
{ type = "card", title = "Sound", components = {
  { type = "select", id = "tone", label = "Alarm", options = TONES, value = S.tone },
  { type = "slider", id = "vol", label = "Volume", min = 0, max = 100, step = 10, value = S.vol, unit = "%" },
} },
```

**A map or a floor plan**: a `canvas` on the left part of the window and the details of the tapped thing beside or
under it (a `card` with `kv` or `text`); a `select` over the same ids for those who prefer a list. Colour areas by a
value with tones (`ok`/`warn`/`error`, or `series1`...`series8` for categories). Draw approximate shapes when you
have no real coordinates and say so in a dim text ("schematic").

**A second window for a picker**: when a pick needs room (a big list), `ui.panel{id = "pick", ...}` and
`ui.open("pick")`; close it in its `on` after the pick (`ui.close("pick")`).

## 6. Limits

| What | Limit (more is refused by `ui.panel` with an error that says which component) |
|---|---|
| components at the top of a window | 16 (more than 8: a warning; use tabs) |
| components in all, inside cards, rows and tabs | 48; containers nest 3 deep; no tabs inside tabs |
| a card or tab's children / a row's | 12 / 6 |
| tabs | 2-6 |
| options: radio / segmented / select | 2-8 / 2-5 / 1-200 |
| list rows | 100 |
| canvas | 120 shapes, 64 points a shape, 1200 points, 60 texts and labels |
| `scene3d`, `physics`, `image` | top level only |

## 7. Checklist before you ship a window

- [ ] Each value has its own control (date, time, stepper, slider, toggle, select...); no value is a button.
- [ ] Buttons are verbs, 1-4 per group, one `accent` (the primary action), `error` only for deleting.
- [ ] Spatial data is a `canvas`; a long choice is a `select`; 2-5 choices are `segmented` or `radio`.
- [ ] Related controls are in `card`s with one-word titles; more than ~8 components are split by `tabs`.
- [ ] `render()` builds everything from the state; the structure stays the same and only values change.
- [ ] `on(e)` handles every event type your components send (section 2) and converts numbers with `tonumber`.
- [ ] Every value is saved (`store`) and shown again after a restart (pass it back as `value`).
- [ ] Empty states say what to do. Texts are short. No brand names; English.
- [ ] `mcx-ui plugin check <id>` says `warnings: 0`; every control tried once with `mcx-ui plugin event <id>
      <type> <id> [value]` (e.g. `plugin event todo date when 2026-11-20T09:30`, `plugin event todo shape kitchen
      40,12`).
