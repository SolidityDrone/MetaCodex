---
name: quest-scripting
description: Change how the MetaCodex headset app behaves while it runs - gestures (double clap, pinch, fist, palm up...), voice commands, panels, toasts, terminal actions, layout and settings - by writing a Lua script, with no rebuild. Use when the user asks things like "make it so if I double clap X happens", "when I show my palm open the files", "move panels with a fist instead of a pinch", "add a voice command that ...", or wants a new hands-free shortcut.
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
| `gesture.names()` / `gesture.list()` | every gesture the app knows: the built-ins above plus plugins' (`thumbs-up`, `swipe-left`, ...) |
| `gesture.active(name[, hand])` | true while that gesture is active |
| `gesture.define(name, opts, detect)` / `gesture.pose(name, spec)` | new gestures: see the skill `quest-gesture-plugins` |
| `hands.get("left"\|"right")` (alias `hands.state`) | `{tracked, pinch, pinching, grabbing, pointing, open, palmUp, x, y, z, curl = {thumb, index, middle, ring, little}, rel, vel, speed, palmFacing, thumbPointing, ...}` |

New gestures (hand poses, swipes, two-hand shapes) belong in a **plugin** folder
(`/mnt/quest/plugins/<id>/`, skill `quest-gesture-plugins`); a script can react to them by name.

## App API

| Call | Does |
|---|---|
| `ui.toast(text[, "info"\|"ok"\|"warn"\|"error"])` | a short message in front of the user |
| `ui.open(id)` `ui.close(id)` `ui.toggle(id)` | panels: `dock`, `terminals`, `files`, `git`, `browser`, `scripts`, `gestures`, `agents`, `threads`, `thread`, `thread-new`, `vault`, `settings`, `voice`, `term-1`... |
| `ui.panels()` | ids of all panels |
| `ui.modal(title, text, {"YES","NO"}, fn)` | a dialog; `fn(index, label)` with the answer |
| `ui.select(true\|false)` | the select tool (pinch-drag a rectangle on a panel) |
| `terminal.send(text)` / `terminal.run(cmd)` / `terminal.new()` | type into the active terminal (`run` adds Enter) / open a new one |
| `browser.open(url)` | the Horizon OS browser at a URL (also `http://127.0.0.1:<port>` for local web apps) |
| `keyboard.open()` | the system keyboard for the terminal |
| `settings.get(key)` / `settings.set(key, value)` | app settings (strings in and out), saved and synced to MQH |
| `store.get(key)` / `store.set(key, value)` | this script's own persistent strings |
| `app.after(sec, fn)` / `app.every(sec, fn)` / `app.cancel(id)` / `app.time()` | timers |
| `app.action("verb:arg")` | one action string: `open:` `close:` `toggle:<panel>`, `toast:`, `run:<cmd>`, `type:<text>`, `url:<url>`, `layout:fixed\|float`, `spin:<deg>`, `select`, `keyboard`, `terminal:new`, `recenter`, `gesture:<name>`, `voice:listen\|stop\|toggle\|say:<text>\|run:<text>` |
| `head.pose()` | `{x, y, z, fx, fy, fz}`: eyes and look direction |
| `voice.command{...}` | a voice command (below); removed when the script is disabled or deleted |
| `voice.say(text)` / `voice.run(text)` / `voice.listen()` / `voice.list()` | speak with the local voice / run text as if spoken / start listening / command names |
| `log(...)` / `print(...)` | lines in the script log (`mcx-ui script log <name>`) |

Settings keys: `gestures.clickMode` (0 pinch, 1 poke, 2 both), `gestures.grabMode` (0 pinch the header,
1 closed hand, 2 both), `gestures.pointMode` (0 ray always, 1 only in the pointing pose),
`gestures.pinchOn` (0.6-0.98), `gestures.clapEnabled`, `gestures.clapDistance` (m), `gestures.rayVisible`,
`gestures.customEnabled` (plugin gestures on/off), `layout` (`fixed` | `float`), `carouselRadius`,
`panelDistance`, `uiScale`, `accent` (`cyan` `amber` `green` `violet` `red` `ice` `magenta` `gold` `white`),
`glow` (0-2), `panelOpacity` (0.3-1), `motion` (0-2), `grid`, `scanlines`, `sounds`, `soundVolume`, `bendPanels`.
No-code links from a gesture to an action live in Settings too (Gestures panel > BIND, or
`mcx-ui gesture bind <gesture> <action>`).
Example: "move panels by closing my hand instead of pinching" = `settings.set("gestures.grabMode", "1")`
(a one-line script, or the Settings panel).

## Voice commands

The user talks to the app with push-to-talk (TALK on the dock, or a gesture bound to `voice:toggle`).
Speech becomes text on the headset (local speech-to-text, nothing leaves it); a phrase template that
matches runs its command at once, anything else goes to a small local LLM that sees every command as a
tool. A script adds its own command:

```lua
-- description: "git status" by voice
voice.command{
  name = "git_status",                       -- snake_case, unique
  description = "Show git status of the project in the terminal",   -- one line: the LLM reads it
  params = { path = { type = "string", description = "folder", required = false } },
  phrases = { "(show|what is) [the] git status", "git status [of|in] {path}" },
  vocabulary = { "git" },                    -- words to hear right and fix when misheard
  examples = { {"what changed in the repo", "git_status"} },
  run = function(args)                       -- args.path is nil when not said
    terminal.run("git -C " .. (args.path or ".") .. " status")
    return "Git status"                      -- shown (and spoken if the user enabled spoken replies)
  end
}
```

Phrase templates: plain words, `(a|b)` one of, `[a|b]` optional, `{param}` captures words for a parameter,
`{param:value}` sets a fixed value ("(make|let) the panels follow me {mode:float}").
For phrasings no template covers, the local LLM picks a command line: teach it with
`examples = {{"make it orange", "set_accent amber"}}` (request, then the line: name and arguments in the
order of `params`). `llm = false` keeps a command to its phrases only (use it for anything that types or
deletes free text).
Text is matched lower-case without punctuation, after polite fillers ("hey", "could you", "please") are
dropped. Enum parameters (`enum = {"cyan","amber"}`) accept spoken synonyms:
`synonyms = { color = { orange = "amber" } }`; a slot tries the known words first, longest first, so
"dome radius" wins over "dome". `confirm = true` makes the user approve in VR before
`run` (use it for anything that types or deletes). Return `"error: why"` when it fails.

Give every command rich context, like an MCP tool: the local LLM and the speech recogniser only know what
the command declares.

- `description`: one line, what it does and what its parameters mean.
- `params`: `type` (`string`, `number`, `integer`, `boolean`), `description`, `required`, `enum`; numbers take
  `min`, `max` and `unit` (`"%"`, `"m"`, `"s"`...). Spoken numbers are parsed for you: "fifty percent", "two
  point five", "30 cm" (to metres), "max", "half".
- `vocabulary = {"warpdrive", "nebula"}`: unusual words the command needs heard right. They go into the
  speech-to-text prompt, and a misheard word one or two letters off ("warpdrove") is corrected to them.
- `examples`: two to four requests no phrase covers, each with its command line.

One sentence can hold several commands ("turn on ambient focus and set the intensity to zero"): the fast
path splits on "and", "then", "also" and commas; the LLM may answer with up to three lines.
Test without speaking: `mcx-ui voice match "git status"` (which command and arguments), then
`mcx-ui voice run "git status"` (runs it, prints the result as JSON); `mcx-ui voice list` lists commands.

## Rules

- One behaviour per file; keep callbacks short. A callback that runs more than 200,000 Lua instructions is
  stopped and the script disabled (shown as ERROR in the Scripts panel): no busy loops, use `app.every`.
- Never put secrets in scripts. Scripts can type into the terminal, so they run with the user's rights.
- Prefer gestures that cannot fire by accident: sequences (double clap) or a hold, not a single pinch.
