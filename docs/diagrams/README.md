# MetaCodex diagram kit

Each diagram is one HTML file on a fixed 1600 x 900 px canvas. The kit (`kit.css`, `kit.js`) lays out the
components, draws the connectors from the real element boxes, runs a lint, and `render.sh` writes a dark and a light PNG
(3200 x 1800) to `docs/assets/diagrams/`.

```
docs/diagrams/
  kit.css  kit.js          the kit (styles, icons, layout, connector router, lint)
  banned-words.txt         brand rule word list used by the lint
  render.sh                lint + PNG render (Windows Chrome headless, run from WSL)
  src/<name>.html          one file per diagram; _sample.html uses everything; _icons.html lists the icons
  fonts/                   Rajdhani, Inter, DejaVu Sans Mono + licences (see fonts/LICENSES.md)
```

## A minimal diagram

```html
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Two boxes</title>
<link rel="stylesheet" href="../kit.css"><script src="../kit.js"></script></head>
<body>
<div id="canvas">
  <header class="titlebar" data-title="Two boxes" data-caption="One line that says what the picture shows"></header>

  <div class="card" id="a" data-tone="app" data-icon="headset" data-title="Shell" data-sub="main process"
       data-x="80" data-y="200" data-w="360">
    <p>Draws the panels.</p>
    <div class="chips"><span class="chip">OpenXR</span></div>
  </div>
  <div class="card" id="b" data-tone="svc" data-icon="process" data-title="Host" data-x="720" data-y="200" data-w="360">
    <p>Owns the Linux userland.</p>
  </div>
</div>
<pre id="lint"></pre>
<script>
  connect('a', 'right', 'b', 'left', { label: 'Binder', style: 'data' });
</script>
</body></html>
```

Rules for the file: keep `<div id="canvas">`, `<pre id="lint"></pre>` and the two includes; call `connect(...)` in a script after the canvas.
Start from `src/_sample.html`.

## Layout

- **Canvas** 1600 x 900. Keep every component at least 16 px inside the edge (the lint enforces it).
  The title bar uses y 28 to 108. Start content at y 132.
- **Position**: `data-x`, `data-y` (px, relative to the parent's top-left corner; the element becomes absolute), `data-w`, `data-h`.
  Without `data-h` the height follows the content. Use whole pixels.
- **Flow layout** (no coordinates): `data-layout="row|col|grid"`, `data-cols="3"` (grid), `data-gap="16"`, `data-align="start|center|end|stretch"`,
  `data-justify="..."`. Plain wrappers: `<div class="row">` / `<div class="col">`. In a row or column, `class="fill"` or
  `data-flex="2"` shares the free space. `data-gap` works on any element, also on a group.
- A group with `data-label` reserves 46 px at the top for its label. With absolute children, the first usable y inside the group is 54.
- **Spacing that works**: 14 to 24 px between cards in a group, 40 px or more between groups.
  A connector label needs a straight stretch about as long as the label plus 40 px. If the gap is too small, the lint reports `label-unplaced`: widen the gap.
- Text rules: body text is 15 px, nothing may be below 13 px. Do not set `font-size` smaller.

## Components

All components accept `id`, `data-tone`, and the position attributes above.

| Component | Markup | Options |
|---|---|---|
| Title bar | `<header class="titlebar" data-title data-caption data-tag>` | `data-tag` is an optional chip on the right |
| Card | `<div class="card" data-icon data-title data-sub data-status data-badge data-variant>` ... body ... | Body: `<p>`, `<p class="dim">`, `<ul class="lines"><li>`, `<code>`, `<div class="sep">`, `<div class="chips">`, `<dl class="kv">`, or nested `.card` / `.row`. `data-status="ok|warn|error"` adds a status dot. `data-badge="1"` adds a number circle in the head. `data-variant="hero"` adds a glow; `"ghost"` gives a dashed outline (automatic for tone `ext`). |
| Group frame | `<section class="group" data-label data-sub data-icon data-variant>` | Translucent frame with a label tab. Holds cards and other groups. `data-variant="dashed"` for a dashed frame. |
| Chip | `<span class="chip">`, `chip mono`, `chip solid`, `chip ok|warn|err|plain`, `data-tone` on the chip | Shortcut: `data-chips="OpenXR,Vulkan,~/work,ok:Passed"` on a card (a name with `/ . \ _` becomes mono; `tone:Label` sets the tone) |
| Step badge | `<div class="badge" data-tone data-x data-y>1</div>` | Or `data-badge` on a card, or `badge:'1'` on a connector |
| Callout note | `<div class="note" data-tone data-icon>text</div>` | `<b>` for emphasis |
| Metric tile | `<div class="metric" data-tone data-value="72" data-unit="fps" data-caption="...">` | |
| Legend | `<div class="legend" data-title="Legend" data-tones="app,svc,sec" data-lines="data,control,secret,async">` | `data-tones` and `data-lines` take `key` or `key:Label`. `data-layout="col"` stacks the items. Chips can be added as children. |
| Icon | `data-icon="name"` on a card, group or note, or `<i data-icon="name"></i>` inline | Names: cpu process thread lock key shield mic waveform model brain plug puzzle hand terminal package gear folder branch headset loop file code globe database bolt eye user speaker check warn x camera clock link cloud server download wifi layers sparkle (see `src/_icons.html`) |

### Colour tokens (`data-tone`, inherited by children)

| Tone | Layer | Dark | Light |
|---|---|---|---|
| `os` | OS / platform | #96abc9 | #55698a |
| `app` | app main process | #35d0ff | #0a8fc0 |
| `svc` | background service process | #2ee6a6 | #0b9d6d |
| `linux` | Linux userland | #cbe34c | #6f8a0c |
| `ai` | interface AI (local models) | #ab8dff | #6a46dc |
| `agent` | coding-agent harness | #ff77d6 | #c0238f |
| `sec` | security / vault | #ffb547 | #b86e00 |
| `native` | native apps built on the headset | #ff8160 | #d24424 |
| `ext` | external / user | #bcc6d6 | #5a687c |
| `neutral` | no layer | #8da2bf | #55698a |
| `ok` `warn` `err` | status | #3ddc97 #ffc23d #ff5c70 | #138a57 #a96b00 #c9263c |

CSS variables: `--c-<tone>`, `--ok --warn --err`, `--tx1 --tx2 --tx3` (text), `--l-data --l-control --l-secret --l-async` (lines).
Use a tone on a group and its cards inherit it; set another tone on a card to break out (a vault card inside a Linux group).

## Connectors

```js
connect(fromId, 'right', toId, 'left', { label: 'API · token', style: 'secret', arrow: 'end', badge: '2', via: [{ x: 800 }] });
```

**Sides**: `top | right | bottom | left`, with an optional position on that side:
`right@0.3` (fraction), `right@30%`, `right@+20` (px from the centre; `-20` the other way), `right@40px` (px from the start edge).
Positions are kept 18 px away from the corners. When two facing sides have no position, the kit picks one coordinate inside both
elements so the line runs straight (`straight: false` turns this off).

| Option | Values | Meaning |
|---|---|---|
| `style` | `data` (solid cyan), `control` (dashed), `secret` (dotted amber), `async` (dash-dot, open arrow), `note` (thin dotted leader, no arrow) | colour and dash pattern, so it also reads in grey scale |
| `arrow` | `end` (default), `both`, `none` | arrow heads; ends without an arrow get a small port dot |
| `label` | text | a pill on the path, never over a component. `mono: true` for code text |
| `badge` | text | a numbered circle, on its own or inside the label pill |
| `via` | `[{x: 800}]`, `[{y: 120}]`, `[[x, y], ...]` | force the route through a vertical lane `x`, a horizontal lane `y`, or points. Legs alternate horizontal / vertical. Without `via` the router finds the shortest path with few bends around all components and other connectors. |
| `labelAt` | 0 to 1 | prefer a label position along the path |
| `stub` | px | length of the straight piece leaving an element (default 22, shorter in tight gaps) |

Lines are orthogonal with 9 px rounded corners, snapped to whole pixels. The start and end points are on the element edges (checked by the lint).
Connect to an element id; groups and nested cards work too. Do not connect through a card: use `via`, or move things.

## Render and lint

```bash
docs/diagrams/render.sh _sample        # one diagram
docs/diagrams/render.sh all            # every src/*.html (names starting with _broken are skipped)
docs/diagrams/render.sh x --debug      # also writes ~/mcxwork/readme/kit/debug/x-debug.png: offenders outlined, 8 px grid
```

For each diagram and both themes it prints the lint report and writes `docs/assets/diagrams/<name>-dark.webp` and `<name>-light.png`.
Exit code 0 = clean, 1 = lint problems, 2 = tool failure. A diagram is done only when it ends with `ALL CLEAN` and you have looked at both PNGs.

To look at a page by hand: `?theme=dark|light`, `?debug=1` (outline offenders), `?guides=1` (grid and safe area). The report is in `<pre id="lint">`
(JSON: `ok`, `problems`, `stats`, `issues[] = {check, msg, el}`).

### What the lint checks

| `check` | Problem |
|---|---|
| `overflow`, `text-outside` | content larger than its box (scroll size), or real text pixels outside the component |
| `escapes-parent` | a component sticks out of its parent component |
| `overlap`, `label-overlap` | sibling components, or connector labels, closer than 4 px |
| `endpoint` | a connector end is more than 0.5 px off the live edge of its element |
| `connector-crosses`, `route-failed`, `label-unplaced` | a line runs through a component, no route found, no room for a label |
| `outside-canvas` | anything outside the canvas or within 16 px of its edge |
| `small-text` | text below 13 px |
| `banned-word` | a word from `banned-words.txt` (case-insensitive, whole words, also in `title`, `alt`, `aria-label`) |
| `font`, `unknown-icon`, `unknown-id`, `bad-anchor`, `duplicate-id`, `js-error` | broken setup |

## Rules

1. **Brand rule.** No company or product names, no third-party logos. Open-source and standard names are fine:
   Android, Linux, Debian, arm64, proot, OpenXR, Vulkan, Lua, git, Node.js, TypeScript, C#, WebView, MCP, ACP.
   `banned-words.txt` holds the banned list. Note that it also catches ordinary words such as "edge" and "cursor": write "border" or "pointer".
2. **Local models only in the local-models diagrams**, with a per-diagram allowlist in `<head>`:
   `<meta name="lint-allow-words" content="Whisper,Qwen3,e5">` (comma list; allows exactly those entries of the banned list).
   Never add it to another diagram.
3. **Minimum text size 13 px**, both themes, no exceptions. Do not shrink text to make things fit: make the box bigger or cut words.
4. **Both themes** are always rendered; check both PNGs. Use tokens (`data-tone`, `var(--tx2)`), never fixed colours.
5. **Not UML, not a flowchart.** Use groups for layers, tones for meaning, metrics and notes for facts. Few connectors, each with a short label (3 words).
6. Write the caption in plain short sentences.

## Use in the README

```html
<picture><source media="(prefers-color-scheme: dark)" srcset="docs/assets/diagrams/x-dark.webp"><img alt="..." src="docs/assets/diagrams/x-light.webp"></picture>
```

## Notes on headless Chrome (WSL)

`render.sh` runs Windows Chrome (`/mnt/c/Program Files/Google/Chrome/Application/chrome.exe`) with `--headless=new`, one instance at a time, with a throw-away profile
in `C:\Users\pnncl\AppData\Local\Temp\mcx-diagram-chrome` (deleted after each run). Flags that matter:

- `--allow-file-access-from-files`: the page reads `../banned-words.txt` from `file://`. Without it the lint reports `banned-words`. When you open a page by hand, run `python3 -m http.server` in `docs/diagrams` instead.
- `--virtual-time-budget=8000`: Chrome waits for fonts, layout and the lint before it dumps the DOM or takes the screenshot. The kit uses timers, not `requestAnimationFrame` (rAF can stall under virtual time).
  `render.sh` retries the lint run up to 3 times if the page did not finish (`data-kit` attribute missing).
- `--force-device-scale-factor=2` with `--window-size=1600,900` gives 3200 x 1800.
- Paths go through `wslpath -w`. One render of a diagram (2 themes, lint + PNG) takes about 15 s.

## Known limits

- Routing is orthogonal and greedy per connector in order of declaration; very dense diagrams may need `via` or `@` positions.
- A label needs a straight stretch of path; very short connectors cannot carry a label.
- Parallel connectors that leave the same side point share their first 22 px: give them different `@` positions.
- The lint measures geometry and text boxes; it cannot judge whether a picture is clear. Look at the PNGs.
- Fonts have no italics or bold in mono; `font-synthesis` is off.

## Output format: WebP for the README

`render.sh` writes the PNG (3200 × 1800, for checks) and then `to-webp.py` writes a WebP next to it
(2400 × 1350, quality 92). The README uses only the WebP files:

- they are about 8 times smaller than the PNGs and look the same;
- the repo sends `*.png` to git LFS, and LFS bandwidth would limit how often the README images load.

The PNGs are in `.gitignore`. Run `render.sh <name>` again after any change; it writes both files.
