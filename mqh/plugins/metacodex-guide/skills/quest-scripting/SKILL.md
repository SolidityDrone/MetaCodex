---
name: quest-scripting
description: Change how the MetaCodex headset app behaves while it runs - gestures (double clap, pinch, fist, palm up...), voice commands, panels, toasts, terminal actions, layout and settings - by writing a Lua script, with no rebuild. Use when the user asks things like "make it so if I double clap X happens", "when I show my palm open the files", "move panels with a fist instead of a pinch", "add a voice command that ...", or wants a new hands-free shortcut.
---

# Quest scripting: live behaviours in the MetaCodex app

The app (Unity, on the headset) runs Lua in two forms, with the same API (this skill is its one home):
- **plugins**: plugin projects with a fixed kind (app, gesture, behaviour) in
  `/root/metacodex-projects/plugins/<apps|gestures|behaviours>/<id>/`, made by the user (THREADS > + PROJECT > PLUGIN)
  with git and a thread to work in: skill `quest-plugins` (start there for anything with a window, a menu tile,
  gestures of its own, or more than one small reaction);
- **scripts**: single files **`/mnt/quest/scripts/*.lua`** for one small reaction (below).

Write or edit a file and the app loads it within a second: no rebuild, no restart. Files persist across restarts.
Each runs sandboxed (no `io`, `require`, `load`; `os` has only `time`, `clock`, `date`) and talks to the app only
through the API below. The network is for plugins only (`net`, https to the hosts their `plugin.json` declares): a
single script has none. The Lua is 5.2 (MoonSharp): no `utf8` library and no `//`; `#` and `string.len` count bytes;
every number is a double (`json.decode` too): `math.floor(n)` or `string.format("%d", n)` for whole numbers.

## Procedure

0. Say what you will add or change (and how to undo it) and wait for the user's OK (Rules: review first).
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
| `gesture.names()` / `gesture.list()` | every gesture the app knows now: the built-ins above plus the loaded plugins' (none on a fresh headset: the examples `core-poses` / `swipes` are opt-in) |
| `gesture.active(name[, hand])` | true while that gesture is active |
| `gesture.define(name, opts, detect)` / `gesture.pose(name, spec)` | new gestures: see the skill `quest-gesture-plugins` |
| `hands.get("left"\|"right")` (alias `hands.state`) | `{tracked, pinch, pinching, grabbing, pointing, open, palmUp, x, y, z, curl = {thumb, index, middle, ring, little}, rel, vel, speed, palmFacing, thumbPointing, ...}` |

A new window or a button in the menu (a timer, a calendar, a tool) is an **app plugin**: skill `quest-plugins`.
New gestures (hand poses, swipes, two-hand shapes) are a **gesture plugin** (a plugin project of kind gesture; skill
`quest-plugins` first, then `quest-gesture-plugins`); a script can react to them by name, but only to names
`mcx-ui gesture list` shows: a script never assumes a plugin's gesture is installed.

## App API

| Call | Does |
|---|---|
| `ui.toast(text[, "info"\|"ok"\|"warn"\|"error"])` | a short message in front of the user |
| `ui.open(id)` `ui.close(id)` `ui.toggle(id)` | panels: `dock`, `terminals`, `files`, `git`, `web` (browser window), `browser` (its LINKS page), `scripts`, `gestures`, `agents`, `threads`, `thread`, `thread-new`, `vault`, `settings`, `voice`, `term-1`... |
| `ui.panels()` | ids of all panels |
| `ui.modal(title, text, {"YES","NO"}, fn)` | a dialog; `fn(index, label)` with the answer (index from 1) |
| `ui.panel{id, title, components, on=fn}` | the plugin's own window (view components; events to `fn(e)`; 4 a plugin): skill `quest-plugins` |
| `menu.add{id, label, glyph, icon, panel=\|run=}` / `menu.remove(id)` | a tile on the DOCK and the wrist menu (app plugins only; 6 plugin tiles at most): skill `quest-plugins` |
| `ui.notify{text, tone, sound, speak, panel}` / `ui.sound(name)` | a callout by the Whisp (a tap opens `panel`) / a short app sound (`click` `open` `close` `ping` `toast` `alert` `error`) |
| `ui.select(true\|false)` | the select tool (pinch-drag a rectangle on a panel) |
| `terminal.send(text)` / `terminal.run(cmd)` / `terminal.new()` | type into the active terminal (`run` adds Enter) / open a new one |
| `browser.open(url)` | the app's WEB window at a URL (also `http://127.0.0.1:<port>` for local web apps; sign-in pages open in the system browser) |
| `keyboard.open()` | the system keyboard for the terminal |
| `settings.get(key)` / `settings.set(key, value)` | app settings (strings in and out), saved and synced to MQH |
| `store.get(key)` / `store.set(key, value)` | this script's own persistent strings (tables: `json.encode` / `json.decode`) |
| `json.encode(value)` / `json.decode(text)` | Lua table to JSON text and back (`decode` returns `nil, message` on bad text) |
| `net.get(url, [{headers=, timeout=}], fn)` | plugins only: an https GET to a host `plugin.json` `"network"` lists; returns the request id at once (`nil, why` when refused) and calls `fn(res)` later, `res = {ok, status, body, json, error}` (`json`: the decoded reply when it is JSON); 4 in flight, 60 a minute, 1 MB, timeout 15 s (30 at most); skill `quest-plugins`, "APP: data from the network" |
| `net.post(url, body, [opts], fn)` / `net.cancel(id)` | the same with a body (text, or a table sent as JSON) / drop a request: its `fn` never runs |
| `app.after(sec, fn)` / `app.every(sec, fn)` / `app.cancel(id)` / `app.time()` | timers (session time: `app.time()` starts at 0 with the app) |
| `app.clock()` / `app.clock{year, month, day, hour, min, sec}` / `app.date([t])` / `app.at(t, fn)` | wall clock (seconds since 1970) / a local time in seconds / `{year, month, day, hour, min, sec, wday, yday}` / run once at a clock time |
| `app.action("verb:arg")` | one action string: `open:` `close:` `toggle:<panel>`, `toast:`, `run:<cmd>`, `type:<text>`, `url:<url>`, `layout:fixed\|float`, `spin:<deg>`, `select`, `keyboard`, `terminal:new`, `recenter`, `gesture:<name>`, `voice:listen\|stop\|toggle\|say:<text>\|run:<text>` |
| `head.pose()` | `{x, y, z, fx, fy, fz}`: eyes and look direction |
| `voice.command{...}` | a voice command (below); removed when the script is disabled or deleted |
| `voice.say(text)` / `voice.run(text)` / `voice.listen()` / `voice.list()` | speak with the local voice / run text as if spoken / start listening / enabled command names |
| `voice.remove(name)` / `voice.registry()` | remove one of this script's commands / the whole registry as JSON text (every enabled command, its schema, samples, warnings) |
| `log(...)` / `print(...)` | lines in the log (`mcx-ui script log <name>`; a plugin's: `mcx-ui plugin log <id>`) |
| `plugin.id` `plugin.name` `plugin.version` `plugin.kind` `plugin.dir` `plugin.network` | plugins only: its id, `plugin.json` name and version, its fixed kind (`""` for an older folder), its Linux folder and the hosts its `"network"` declares |

Settings keys: `gestures.clickMode` (0 pinch, the default; the app shows no switch for it: change it only when the
user asks for fingertip pokes: 1 poke, 2 both), `gestures.pointMode` (0 ray always, 1 only in the pointing pose),
`gestures.pinchOn` (0.6-0.98), `gestures.clapEnabled`, `gestures.clapDistance` (m), `gestures.rayVisible`,
`gestures.customEnabled` (plugin gestures on/off), `layout` (`fixed` | `float`), `carouselRadius`,
`panelDistance`, `uiScale`, `accent` (`cyan` `amber` `green` `violet` `red` `ice` `magenta` `gold` `white`),
`panelOpacity` (0.3-1), `motion` (0-2), `grid`, `scanlines`, `sounds`, `soundVolume`, `bendPanels`.
No-code links from a gesture to an action live in Settings too (Gestures panel > BIND, or
`mcx-ui gesture bind <gesture> <action>`).
Windows move only by a pinch on their title bar (or just above it); there is no setting for that.
Example: "show the hand ray only when I point" = `settings.set("gestures.pointMode", "1")`
(a one-line script, or CONTROL > HANDS).

## Voice commands

The user talks to the app with push-to-talk (TALK on the dock, or a gesture bound to `voice:toggle`).
Speech becomes text on the headset (local speech-to-text, nothing leaves it). Then, in this order:
1. **phrase templates** (the fast path): a template that matches runs its command at once;
2. the **decision model**: a small multilingual model compares each part of the sentence with every command's
   samples, examples, phrases and description and picks the command (or none) in ~10-60 ms; values are read
   from the command's schema (enum values and their synonyms, numbers with units, on / off, free text);
3. the local LLM only when the user turned on "Ask the LLM when nothing fits".

So a command is found by **what it declares**: its samples and synonyms are its training data. A script adds one:

```lua
-- description: "git status" by voice
voice.command{
  name = "git_status",                       -- snake_case, unique
  description = "Show git status of a project folder in the terminal (path: the folder, default the current one)",
  params = { path = { type = "string", description = "folder", required = false } },
  phrases = { "(show|what is) [the] git status", "git status [of|in] {path}" },
  samples = {                                -- REQUIRED: 4+ natural requests (English: voice is English only)
    "what changed in the repo", "show me the git status", "is there anything to commit",
    "are there changes in this folder", "how does the repository look",
  },
  vocabulary = { "git" },                    -- words to hear right and fix when misheard
  examples = { {"what changed in the repo", "git_status"} },
  run = function(args)                       -- args.path is nil when not said
    terminal.run("git -C " .. (args.path or ".") .. " status")
    return "Git status"                      -- shown (and spoken if the user enabled spoken replies)
  end
}
```

Phrase templates: plain words, `(a|b)` one of, `[a|b]` optional, `{param}` captures words for a parameter,
`{param:value}` sets a fixed value ("(make|let) the panels follow me {mode:float}"). Text is matched lower-case
without punctuation, after polite fillers ("hey", "could you", "please") are dropped. Enum
parameters (`enum = {"cyan","amber"}`) accept spoken synonyms:
`synonyms = { color = { orange = "amber", gold = "amber" } }`; the longest known name wins ("dome radius"
before "dome"). `confirm = true` makes the user approve in VR before `run` (anything that deletes or cannot be
undone). `llm = false` marks commands that take free words verbatim (typing, shell lines): they run only when
the sentence starts with their own verb (a phrase's first word or a sample's first word). Return
`"error: why"` when it fails. `speak = true` speaks the reply (answers, not confirmations).

### MUST: every command complete, like an MCP tool (all options usable)

Voice, the decision model, the LLM and other agents only know what the command declares. Every
`voice.command` you write or change MUST have:

- `description`: one line, what it does and every option it takes;
- `params`: each with `type` (`string`, `number`, `integer`, `boolean`), `description`, `required`; `enum` for
  closed sets; `min`, `max`, `unit` (`"%"`, `"m"`, `"s"`...) for numbers (spoken numbers are parsed for you:
  "fifty percent", "two meters and a half", "30 cm", "max", "half");
- `synonyms` for every enum value people say differently;
- `phrases` for the common wordings and `samples`: 4 or more natural requests, covering every option (a sample
  per enum value, with and without numbers);
- English only: MetaCodex's voice (speech-to-text, the decision model, spoken replies) is English since
  2026-11-12; do not add Italian or other non-English phrases, samples or synonyms;
- `vocabulary` for unusual words, `examples` for tricky phrasings.

The app checks this when the command registers: missing parts appear as `voice.command <name>: ...` lines in the
script log (`mcx-ui script log <name>`) and as `warnings` in `mcx-ui voice registry`. Fix them all: a command
with warnings is a command the user cannot reach.

### Disabled means gone

A script's or plugin's commands are removed when it is disabled, deleted or stopped by an error, and come back
when it is enabled again. When your behaviour turns a feature off at runtime, remove its command too
(`voice.remove("git_status")`) and register it again when it is back. Never leave a command whose action can no
longer work: the decision model and agents would offer it.

### Test, then tell

`mcx-ui voice registry` (the full JSON: your command, its schema, samples, `warnings: []`),
`mcx-ui voice decide "how does the repository look"` (what the decision model makes of a sentence, with scores;
nothing runs), `mcx-ui voice match "git status"` (the fast path), then `mcx-ui voice run "git status"` (runs it,
prints the result as JSON). One sentence can hold several commands ("turn on ambient focus and set the
intensity to zero", "turn on the focus and then open the files"): they run in the spoken order, and a part without a
verb ("and the softness to half a metre") repeats the previous command with its own values.

## Rules

- **Review first.** Before you create, change, enable or delete a script, a plugin, a gesture or a voice command,
  show the user what will change (file, behaviour, commands added or removed, how to undo) and wait for their
  OK. Then write it, test it as above and report the result. In a plugin project, commit each working step.
- Remove a script: delete its file (or MQH > SCRIPTS > DELETE). Plugins: skill `quest-plugins`, "Turn off or remove".
- One behaviour per file; keep callbacks short. A callback that runs more than 200,000 Lua instructions is
  stopped and the script disabled (shown as ERROR in the Scripts panel): no busy loops, use `app.every`.
- Never put secrets in scripts or plugins (API keys, tokens): the user types a key into a plugin's `input`, the plugin
  keeps it in `store`, and `store` is not a vault (a plain file agents can read). Scripts can type into the
  terminal, so they run with the user's rights.
- Prefer gestures that cannot fire by accident: sequences (double clap) or a hold, not a single pinch.
