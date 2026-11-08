Tool icons for SYSTEM > TOOLS: the logos of the tools, from Simple Icons (https://simpleicons.org, CC0 1.0 for
the SVG data; each logo is a trademark of its owner, shown only to name the tool). Rendered white into 128 px
cells by `tools/make-tool-icons.js` (npm: simple-icons, @resvg/resvg-js), then assembled with ImageMagick:
`magick montage <cells> -tile 8x -geometry 128x128+0+0 -background none PNG32:tool-icons.png`.
`tool-icons-index.json`: cell per logo, the brand colour, and which tool uses which logo.
