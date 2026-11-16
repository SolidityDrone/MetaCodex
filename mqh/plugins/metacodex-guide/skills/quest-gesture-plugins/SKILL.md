---
name: quest-gesture-plugins
description: Create or change hand-gesture plugins for the MetaCodex headset app - new hand poses (thumbs up, finger gun, rock on...), motion gestures (swipes, pushes, waves, circles), two-hand gestures, sequences and combos, and what they trigger (open panels, run terminal commands, open URLs, toggle settings) - live, with no rebuild. Use when the user asks for a new gesture, "when I do X with my hand(s) do Y", a gesture plugin, a pose to record, or to fix a gesture that misfires. Builds on quest-scripting (Lua basics) and metacodex-context (where things live).
---

# Gesture plugins for the MetaCodex app

The MetaCodex app (Unity, on a Meta Quest 3 / 3S) recognises hands every frame. Plugins add **named
gestures** and **what they do**. A plugin is a folder of plain files; the app loads it within a second
of any change, with no rebuild and no restart. You write files; you never touch Unity or C#.

Read this whole file before writing anything. Details: `reference/hand-model.md` (numbers, axes),
`reference/lua-api.md` (every function), `reference/recipes.md` (complete working plugins),
`reference/testing.md` (how to prove it works without hands).

## 0. Decide the lightest mechanism (in this order)

| Need | Use | Code |
|---|---|---|
| A gesture that `mcx-ui gesture list` shows should do something | a **binding** (no plugin) | `mcx-ui gesture bind clap toggle:files` |
| A static hand shape ("finger gun", "OK") | a **pose file** `poses/<name>.json` | JSON only |
| A shape plus a condition a pose can't express (two hands, distances, palm + motion) | a **Lua detector** `gesture.define` | small `main.lua` |
| Motion (swipe, push, wave, circle) | a **Lua detector** on `h.vel` (+ a tiny state machine for waves/circles) | `main.lua` |
| "A then B" (double clap, thumbs up twice) | `gesture.sequence` | `main.lua` |
| The reaction (toast, panel, terminal command, URL, modal) | `gesture.on(name, fn)` or `app.action("...")` | `main.lua` |

Check what exists first: `mcx-ui gesture list` (the 7 built-ins and every plugin loaded on this headset).

**A plugin brings its own poses.** A fresh headset has only the built-ins (`pinch grab point open palm-up
clap select`): the examples `core-poses` (thumbs-up, thumbs-down, victory, horns, call-me) and `swipes`
(swipe-left / right / up) are NOT installed unless the user added them. Never react to `thumbs-up` or any
other non-built-in name your plugin does not define, unless `mcx-ui gesture list` shows it right now: a
`gesture.on` for a name nobody defines never fires, and nothing reports it. Copy the fields you need from
`reference/hand-model.md` (the examples' poses) into `poses/<name>.json` inside your own plugin. If the
same name is defined twice, the plugin loaded last wins, and disabling it drops the name until the other one
reloads: prefer the name the user said, defined once, in your plugin. To install an example itself, ask
first, then `mcx-ui plugin example core-poses` (or `swipes`).

## 1. Where files go

```
/mnt/quest/plugins/<id>/          <id>: lowercase letters, digits, - or _ (folder name = plugin id)
  plugin.json                     required
  poses/<gesture-name>.json       optional, one named pose per file
  main.lua                        optional: detectors, handlers, sequences
  README.md                       optional: what it does, how to use it
```

`/mnt/quest` is the app's shared folder seen from Linux. If it does not exist you are not on the
headset: write to the folder the user or the test harness gives you, same layout.

`plugin.json`:

```json
{
  "id": "finger-gun",
  "name": "Finger gun",
  "version": "1.0.0",
  "kind": "gesture",
  "description": "Point a finger gun (thumb up, index forward) to open the web panel"
}
```

Only `id`/`name`/`version`/`description` are read today (`main` and `poses` override the defaults
`main.lua` and `poses/`). Keep the description one line: CONTROL > PLUGINS shows it.

## 2. Pose files (static shapes)

```json
{
  "name": "finger-gun",
  "description": "thumb up, index straight, the other fingers curled",
  "hand": "any",
  "fingers": { "thumb": 0, "index": 0, "middle": 1, "ring": 1, "little": 1 },
  "tolerance": 0.35,
  "thumbPointing": "up",
  "hold": 0.15,
  "cooldown": 0.6
}
```

| Field | Meaning | Default |
|---|---|---|
| `name` | the gesture name events carry (unique across all plugins) | file name |
| `hand` | `any`, `left`, `right` (events say which hand) | `any` |
| `fingers` | target curl per finger: **0 = straight, 1 = curled**. Omit a finger = "don't care" | all omitted |
| `tolerance` | max distance between target and real curl, per finger (0.05-1) | 0.3 |
| `palmFacing` | `up` `down` `left` `right` `forward` `back` `head` (towards the face) `away` | any |
| `thumbPointing` | same directions, for where the thumb points (only meaningful with thumb ≈ 0) | any |
| `directionMin` | how strict directions are: cosine, 0.6 ≈ within 53°, 0.85 ≈ within 32° | 0.6 |
| `pinch` | `[min, max]` thumb-index pinch strength 0..1 | any |
| `hold` | seconds the shape must hold before `start` fires (0.1-0.3 avoids flicker) | 0.12 |
| `cooldown` | seconds before it may start again | 0.3 |

Curl targets: use 0 or 1 for clear shapes, 0.5 for "half bent". Tolerance 0.3-0.4 suits most people;
go lower only to separate two similar poses. Left/right/forward/back are relative to where the user
faces (head yaw), up/down are world up/down, head/away are towards / away from the eyes.

A pose fires `start` (after `hold`) and `end` (as soon as the shape breaks) per hand.

## 3. Lua: detectors, handlers, sequences (`main.lua`)

```lua
-- description: a one-line summary (shown in the panels)

-- a detector: return true while the gesture is happening; the app adds hold/cooldown and start/end
gesture.define("push", { hand = "any", hold = 0, cooldown = 0.8, description = "open palm pushed forward fast" },
  function(h)
    return h.open and h.palmFacing == "away" and h.vel.z > 1.0
  end)

-- a reaction: runs for every event of that name (phase "start", "end" or "tap")
gesture.on("push", function(e)
  if e.phase ~= "start" then return end
  ui.toast("Push (" .. e.hand .. ")")
  ui.close("web")
end)

-- a sequence: start/tap events in order, within a time window
gesture.sequence({ "thumbs-up", "thumbs-up" }, 1.2, function(e)
  app.action("toggle:threads")
end)
```

Rules that keep it working:

- **Detectors are per frame** (~36 times a second): only read fields and compare numbers. No loops
  over big tables, no string building, no `log` calls inside them. Over 200 000 Lua instructions in
  one call stops the whole plugin (the app keeps its frame rate; the panel shows the error).
- `hand = "any"` runs the detector once per tracked hand and events carry `left`/`right`.
  `hand = "both"` calls `detect(left, right)` once with both hands (only when both are tracked) and
  events carry `both`. An untracked hand is never passed to a one-hand detector.
- Do the reaction in `gesture.on`, not in the detector.
- Motion: use `h.vel` (m/s in the head's frame: x right, y up, z forward). 1.0 m/s is a brisk move;
  ask for a small cross-axis speed too (`math.abs(h.vel.y) < 0.7`) to reject diagonal arm swings.
- Short-lived state (waves, circles, "armed" flags) lives in locals; anything that must survive a
  restart goes in `store.set(key, string)` / `store.get(key)`.
- Never use a gesture that the system or the UI already owns as a trigger by itself:
  `pinch` (clicks), `grab`/fist (moves panels, spins the carousel), `point` (aims the ray), and
  "palm up + pinch" (the Horizon OS menu). Combine them with something distinctive instead.
- Destructive actions (closing everything, `run:` commands that change files) go through a
  confirmation: `ui.modal(title, text, {"DO IT", "CANCEL"}, function(i) if i == 1 then ... end end)`.

## 4. What reactions can do (summary; full list in reference/lua-api.md)

`ui.toast(text, kind)` · `ui.open/close/toggle(panel)` · `ui.modal(...)` · `ui.select(on)` ·
`terminal.run(cmd)` · `terminal.send(text)` · `terminal.new()` · `browser.open(url)` ·
`keyboard.open()` · `settings.get/set(key, value)` · `app.action("verb:arg")` · `app.after(s, fn)` ·
`app.every(s, fn)` · `store.get/set` · `gesture.emit(name, hand, phase)` · `voice.listen()` ·
`voice.say(text)` · `voice.command{...}` (the same plugin can add voice commands: complete, with `samples`
(English: voice is English only); skill `quest-scripting`, "MUST: every command complete") · `log(...)`

Panels: `dock` `terminals` `files` `git` `web` (the in-app browser window) `browser` (its LINKS page)
`scripts` `gestures` `plugins` `agents` `threads` `thread` `thread-new` `vault` `settings` `voice`. Actions (`app.action`, bindings): `open:` `close:` `toggle:<panel>`,
`toast:<text>`, `run:<command>`, `type:<text>` (`\n` Enter, `\e` Esc, `\x03` Ctrl-C), `url:<url>`,
`layout:fixed|float`, `spin:<degrees>`, `select[:on|off]`, `keyboard`, `terminal:new`, `recenter`,
`gesture:<name>`, `lua:<code>`, `voice:listen|stop|toggle|say:<text>|run:<text>`, `focus[:on|off]`.

## 5. Procedure (do all of it)

0. Review first: tell the user what the plugin will add or change (gestures, reactions, voice commands) and how
   to turn it off, and wait for their OK. The same for changes to an existing plugin.
1. Restate the gesture as a precise shape/motion: which hand(s), which fingers straight or curled,
   where the palm and thumb point, what motion, how long. Pick the mechanism from section 0.
2. `mcx-ui gesture list` and `mcx-ui plugin list`: see what is loaded; pick a new plugin id.
3. Write `plugin.json`, then the pose files and/or `main.lua` (complete files; no placeholders). Every
   gesture your reactions name is a built-in or defined in this plugin (section 0).
4. Within ~1 s the app loads it. Check: `mcx-ui plugin list` → your plugin, `error=-`;
   `mcx-ui plugin log <id>` → `loaded (N handlers, N poses, N detectors)`;
   `mcx-ui gesture list` → your gesture names.
5. Prove the reaction: `mcx-ui gesture <name> right start` simulates the event; the toast/panel
   should happen (`mcx-ui ui list` shows `open=True`). Fix and repeat until clean.
6. Tell the user, in one short message: the gesture to make (in body words), what it triggers, how to
   watch it live (MQH > GESTURES > LIVE shows finger bars and active gestures), how to turn it off
   (CONTROL > PLUGINS, or `mcx-ui plugin disable <id>`), and the plugin folder.

The simulation proves the wiring, not the recognition: real recognition needs the user's hands. When a
pose does not fire for the user, ask them to record it (GESTURES > RECORD makes `my-poses/poses/<name>.json`
from their real hand) and compare its curls with yours; or read `hands.get("right")` through
`mcx-ui script eval 'local h = hands.get("right"); return h.curl.thumb .. " " .. h.palmFacing'`
while they hold it.

## 6. Checklist before you answer

- [ ] Folder `/mnt/quest/plugins/<id>/` with a valid `plugin.json` (JSON parses; id = folder name)
- [ ] Every gesture the plugin reacts to is a built-in or defined in the plugin itself (never assume the
      examples `core-poses` / `swipes` are installed)
- [ ] Every gesture name is unique and lowercase-with-dashes; no clash with built-ins
      (`pinch grab point open palm-up clap select`) unless you mean to react to those
- [ ] Poses: only the fingers that matter are listed; tolerance 0.3-0.4; `hold` 0.1-0.3
- [ ] Detectors: cheap, `hand` set correctly, a cooldown on motion gestures (0.5-1 s)
- [ ] Reactions only on `e.phase == "start"` (or `"tap"`) unless the end matters
- [ ] No reliance on pinch / fist / point / palm-up-pinch alone
- [ ] `mcx-ui plugin list` shows no error; the simulated event produced the reaction
- [ ] The answer tells the user how to do it, how to see it live, and how to turn it off
- [ ] The user saw the change and said OK before it was written (review first)
- [ ] Voice commands the plugin adds are complete: `mcx-ui voice registry` shows them with `warnings: []`, and
      `mcx-ui voice decide "<a sample in your own words>"` picks them; disabling the plugin
      (`mcx-ui plugin disable <id>`) removes them from `mcx-ui voice registry`
- [ ] Nothing stays registered that cannot run: a reaction or command that depends on something switched off
      is removed with it (`voice.remove(name)`, or disable the plugin)

## Related skills

- `quest-scripting`: single-file scripts in `/mnt/quest/scripts` (same Lua API) for quick behaviours.
- `metacodex-context`: the headset, the Linux userland, MQH, the control port, paths and limits.
- Unity / Meta skills bundled here: `hz-immersive-designer` (comfortable, discoverable gestures and
  interaction patterns), `hz-unity-meta-interaction-sdk` and `hz-unity-meta-core-sdk` (how Meta's
  hand tracking, OVRHand / OVRSkeleton joints, poke, pinch and rays work), `hz-unity-meta-quest-ui`
  (panel distances and sizes). Read them for design questions; you still only write the files above.
