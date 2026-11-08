// Tool icons for MetaCodex SYSTEM > TOOLS: Simple Icons (CC0) rendered white, 128 px cells, one atlas + index.
const fs = require('fs');
const si = require('simple-icons');
const { Resvg } = require('@resvg/resvg-js');
const map = { git: 'git', gh: 'github', node: 'nodedotjs', npm: 'npm', pnpm: 'pnpm', bun: 'bun', deno: 'deno',
  python3: 'python', pip: 'pypi', uv: 'uv', rustc: 'rust', cargo: 'rust', go: 'go', gcc: 'gnu', clang: 'llvm',
  make: 'gnu', cmake: 'cmake', java: 'openjdk', ruby: 'ruby', php: 'php', lua: 'lua', sqlite3: 'sqlite',
  tmux: 'tmux', vim: 'vim', nano: 'gnu', curl: 'curl', wget: 'gnu', claude: 'claude' };
const cell = 128, pad = 14, out = process.argv[2] || 'out';
fs.mkdirSync(out, { recursive: true });
const slugs = [...new Set(Object.values(map))];
const index = { cell, columns: 8, icons: {}, tools: {} };
slugs.forEach((slug, i) => {
  const ic = si['si' + slug.charAt(0).toUpperCase() + slug.slice(1)];
  const svg = ic.svg.replace('<svg ', '<svg fill="#FFFFFF" ');
  const png = new Resvg(svg, { fitTo: { mode: 'width', value: cell - 2 * pad }, background: 'rgba(0,0,0,0)' }).render().asPng();
  fs.writeFileSync(`${out}/${String(i).padStart(2, '0')}-${slug}.png`, png);
  index.icons[slug] = { cell: i, hex: ic.hex, title: ic.title };
});
for (const [tool, slug] of Object.entries(map)) index.tools[tool] = slug;
fs.writeFileSync(`${out}/index.json`, JSON.stringify(index, null, 1));
console.log(slugs.length, 'icons');
