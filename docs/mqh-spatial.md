# MQH spatial surface: how plugins, gestures and appearance reach Unity (design v0, Nov 7)

Upstream bb plugins draw their UI with React (an `"app"` entry in the plugin's `package.json` `"bb"`
field). MQH removed that web surface entirely (no React, no app bundles; the manifest schema no longer
accepts `"app"`). Unity runs IL2CPP and cannot load code at runtime, so a plugin's UI is **data only**:

```json
"bb": { "name": "...", "server": "./src/server.ts", "spatial": "./spatial.json" }
```

MQH serves `spatial.json` to Unity (`GET /api/v1/plugins/:id/spatial`; every plugin in the plugin list
carries `spatial: { hasSpatial, url }`); Unity renders it with prebuilt widgets and calls back into the
plugin's own server code over RPC. A plugin an agent writes on the headset shows up in VR without an APK.
`mqh plugin new` scaffolds a server, a `spatial.json` panel (list + form bound to RPCs) and a skill.

## spatial.json v0

```json
{
  "version": 1,
  "panels": [
    {
      "id": "questions", "title": "Questions", "icon": "MessageQuestion",
      "placement": "dock",
      "refresh": { "on": ["thread.changed"] },
      "body": [
        { "type": "list", "bind": "rpc:listOpen",
          "item": { "title": "$.question", "subtitle": "$.thread", "badge": "$.status" },
          "onSelect": { "rpc": "open", "args": { "id": "$.id" } } },
        { "type": "row", "body": [
          { "type": "button", "label": "Answer A", "action": { "rpc": "answer", "args": { "choice": "a" } } },
          { "type": "button", "label": "Skip", "action": { "rpc": "skip" } } ] }
      ]
    }
  ],
  "gestures": [ { "gesture": "clap", "when": "panel:questions", "action": { "rpc": "answer", "args": { "choice": "a" } } } ]
}
```

| Field | Values |
|---|---|
| `placement` | `dock` (shelf in front, within reach), `wrist` (left-wrist menu), `float` (free, grab to move), `thread` (attached to a thread's panel) |
| widgets | `text`, `markdown` (subset), `list`, `button`, `toggle`, `form` (JSON Schema subset), `image`, `progress`, `row`, `column`, `divider` |
| `bind` | `rpc:<method>`: the plugin's server RPC, called through MQH; `$.path` picks fields of the result |
| `action` | `{ "rpc": m, "args": {} }`, `{ "thread": "send", "text": "..." }`, `{ "open": "thread:<id>" }` |
| `refresh.on` | MQH change notifications that re-run the binds (`thread.changed`, `plugin.changed`, ...) |

Unity validates the file against a JSON Schema before rendering and shows the error in the panel
instead of failing.

## Gestures

Recognition runs in Unity (hand joints at frame rate); everything else is data in MQH.

- **Built-in gestures** (Unity, `GestureEngine.Names`): `pinch`, `grab`, `point`, `open`, `palm-up`, `clap`,
  `select`. Everything else is a plugin gesture. `thumbs-up`, `thumbs-down`, `victory`, `horns`, `call-me`
  (example `core-poses`) and `swipe-left` / `-right` / `-up` (example `swipes`) are opt-in examples, not
  installed on a fresh headset. Plan, not built: `pinch-hold`, `palm-up-pinch` (push to talk), `fist`,
  `two-hand-push`, `flick-left`, `flick-right`.
- **Plugin gestures**: `gestures.json` in a plugin, pose templates Unity can evaluate:
  `{ "id": "ok-sign", "hand": "any", "fingers": { "thumb": "touching-index", "middle": "extended", "ring": "extended", "pinky": "extended" }, "palm": "any", "holdMs": 250 }`.
- **Bindings** (settings, user-editable, agents can change them): gesture id + context (`global`,
  `panel:<id>`, `thread`) -> an action (MQH command or plugin RPC). Unity sends
  `{ gesture, hand, target: { panel, thread, region } }`; MQH resolves the binding and runs the action.

This replaces bb's *Keyboard* settings page: hands only, no controller or gamepad mapping.

## Appearance

A `quest.appearance` settings group owned by MQH, applied live by Unity: panel distance and height, text
scale, theme colours, passthrough dimming, which panels open at start. Agents and the CLI can change it
like any setting.

## Agent skills that ship with MQH

| Skill | Teaches |
|---|---|
| plugin authoring (bb's, rebranded) | plugin layout, server code, the SDK |
| `mqh-spatial-panels` | `spatial.json`: widgets, binds, actions, validation, preview |
| `mqh-gestures` | pose templates, bindings, testing a gesture without wearing the headset |
| `mqh-appearance` | the appearance keys and safe ranges (comfort) |
| skill creator (bb's, rebranded) | writing and activating skills |

## Later: an MQH MCP server for the spatial side

Tools that let agents see and touch the user's space: `quest.screenshot(panel?)`, `quest.listPanels()`,
`quest.highlight(panel, region)`, `quest.say(text)`, `quest.notify(...)`. Needed for the multimodal
pipeline in `mqh.md`; shipped as an MQH plugin that registers the server for every thread.
