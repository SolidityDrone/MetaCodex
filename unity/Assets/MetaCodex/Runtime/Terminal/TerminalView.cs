using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TextCore.LowLevel;

namespace MetaCodex.Terminal
{
    /// Draws a VtTerminal on a world-space panel, local origin at the top-left corner, +x right, -y down.
    /// One TextMeshPro per row (only rows that changed are re-parsed: a spinner costs one row, not the
    /// screen) and one opaque mesh for the panel, cell backgrounds and the cursor. Glyphs are SDF, so text
    /// stays sharp at reading distance; size is set in metres per cell.
    public sealed class TerminalView : MonoBehaviour
    {
        [SerializeField] private Font monoFont;
        [Tooltip("Tried in order for glyphs the mono font lacks (symbols, box/tech characters, emoji)")]
        [SerializeField] private Font[] fallbackFonts;
        [SerializeField] private Material panelMaterial;
        [Tooltip("Metres per row")]
        [SerializeField] private float cellHeight = 0.021f;
        [Tooltip("Cell width / height")]
        [SerializeField] private float cellAspect = 0.55f;
        [SerializeField] private float padding = 0.014f;
        [Tooltip("Glyph em as a fraction of the row height")]
        [SerializeField] private float emFraction = 0.84f;

        public VtTerminal Terminal { get; private set; }
        public float CellWidth => cellHeight * cellAspect;
        public float CellHeight => cellHeight;
        public float Width => (Terminal?.Cols ?? 0) * CellWidth + 2 * padding;
        public float Height => (Terminal?.Rows ?? 0) * cellHeight + 2 * padding;
        /// Lines scrolled back into the history (0 = live screen).
        public int ScrollOffset { get; private set; }
        public float LastRenderMs { get; private set; }
        public TMP_FontAsset Font { get { EnsureFont(); return _font; } }

        private TMP_FontAsset _font;
        private TextMeshPro[] _rows;
        private Mesh _mesh;
        private string _mspace, _mspaceWide;
        private readonly StringBuilder _sb = new StringBuilder(8192);
        private readonly StringBuilder _run = new StringBuilder(512);
        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Color> _colors = new List<Color>();
        private readonly List<int> _tris = new List<int>();
        private List<(int col, int len, int rgb)>[] _rowBg;
        private bool[] _rowNeeds;
        private int _lastCursorRow = -1, _lastCursorCol = -1;
        private bool _lastCursorVisible;
        private long _lastVersion = -1;
        private float _syncSince = -1f;

        /// A new terminal panel made at runtime takes the fonts, material and cell metrics of an existing one.
        public void CopyConfigFrom(TerminalView other)
        {
            monoFont = other.monoFont;
            fallbackFonts = other.fallbackFonts;
            panelMaterial = other.panelMaterial;
            cellHeight = other.cellHeight;
            cellAspect = other.cellAspect;
            padding = other.padding;
            emFraction = other.emFraction;
            _font = other._font; // share the runtime SDF atlas (and its fallbacks)
        }

        public Font MonoFont => monoFont;
        public Font[] FallbackFonts => fallbackFonts;

        public void Attach(VtTerminal terminal)
        {
            Terminal = terminal;
            EnsureFont();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }

            float em = cellHeight * emFraction;
            float fontSize = em / 0.1f; // TextMeshPro (3D): 1 em = fontSize * 0.1 world units
            float mono = CellWidth / em;
            _mspace = $"<mspace={mono:0.####}em>";
            _mspaceWide = $"<mspace={mono * 2f:0.####}em>";

            _rows = new TextMeshPro[terminal.Rows];
            _rowBg = new List<(int, int, int)>[terminal.Rows];
            _rowNeeds = new bool[terminal.Rows];
            for (int r = 0; r < terminal.Rows; r++)
            {
                var go = new GameObject("row" + r);
                go.transform.SetParent(transform, false);
                var t = go.AddComponent<TextMeshPro>();
                t.font = _font;
                t.fontSize = fontSize;
                t.richText = true;
                t.parseCtrlCharacters = false;
                t.textWrappingMode = TextWrappingModes.NoWrap;
                t.overflowMode = TextOverflowModes.Overflow;
                t.alignment = TextAlignmentOptions.TopLeft;
                t.margin = Vector4.zero;
                t.enableAutoSizing = false;
                t.color = Color.white;
                var rt = t.rectTransform;
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(terminal.Cols * CellWidth, cellHeight);
                rt.localPosition = new Vector3(padding, -padding - r * cellHeight, -0.0008f);
                var mr = t.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _rows[r] = t;
                _rowBg[r] = new List<(int, int, int)>();
                _rowNeeds[r] = true;
            }

            var bg = new GameObject("cells");
            bg.transform.SetParent(transform, false);
            _mesh = new Mesh { name = "terminal-cells" };
            _mesh.MarkDynamic();
            bg.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var bgr = bg.AddComponent<MeshRenderer>();
            bgr.sharedMaterial = panelMaterial;
            bgr.shadowCastingMode = ShadowCastingMode.Off;
            bgr.receiveShadows = false;
            _lastVersion = -1;
            ScrollOffset = 0;
        }

        public void ScrollBy(int lines)
        {
            if (Terminal == null || Terminal.AltScreen) { ScrollOffset = 0; return; }
            int v = Mathf.Clamp(ScrollOffset + lines, 0, Terminal.ScrollbackCount);
            if (v == ScrollOffset) return;
            ScrollOffset = v;
            for (int r = 0; r < _rowNeeds.Length; r++) _rowNeeds[r] = true;
        }

        /// Redraws what changed since the last call. Call once per frame after feeding the terminal.
        public void Render()
        {
            var t = Terminal;
            if (t == null) return;
            if (t.AltScreen && ScrollOffset != 0) ScrollBy(-ScrollOffset);
            // a program mid-frame (DECSET 2026) is shown when it ends the frame, or after 0.25 s at most
            if (t.SyncOutput)
            {
                if (_syncSince < 0f) _syncSince = Time.unscaledTime;
                if (Time.unscaledTime - _syncSince < 0.25f) return;
            }
            else _syncSince = -1f;

            bool cursorVisible = t.CursorVisible && ScrollOffset == 0;
            int cr = t.CursorRow, cc = t.CursorCol;
            bool cursorMoved = cr != _lastCursorRow || cc != _lastCursorCol || cursorVisible != _lastCursorVisible;
            if (t.Version == _lastVersion && !cursorMoved && !AnyNeeds()) return;

            float t0 = Time.realtimeSinceStartup;
            if (cursorMoved)
            {
                if (_lastCursorRow >= 0 && _lastCursorRow < _rowNeeds.Length) _rowNeeds[_lastCursorRow] = true;
                if (cr < _rowNeeds.Length) _rowNeeds[cr] = true;
            }
            bool bgChanged = false;
            for (int r = 0; r < t.Rows; r++)
            {
                if (!(_rowNeeds[r] || (ScrollOffset == 0 && t.IsRowDirty(r)))) continue;
                var line = LineAt(r);
                bool cursorHere = cursorVisible && r == cr && ScrollOffset == 0;
                _rows[r].text = BuildRow(line, r, cursorHere ? cc : -1);
                _rowNeeds[r] = false;
                bgChanged = true;
            }
            t.ClearDirty();
            if (bgChanged || cursorMoved) RebuildMesh(cursorVisible ? cr : -1, cc);
            _lastCursorRow = cr;
            _lastCursorCol = cc;
            _lastCursorVisible = cursorVisible;
            _lastVersion = t.Version;
            LastRenderMs = (Time.realtimeSinceStartup - t0) * 1000f;
        }

        private bool AnyNeeds()
        {
            for (int r = 0; r < _rowNeeds.Length; r++) if (_rowNeeds[r]) return true;
            return false;
        }

        private Cell[] LineAt(int r)
        {
            int virt = r - ScrollOffset;
            if (virt >= 0) return Terminal.GetRow(virt);
            return Terminal.GetScrollbackLine(Terminal.ScrollbackCount + virt);
        }

        private string BuildRow(Cell[] line, int r, int cursorCol)
        {
            var bgRuns = _rowBg[r];
            bgRuns.Clear();
            _sb.Clear();
            if (line == null) return "";
            var t = Terminal;
            int cols = Mathf.Min(line.Length, t.Cols);
            int runFg = int.MinValue;
            CellAttr runStyle = CellAttr.None;
            bool runWide = false, open = false;
            int bgStart = -1, bgRgb = 0;

            for (int c = 0; c < cols; c++)
            {
                var cell = line[c];
                if ((cell.Attr & CellAttr.WideTail) != 0) continue;
                bool wide = (cell.Attr & CellAttr.WideHead) != 0;
                bool inverse = ((cell.Attr & CellAttr.Inverse) != 0) ^ t.ReverseVideo;
                bool bold = (cell.Attr & CellAttr.Bold) != 0;
                int fg = t.ResolveRgb(cell.Fg, true, bold);
                int bg = t.ResolveRgb(cell.Bg, false, false);
                bool defaultBg = cell.Bg == VtTerminal.DefaultColor;
                if (inverse) { int x = fg; fg = bg; bg = x; defaultBg = false; }
                if (c == cursorCol && t.CursorShape == CursorShape.Block) { fg = t.DefaultBgRgb; }
                if ((cell.Attr & CellAttr.Dim) != 0) fg = Mix(fg, bg, 0.45f);
                if ((cell.Attr & CellAttr.Hidden) != 0) fg = bg;

                // background runs (the panel colour itself is drawn once, under everything)
                int width = wide ? 2 : 1;
                if (!defaultBg)
                {
                    if (bgStart < 0 || bgRgb != bg) { CloseBg(bgRuns, ref bgStart, c, bgRgb); bgStart = c; bgRgb = bg; }
                }
                else CloseBg(bgRuns, ref bgStart, c, bgRgb);

                CellAttr style = cell.Attr & (CellAttr.Bold | CellAttr.Italic | CellAttr.Underline | CellAttr.Strike);
                if (!open || fg != runFg || style != runStyle || wide != runWide)
                {
                    if (open) CloseRun(runStyle);
                    OpenRun(fg, style, wide);
                    runFg = fg; runStyle = style; runWide = wide; open = true;
                }
                int rune = cell.Rune == 0 ? ' ' : cell.Rune;
                VtTerminal.AppendRune(_run, rune);
                c += width - 1;
            }
            CloseBg(bgRuns, ref bgStart, cols, bgRgb);
            if (open) CloseRun(runStyle);
            return _sb.ToString();
        }

        private static void CloseBg(List<(int col, int len, int rgb)> runs, ref int start, int end, int rgb)
        {
            if (start < 0) return;
            if (end > start) runs.Add((start, end - start, rgb));
            start = -1;
        }

        private void OpenRun(int fg, CellAttr style, bool wide)
        {
            _sb.Append(wide ? _mspaceWide : _mspace);
            _sb.Append("<color=#").Append(fg.ToString("X6")).Append('>');
            if ((style & CellAttr.Bold) != 0) _sb.Append("<b>");
            if ((style & CellAttr.Italic) != 0) _sb.Append("<i>");
            if ((style & CellAttr.Underline) != 0) _sb.Append("<u>");
            if ((style & CellAttr.Strike) != 0) _sb.Append("<s>");
            _run.Clear();
        }

        private void CloseRun(CellAttr style)
        {
            // noparse keeps terminal text such as "<b>" literal; its own closing tag must not appear inside
            _sb.Append("<noparse>").Append(_run.Replace("</noparse>", "‹/noparse>")).Append("</noparse>");
            if ((style & CellAttr.Strike) != 0) _sb.Append("</s>");
            if ((style & CellAttr.Underline) != 0) _sb.Append("</u>");
            if ((style & CellAttr.Italic) != 0) _sb.Append("</i>");
            if ((style & CellAttr.Bold) != 0) _sb.Append("</b>");
            _run.Clear();
        }

        private void RebuildMesh(int cursorRow, int cursorCol)
        {
            _verts.Clear(); _colors.Clear(); _tris.Clear();
            var t = Terminal;
            // panel
            Quad(0f, 0f, Width, Height, 0f, t.ReverseVideo ? t.DefaultFgRgb : t.DefaultBgRgb);
            for (int r = 0; r < _rowBg.Length; r++)
            {
                float y = padding + r * cellHeight;
                foreach (var run in _rowBg[r])
                    Quad(padding + run.col * CellWidth, y, run.len * CellWidth, cellHeight, -0.0002f, run.rgb);
            }
            if (cursorRow >= 0)
            {
                float x = padding + cursorCol * CellWidth, y = padding + cursorRow * cellHeight;
                switch (t.CursorShape)
                {
                    case CursorShape.Block: Quad(x, y, CellWidth, cellHeight, -0.0004f, t.CursorRgb); break;
                    case CursorShape.Underline: Quad(x, y + cellHeight * 0.86f, CellWidth, cellHeight * 0.14f, -0.0004f, t.CursorRgb); break;
                    case CursorShape.Bar: Quad(x, y, CellWidth * 0.14f, cellHeight, -0.0004f, t.CursorRgb); break;
                }
            }
            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0);
            _mesh.RecalculateBounds();
        }

        // x, y measured from the top-left corner, y downwards
        private void Quad(float x, float y, float w, float h, float z, int rgb)
        {
            int i = _verts.Count;
            _verts.Add(new Vector3(x, -y, z));
            _verts.Add(new Vector3(x + w, -y, z));
            _verts.Add(new Vector3(x + w, -y - h, z));
            _verts.Add(new Vector3(x, -y - h, z));
            var c = new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) c = c.linear;
            _colors.Add(c); _colors.Add(c); _colors.Add(c); _colors.Add(c);
            _tris.Add(i); _tris.Add(i + 1); _tris.Add(i + 2);
            _tris.Add(i); _tris.Add(i + 2); _tris.Add(i + 3);
        }

        private static int Mix(int a, int b, float t)
        {
            int Ch(int s) => Mathf.RoundToInt(((a >> s) & 0xFF) * (1 - t) + ((b >> s) & 0xFF) * t);
            return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
        }

        private void EnsureFont()
        {
            if (_font != null) return;
            _font = TMP_FontAsset.CreateFontAsset(monoFont, 64, 8, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            _font.name = monoFont.name + " (runtime SDF)";
            // TextBend = TMP's mobile SDF shader + the panel bend (off here) + real things in front of the text
            var mobile = Resources.Load<Shader>("Shaders/TextBend") ?? Shader.Find("MetaCodex/TextBend") ?? Shader.Find("TextMeshPro/Mobile/Distance Field");
            if (mobile != null) _font.material.shader = mobile;
            var chain = new List<TMP_FontAsset>();
            foreach (var f in fallbackFonts ?? new Font[0])
            {
                if (f == null) continue;
                var fa = TMP_FontAsset.CreateFontAsset(f, 64, 8, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
                fa.name = f.name + " (runtime SDF)";
                if (mobile != null) fa.material.shader = mobile;
                chain.Add(fa);
            }
            _font.fallbackFontAssetTable = chain;
        }

        /// Local panel point (metres from the top-left, y down) -> cell, or (-1,-1) outside.
        public Vector2Int CellAt(Vector3 local)
        {
            int col = Mathf.FloorToInt((local.x - padding) / CellWidth);
            int row = Mathf.FloorToInt((-local.y - padding) / cellHeight);
            if (Terminal == null || col < 0 || row < 0 || col >= Terminal.Cols || row >= Terminal.Rows) return new Vector2Int(-1, -1);
            return new Vector2Int(col, row);
        }
    }
}
