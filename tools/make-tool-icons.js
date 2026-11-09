// Tool icons for MetaCodex SYSTEM > TOOLS: one power-of-two atlas + an index the app reads (UiIcons).
//   logos:      Simple Icons (CC0 data; each logo is a trademark of its owner, shown only to name the tool)
//   pictograms: Lucide (ISC) for tools without a logo there, by what they do (download, key, braces, ...)
// Every icon is rendered white (only alpha varies, so filtering never darkens the edges), measured (its real
// content box, so the app centres and sizes it exactly instead of trusting the 24x24 viewBox), and packed into
// 128 px cells of a 1024 x 1024 atlas (a power of two: Unity never rescales it).
// Run from a folder with the packages (npm i simple-icons lucide-static @resvg/resvg-js pngjs):
//   NODE_PATH=<that folder>/node_modules node tools/make-tool-icons.js unity/Assets/MetaCodex/Resources/ToolIcons
const fs = require('fs');
const path = require('path');
const si = require('simple-icons');
const { Resvg } = require('@resvg/resvg-js');
const { PNG } = require('pngjs');

// tool (as in ShellSystem's catalog) -> 'si:<slug>' (logo) or 'lu:<name>' (pictogram)
const map = {
  claude: 'si:claude', codex: 'lu:bot', opencode: 'si:opencode', mqh: 'lu:network',
  node: 'si:nodedotjs', npm: 'si:npm', pnpm: 'si:pnpm', bun: 'si:bun',
  python3: 'si:python', pip: 'si:pypi', uv: 'si:uv',
  gcc: 'si:gnu', clang: 'si:llvm', make: 'lu:hammer', cmake: 'si:cmake', ninja: 'lu:zap', rustc: 'si:rust', cargo: 'lu:package', go: 'si:go',
  ndk: 'si:android', java: 'si:openjdk', adb: 'lu:usb', 'build-tools': 'lu:package-check', glslc: 'si:vulkan',
  git: 'si:git', gh: 'si:github',
  curl: 'si:curl', wget: 'lu:download', ssh: 'lu:key-round', jq: 'lu:braces', rg: 'lu:search', fd: 'lu:folder-search', fzf: 'lu:list-filter',
  tmux: 'si:tmux', sqlite3: 'si:sqlite',
  nano: 'si:nano', vim: 'si:vim', nvim: 'si:neovim',
};

const cell = 128, columns = 8, rows = 8, inner = 104; // the icon is fitted into inner x inner, centred in its cell
const out = process.argv[2] || 'out';
fs.mkdirSync(out, { recursive: true });
const bySlug = {};
for (const k of Object.keys(si)) { const ic = si[k]; if (ic && ic.slug) bySlug[ic.slug] = ic; }
const lucideDir = [...(process.env.NODE_PATH || '').split(':'), path.join(__dirname, 'node_modules')]
  .map(d => path.join(d, 'lucide-static', 'icons')).find(d => d && fs.existsSync(d));
if (!lucideDir) throw new Error('lucide-static not found: set NODE_PATH to the node_modules that has it');

const atlas = new PNG({ width: cell * columns, height: cell * rows });
atlas.data.fill(0);
const index = { cell, columns, rows, width: atlas.width, height: atlas.height, icons: {}, tools: {}, generated: 'tools/make-tool-icons.js' };
const specs = [...new Set(Object.values(map))];
if (specs.length > columns * rows) throw new Error('atlas full: ' + specs.length + ' icons');

specs.forEach((spec, i) => {
  const [src, name] = spec.split(':');
  let svg, hex = null, title = name, kind;
  if (src === 'si') {
    const ic = bySlug[name];
    if (!ic) throw new Error('no Simple Icon ' + name);
    svg = ic.svg.replace('<svg ', '<svg fill="#FFFFFF" ');
    hex = ic.hex; title = ic.title; kind = 'logo';
  } else {
    svg = fs.readFileSync(path.join(lucideDir, name + '.svg'), 'utf8').replace(/<!--[\s\S]*?-->/g, '').replace(/stroke="currentColor"/g, 'stroke="#FFFFFF"');
    kind = 'pictogram';
  }
  const r = new Resvg(svg, { fitTo: { mode: 'width', value: inner }, background: 'rgba(0,0,0,0)' }).render();
  const w = r.width, h = r.height, px = r.pixels; // RGBA, premultiplied: alpha is what we keep
  if (w > inner || h > inner) throw new Error(`${name}: ${w}x${h} does not fit ${inner}`);
  const ox = (i % columns) * cell + Math.floor((cell - w) / 2), oy = Math.floor(i / columns) * cell + Math.floor((cell - h) / 2);
  let x0 = cell, y0 = cell, x1 = -1, y1 = -1;
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      const a = px[(y * w + x) * 4 + 3];
      if (!a) continue;
      const d = ((oy + y) * atlas.width + (ox + x)) * 4;
      atlas.data[d] = 255; atlas.data[d + 1] = 255; atlas.data[d + 2] = 255; atlas.data[d + 3] = a;
      const cx = ox + x - (i % columns) * cell, cy = oy + y - Math.floor(i / columns) * cell;
      if (a > 24) { if (cx < x0) x0 = cx; if (cy < y0) y0 = cy; if (cx > x1) x1 = cx; if (cy > y1) y1 = cy; }
    }
  if (x1 < 0) throw new Error(name + ': empty');
  // the content box in cell units (0..1, y down), one pixel of anti-aliasing around it
  const box = [Math.max(0, x0 - 1) / cell, Math.max(0, y0 - 1) / cell, Math.min(cell, x1 + 2) / cell, Math.min(cell, y1 + 2) / cell].map(v => +v.toFixed(4));
  index.icons[spec] = { cell: i, kind, source: src === 'si' ? 'simple-icons' : 'lucide', name, title, hex, box };
});
for (const [tool, spec] of Object.entries(map)) index.tools[tool] = spec;
fs.writeFileSync(path.join(out, 'tool-icons.png'), PNG.sync.write(atlas));
fs.writeFileSync(path.join(out, 'tool-icons-index.json'), JSON.stringify(index, null, 1) + '\n');
console.log(`${specs.length} icons (${specs.filter(s => s.startsWith('si:')).length} logos, ${specs.filter(s => s.startsWith('lu:')).length} pictograms) -> ${out}`);
