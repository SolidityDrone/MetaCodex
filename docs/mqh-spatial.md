# Plugin panels: the `spatial.json` format (served by MQH, not rendered by the app)

Upstream plugins draw their UI as web components (an `"app"` entry in the plugin's `package.json` `"bb"` field). MQH
removed that web surface (the manifest schema no longer accepts `"app"`), and the app's compiled C# cannot load code
at run time, so a plugin's panel would be **data only**:

```json
"bb": { "name": "...", "server": "./src/server.ts", "spatial": "./spatial.json" }
```

What exists in MQH: the manifest field; the route `GET /api/v1/plugins/:id/spatial`; the field
`spatial: { hasSpatial, url }` in the plugin list; and `mqh plugin new`, which scaffolds a server, a `spatial.json`
panel (a list and a form bound to the server's RPCs) and a skill.

What does not exist: a renderer in the app. No shipped plugin has a panel. To give the headset new windows and menu
tiles while it runs, write an app plugin in Lua instead (`ui.panel`, `menu.add`: the skill `quest-plugins`,
[ui.md](ui.md)).

## The format (version 1)

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

A renderer would validate the file against a JSON Schema first and show the error in the panel instead of failing.
