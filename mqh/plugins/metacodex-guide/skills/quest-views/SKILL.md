---
name: quest-views
description: "Show things to the user in the MetaCodex headset as floating views instead of walls of text - charts (bar, line, area, scatter, donut), diagrams (flowcharts, sequence diagrams: a Mermaid subset), tables, stats, progress, checklists, timelines, kanban boards, code and diffs, images, choices the user taps to answer, and small Unity physics scenes. Use when a picture beats prose - \"show me\", \"visualize\", \"diagram\", \"chart\", \"compare\", \"plan\", \"architecture\", \"progress\", \"which option\", \"simulate\" - or when you want the user to pick between options, and when a script should push live data to a panel."
---

# Quest views: draw in the headset

The user reads your messages in VR. A view is a panel the app draws next to the conversation; it is
easier to take in than text. Two ways to show one, same spec:

1. **In your reply** (threads): a fenced block. The app renders it when the message arrives and lists it
   in the thread's workspace (VIEWS). Keep a one-line text answer around it.

   ````
   ```mcx-view
   {"title": "Build times", "components": [
     {"type": "chart", "kind": "bar", "labels": ["mon", "tue", "wed"], "series": [{"name": "ms", "data": [41, 38, 29]}]}
   ]}
   ```
   ````

   or plain Mermaid (flowchart, sequenceDiagram, pie):

   ````
   ```mermaid
   graph LR
     A[Parse] --> B{Valid?}
     B -->|yes| C[Run]
     B -->|no| D[Report]
   ```
   ````

2. **From a shell** (live data, scripts, waiting for an answer):
   `mcx-ui view show spec.json` (a file, or `-` for stdin), `mcx-ui view mermaid diagram.mmd`,
   `mcx-ui view list`, `mcx-ui view close <id>`, `mcx-ui view schema` (the component list),
   `mcx-ui view wait <id>` (blocks until the user taps a choice or sends an input; prints `answer <text>`).

The JSON must be valid (no comments, no trailing commas, numbers as numbers). Reuse an `"id"` to replace a
view in place (a chart you update as work progresses). At most 16 components per view.

## The spec

```json
{"id": "perf", "title": "Frame time", "columns": 1, "size": [0.6, 0.45], "components": [ ... ]}
```

`columns` 2 puts components side by side (diagrams, tables, boards, code, timelines and scenes still take a
full row; `"wide": false` keeps one in a column); `size` (metres) is optional: the app sizes the panel to its
content. A single component object (with `"type"`) is accepted as a whole view.

| type | fields | use |
|---|---|---|
| `text` | `text` (markdown-lite: `# heading`, `- bullet`, `**bold**`, `` `code` ``) | a short note |
| `stats` | `items: [{label, value, delta?, tone?}]` (up to 4 per row) | headline numbers |
| `chart` | `kind: bar\|hbar\|line\|area\|scatter\|donut\|pie\|sparkline`, `labels`, `series: [{name, data}]` or `data` (one series) or `values: {label: n}`; `unit`, `min`, `max`; scatter: `series: [{name, points: [[x,y],...]}]` | trends, comparisons, shares |
| `graph` | `mermaid: "graph TD; ..."` or `direction: TD\|LR`, `nodes: [{id, label, shape: rect\|round\|diamond\|circle, tone}]`, `edges: [{from, to, label, style: solid\|dotted\|thick}]` | architecture, flows, dependencies |
| `mermaid` | `text` (flowchart / sequenceDiagram / pie) | same, written as Mermaid |
| `table` | `columns: [..]`, `rows: [[..], ..]` (or row objects) | lists with columns (scrolls) |
| `code`, `diff`, `log` | `text` (`diff`: a unified diff, coloured), `title` | snippets, changes |
| `progress` | `items: [{label, value (0..1 or %), tone}]` | task progress |
| `checklist` | `items: [{text, done}]` or `"[x] text"` strings | plans, todo |
| `timeline` | `items: [{time, text, tone}]` | what happened when |
| `kanban` | `columns: [{title, cards: ["text" or {text, tone}]}]` | work by state |
| `kv` | `items: [{key, value, tone}]` or an object | properties |
| `choices` | `prompt`, `options: ["A", "B"]` or `[{label, value}]` | ask the user to pick |
| `input` | `prompt`, `placeholder` | ask for a short text |
| `buttons` | `items: [{id, label, tone}]` (1-12; `tone: accent` = the main one) | a few actions to tap |
| `toggle` | `id`, `label`, `value` (true/false) | an on / off answer |
| `grid` | `columns` (1-7), `cells: [{id, text, sub, tone}]` (1-49; a cell without `id` is not tappable) | a month, a launcher, a board |
| `image` | `path` (PNG/JPG in Linux: `/root/...`, `/mnt/quest/...`) | anything you can render to a file (matplotlib, graphviz) |
| `scene3d` | `size: [w, d]`, `gravity: [0,-9.81,0]`, `walls`, `timeScale`, `bodies: [{shape: sphere\|box\|capsule\|cylinder, size, pos: [x,y,z], vel, spin, mass, color, bounce, friction, static, count, spread}]` | physics demos: the user pushes bodies with a fingertip |

A `text` with `style: "big"` is one short centred line in large type (a number, a clock).
Every component takes an optional `title` and `height` (metres). Tones: `ok`, `warn`, `error`, `accent`,
`dim`, or `#RRGGBB`. Chart series get a fixed, colour-blind-safe palette in order: keep to 4 series or fewer
on line/bar charts and 3 on scatter plots; past 8, fold the rest into "other".

## Answers come back to you

A `choices` tap or an `input` sent arrives **as the user's next message in this thread** (the option's
`value`, else its label; a `buttons` item: its label; a `toggle`: `"<label>: on"` or `"off"`; a `grid` cell:
its text). From a shell, `mcx-ui view wait <id>` returns it instead. Ask one question per
view.

## Mermaid subset

- `graph` / `flowchart` with `TD`, `TB`, `LR`, `RL`, `BT`; nodes `A[rect]`, `A(round)`, `A([stadium])`,
  `A((circle))`, `A{decision}`, `A[(db)]`, `A[[sub]]`; edges `-->`, `---`, `-.->`, `==>`, `-->|label|`,
  `-- label -->`; chains `A --> B --> C`; `A & B --> C`. `subgraph`, `classDef`, `style`, `click` are
  ignored (no frames, no custom colours: use JSON `nodes` with `tone` for colour).
- `sequenceDiagram`: `participant A as Alice`, `A->>B: text`, `A-->>B: reply` (dashed),
  `Note over A,B: text`; `loop` / `alt` blocks are flattened.
- `pie [title X]` with `"label" : value` lines.

Keep diagrams under ~25 nodes: the panel scales a larger graph down until the labels are too small.

## Examples

Plan with progress and a decision:

```mcx-view
{"id": "plan", "title": "Refactor plan", "columns": 2, "components": [
  {"type": "checklist", "title": "Steps", "items": [{"text": "Extract parser", "done": true}, {"text": "Add tests", "done": false}]},
  {"type": "progress", "title": "Coverage", "items": [{"label": "parser", "value": 0.82}, {"label": "runtime", "value": 0.41, "tone": "warn"}]},
  {"type": "choices", "prompt": "Start with?", "options": ["tests first", "parser first"]}
]}
```

A physics demo (balls dropped on boxes):

```mcx-view
{"id": "drop", "title": "Drop test", "components": [
  {"type": "scene3d", "size": [0.5, 0.3], "bodies": [
    {"shape": "sphere", "size": 0.03, "pos": [0, 0.25, 0], "count": 12, "spread": 0.15, "bounce": 0.8},
    {"shape": "box", "size": [0.08, 0.04, 0.08], "pos": [0.1, 0.02, 0], "static": true, "color": "#D95926"}
  ]}
]}
```

A window that stays, with its own button in the menu and its own logic (a timer, a calendar, a tool), is a
plugin, not a view: skill `quest-plugins` (same components, events go to Lua).

## Rules

- Prefer one clear view over several; put the key number or answer in the text too (the user may not
  look at the panel).
- Do not put secrets in views. Do not draw views for every message: when the user asks, or when it
  genuinely shows something prose cannot (structure, trends, many numbers, a choice).
- The app runs at 72 fps on a phone chip: tables up to ~200 rows, scenes up to ~150 bodies.
