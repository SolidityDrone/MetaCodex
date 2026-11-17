# Lua scripts and app plugins: the headset grows while it runs

The app's C# is compiled ahead of time and cannot load code at run time. The headset grows through Lua and data
instead: scripts, gesture plugins and app plugins. They run inside the app (`Runtime/Scripting/ScriptHost.cs`) in a
soft sandbox: no files and no processes (`os.time`, `os.date` and `os.clock` are there; plugins use `app.clock` and
`app.date`, which the test harness can fake). A plugin reaches the network only through `net.get` / `net.post`, over
https, to the hosts its `plugin.json` declares ("The network" below); a script has no network. Every Lua call runs under an instruction budget, and an error stops
only its own script or plugin. Nothing needs an APK rebuild.

| Kind | Where | What it can do | Skill |
|---|---|---|---|
| Script | `/mnt/quest/scripts/<name>.lua` | react to events, run app actions, add voice commands | `quest-scripting` |
| App plugin | project `/root/metacodex-projects/plugins/apps/<id>/` | its own windows, menu tiles, voice commands, alarms, saved state, and https requests to the hosts it declares | `quest-plugins` |
| Gesture plugin | project `/root/metacodex-projects/plugins/gestures/<id>/` (pose files, `main.lua`) | new poses and gestures and what they do ([ui.md](ui.md#gesture-plugins-no-rebuild)) | `quest-plugins`, `quest-gesture-plugins` |
| Behaviour plugin | project `/root/metacodex-projects/plugins/behaviours/<id>/` | reactions to gestures, the voice or the time, no window | `quest-plugins`, `quest-scripting` |

The app reloads a script or a plugin within a second of a change. MQH > SCRIPTS and CONTROL > MQH > PLUGINS list them; an
agent that changes one shows the change and asks the user first (AGENTS.md).

## Plugin projects

A plugin is a project of type `plugins` with a fixed kind (`Runtime/Scripting/PluginProject.cs`):

- The folder is `/root/metacodex-projects/plugins/<apps|gestures|behaviours>/<id>/`; its name is the plugin id. It holds
  `plugin.json`, `main.lua`, optional `poses/*.json`, `AGENTS.md` and `CLAUDE.md` (what an agent reads first) and git.
- The marker `.metacodex/project.json`, `{"type": "plugins", "kind": "app"}`, is written read-only. The app loads a
  project only when the marker's kind is its folder's kind. `plugin.json`'s `"kind"` never changes it: a different
  value is a `warn` line (`plugin check` counts it). A folder without the marker, with a marker of another type or
  kind, or with an id another plugin has, is not loaded; the log says why once, and `plugin check <id>` shows it.
- Made by THREADS > + PROJECT > PLUGIN or CONTROL > MQH > PLUGINS > + NEW PLUGIN… (APP, GESTURE, BEHAVIOUR, then a name),
  by voice (`new_project`, type `plugins`, kind), or by `mcx-project new plugins --kind <kind> <name>` (through the
  control port: `plugin new <name> <kind>`). The app writes the scaffold of the kind (APP: a window with a counter
  and a menu tile; GESTURE: a both-palms-up detector and a toast; BEHAVIOUR: a triple clap and a toast), the marker
  and the agent files, runs the plugin at once, then starts git (first commit) and adds the project to MQH: THREADS
  lists it under PLUGINS, and a thread on it is an agent working on the plugin.
- Older plugin folders in `/mnt/quest/plugins/<id>/` still load. On an id clash the project wins (the log says so).
  The one example the app adds by itself, `core-poses` for the V shortcut, stays such a folder.
- `ScriptHost` scans the projects folder with the others, off the main thread, once a second; it does not walk `.git`.
  `mcx-project type <dir>` says `plugins`, `mcx-project kind <dir>` the kind; `mcx-project set` refuses a plugins project.

## App plugins

An app plugin makes windows drawn with the app's own UI kit, and tiles on the DOCK and on the wrist menu's outer ring.
It loads again at every start, and its `main.lua` makes its windows and tiles again from its saved `store`.

Four examples ship with the app: **Timers** (a focus timer in the tomato style, with alarms), **Calendar** (a month
grid with entries and reminders), **Weather** (the weather now and the next 3 days for a city the user picks, from
Open-Meteo: the model for data from the network) and **Components** (a gallery of every window component, working).
Install one from CONTROL > MQH > PLUGINS (EXAMPLES TO ADD, + ADD), by
voice, or with `mcx-ui plugin example timers`: it becomes a plugin project (`plugins/apps/timers`) with the example's files, the
project's AGENTS.md and git, so a thread on it can change it. Their files are in
`Runtime/Scripting/Resources/PluginExamples/<id>/`.

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
| `net.get(url, [opts], fn)`, `net.post(url, body, [opts], fn)`, `net.cancel(id)` | An https request to a host `plugin.json` `"network"` declares; returns its id at once (`nil, why` when refused) and calls `fn(res)` in a later frame. `opts`: `headers`, `timeout` (seconds). `net.cancel`: its `fn` never runs. See "The network". |
| `plugin.id`, `plugin.name`, `plugin.version`, `plugin.kind`, `plugin.dir`, `plugin.network` | The plugin's id, `plugin.json` name and version, its fixed kind (`""` for an older folder), its Linux folder and its declared hosts. |

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

#### The component library (`Shell/ShellViewControls.cs`, `UI/UiViewControls.cs`)

Plugin windows and agent views also have controls, pickers, layout and a canvas; the guide for agents (fields,
events, examples, design rules, checklist) is `mqh/plugins/metacodex-guide/skills/quest-plugins/reference/components.md`,
and the example plugin `components` shows every one working.

- Controls: `checkbox` (event `toggle`, boolean), `radio`, `segmented` (2-5), `select` (1-200 options; a tap opens a
  list window in front, with a search field above 15), `slider` (event on release; `live` also while dragging, 5 a
  second at most), `stepper`, `field` (event `input`); pickers `date` (month grid, Monday first, today ringed, min /
  max, `time = true` adds hour and minute: `YYYY-MM-DD[THH:MM]`), `time` (`HH:MM`), `color` (`#RRGGBB`); a `list` whose
  items have ids (event `list`, e.id = the row).
- Layout: `card` (a titled group), `row` (children side by side by `weight`), `tabs` (2-6 pages, switched without a
  rebuild, event `tab`), `divider`, `spacer`. 48 components in all, nesting 3 deep, no tabs in tabs, no 3D scene or
  image inside containers.
- `canvas`: its own units (`width` x `height`, y down) or `fit = "lonlat"` with `bounds`; rect, circle, line,
  polyline, polygon, text, arc; outlines are strokes, fills are flat boxes or triangles (ear clipping; the C#-only
  shape kind `Tri` of `UiShapeMesh`, drawn as the shader's flat kind). A tap sends `shape` with the topmost shape with
  an id and `"x,y"` in canvas units. 120 shapes, 64 points a shape, 1200 points, 60 texts and labels.
- In place: a new spec that differs only in values (the controls' `value`, `selected`, block items, canvas tones and
  labels, texts; `ShapeOf` leaves them out) is applied to the built controls by the window's watcher (`UiViewWatch`)
  without a rebuild; a value within 1 s of the user's own change waits. In a view, value controls answer the thread
  with `label: value` after 1.5 s of quiet.
- `mcx-ui plugin event <id> <type> <component id> [value]` takes the library's event types too.

A `text` with an `id` is LIVE: when only such texts change, the app sets the labels and does not rebuild the window.
A new spec with the same components (kinds, ids, sizes, columns, title, count) also sets the texts of its `text`
components in place, whether they have an `id` or not, as long as each new text fits its row. Any other change
rebuilds the window at most once a second.

A window that is updated keeps its place and its bend, and the update waits until a press ends: when a tap, a pinch or
a drag is on the window, its new spec is kept and applied the frame after the release, so the tap reaches the
element it hit. (The app logs `plugin window <id>: rebuild deferred until the press ends` and `updated in place`.)

The spec is checked before anything is built (`Runtime/Scripting/ViewSpecCheck.cs`): an error (an unknown component,
a missing id, more than 16 components or 49 cells, a duplicate id) raises a Lua error that names the component and
the field; a warning (a long title or label, more than 8 components) goes to the plugin's log as `warn ...`.

### The network

A plugin lists the hosts it calls in `plugin.json`: `"network": ["api.open-meteo.com", "geocoding-api.open-meteo.com"]`.
An entry is a host name; `*.example.com` covers the subdomains of `example.com` (not `example.com` itself). A scheme, a
path, a port, `*` or `*.com` is left out with a `warn` line. MQH > PLUGINS shows "uses the network: <hosts>" in the
plugin's detail, and `plugin check <id>` shows `network: <hosts> (n in flight, m in the last minute)`.

`net.get(url, [opts], fn)` and `net.post(url, body, [opts], fn)` (the body a text, sent as `text/plain`, or a table,
sent as JSON) never wait. Each returns the request's id at once, and `fn(res)` runs in a later frame of the plugin tick,
under the instruction budget like any callback; an error in it stops the plugin. `res` is `{ ok, status, body, json,
error }`: `ok` is true for a reply with status 200-299, `status` is 0 when no reply came, `body` is the text, `json` is
the decoded reply when it is JSON (by its type or its first character) and nil otherwise, and `error` is nil when ok,
else `status <n>`, `timeout`, `too large`, a network error, or the reason a request is refused. Every request gets exactly
one callback, a refused one too, unless `net.cancel(id)` dropped it or the plugin unloaded.

The rules, in `ScriptHost` (`NetCheckUrl`, `NetCheckUri`, also for every redirect):

| Rule | Value |
|---|---|
| Scheme | https; plain http only to `127.0.0.1` and `localhost` (as the app's network security config), declared like any host |
| Host | one of the declared hosts, else `net: host not declared in plugin.json "network": <host>` (a `warn` line in the plugin log) |
| In the URL | no user name or password |
| In flight | 4 a plugin; the 5th is refused until a reply comes |
| Rate | 60 requests started in a minute a plugin |
| Reply body | 1 MB; a longer reply is cut there, `res.error = "too large"` |
| Request body | 1 MB |
| Timeout | 15 s by default, `opts.timeout` 1 to 30 s; a fetcher that does not answer gets `timeout` one second later |
| Headers | the plugin's own, sent as set; `Host`, `Content-Length`, `Connection`, `Transfer-Encoding`, `Expect` and `proxy-*` are the app's; `User-Agent: MetaCodex-plugin/1.0 (<id>)` unless the plugin sets one |
| Cookies, credentials | none kept and none added; `Authorization` and `Cookie` go only to the host they were set for |
| Unload | a disable, a delete, an error or a reload cancels every request of the plugin; no callback of it runs after that |

The fetch (`Shell/ShellPluginNet.cs`) uses `System.Net.Http` on a pool thread, not `UnityWebRequest`: on Android
`UnityWebRequest` runs on the platform's HTTP stack, which follows redirects to any host by itself and keeps cookies in
a store shared by the whole app. Here the handler keeps no cookies, redirects are followed by hand (3 at most, each
checked against the plugin's hosts), the body is read to 1 MB and one byte more and no further, and each request has a
timeout and a cancel. HttpClient is already in the IL2CPP build (MQH, the local AI). Android's network security config
does not reach these managed sockets, so the host's own scheme rule is the one that holds, and it is the same rule.
The text is decoded and the JSON parsed on the fetch thread; the main thread only makes the Lua table, at most 8 replies
a frame. With no request out, nothing runs per frame.

The log: the first reply of a plugin after each load (`net: GET https://host/path -> 200 (2.1 KB)`) and each failure,
at most once in 10 s, never with the query (it may hold a key). Keys belong to the user: a plugin that needs one asks
for it in an `input` and keeps it in `store`, which is a plain file, not a vault (skill `quest-plugins`).

The Weather example shows the pattern: no request before the user picks a city; the parsed result and its time in
`store`, shown at once after a restart with its age; one `app.at` at the result's time + 30 minutes; a failure keeps the
data, says why and retries in 5 minutes; one request at a time, a new city cancels the old one; the credit line and
SOURCE that the data's licence (CC BY 4.0) asks for ([LICENSES.md](LICENSES.md), 4.5).

### Lifetime

- Unloading a plugin (disable, delete, an error, a reload after an edit) removes its windows, tiles, timers, voice
  commands, gestures and its network requests (no callback runs after). A window made again within 10 s of its removal (a reload) comes back at the same place,
  open if it was open.
- An error in any callback stops the plugin (log `ERROR ...`, a callout); it loads again at the next start.
- Only app plugins' tiles go on the DOCK and the wrist menu.
- `mcx-ui plugin check <id>`: loaded, disabled or error; its kind and folder; its windows (builds, open), tiles, voice
  commands, timers and the last 10 warnings and errors; for a folder that is not loaded, why.
- `mcx-ui plugin event <id> <type> <component id> [value]`: what a tap or a sent text does in its window (`input`,
  `cell`, `toggle`, `button`, `choice`, `open`, `close`), so an agent can try every part of a window without hands.
- Delete (MQH > PLUGINS > DELETE, `mcx-ui plugin delete <id>`): the folder (a project's with its git history), its
  saved state and enabled flag, and a project's MQH project with its threads.

## Tests

- `tests/plugins/run-plugin-tests.sh <plugin dir>... | --examples | --scaffolds | --rules | --specs`: on the PC, no editor. The
  real `ScriptHost` and the Lua interpreter with a fake app: load (a folder with the marker as a project of its kind),
  the tile, every button, the gestures it reacts to and hand shapes through a stand-in engine, three hours of clock,
  voice lint, a restart and the unload. `--rules`: the marker and kind rules, id clashes, reload, create, delete.
  `--net`: the network API and the Weather example, offline: the fake fetcher answers from canned replies by URL
  (`tests/plugins/fixtures/net/*.json`); a request no reply matches is a FAIL that names the URL.
  `--specs`: the component library's spec rules (a window with every component passes; broken specs are refused).
  `--scaffold <kind> <id> <dest>` and `--example-project <id> <dest>` write a project as the app makes it.
- `tests/plugins/device-test.sh [timers|calendar|weather]`: the same plugin on the headset.
- Editor self-test sections `scripts`, `agentscript`, `gestureplugins` and `agentplugin`.
