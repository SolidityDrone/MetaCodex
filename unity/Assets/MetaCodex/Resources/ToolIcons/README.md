Tool icons for SYSTEM > TOOLS, made by `tools/make-tool-icons.js` (npm: simple-icons, lucide-static,
@resvg/resvg-js, pngjs):

- logos from Simple Icons (https://simpleicons.org, CC0 1.0 for the SVG data; each logo is a trademark of its owner,
  shown only to name the tool);
- pictograms from Lucide (https://lucide.dev, ISC licence) for tools without a logo there, by what they do: download
  (wget), key (ssh), braces (jq), search (rg), folder search (fd), filter (fzf), hammer (make), bolt (ninja), package
  (cargo), USB (adb), package check (APK build tools), bot (codex), network (mqh).

Every icon is rendered white (only alpha varies, so filtering never darkens its edges), measured (its drawn content
box goes into the index, so the app centres and sizes it exactly), and packed into 128 px cells of a 1024 x 1024
atlas: a power of two, never rescaled on import (the old 1024 x 384 atlas was stretched to 1024 x 512 by Unity, and
the uv maths used the stretched height: logos looked cut and shifted).

`tool-icons-index.json`: atlas size, cell per icon, kind (logo / pictogram), brand colour, content box, and which tool
uses which icon. Regenerate:
`NODE_PATH=<folder with the packages>/node_modules node tools/make-tool-icons.js unity/Assets/MetaCodex/Resources/ToolIcons`
