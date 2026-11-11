using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MetaCodex.UI
{
    /// The look of every panel: a sci-fi command console (shader MetaCodex/SciFiUI): chamfered frames, holographic
    /// deep-navy glass, steel rims, cyan energy edges; Rajdhani for titles and buttons (capitals, spaced), Inter
    /// for text. One built-in theme. Sizes are metres.
    public sealed class UiTheme
    {
        public static UiTheme Current { get; set; }

        // colours are given in sRGB hex and converted to linear for the vertex-colour shaders
        public Color Accent = Hex(0x35D0FF);      // energy cyan
        public Color AccentSoft = Hex(0x2A9FD6);
        public Color Frame = Hex(0x5F7C9C);       // steel
        public Color Warn = Hex(0xFFB547);
        public Color Danger = Hex(0xFF4D5E);
        public Color Ok = Hex(0x46F28C);
        public Color Violet = Hex(0xB28CFF);
        public Color Text = Hex(0xE2F4FF);
        public Color TextDim = Hex(0x9DBDD2);     // readable at small sizes in the headset
        public Color TextAccent = Hex(0x8FEBFF);

        public float HeaderHeight = 0.040f;
        public float Padding = 0.014f;
        public float Gap = 0.007f;
        public float RowHeight = 0.030f;
        public float ButtonHeight = 0.034f;
        /// The big corner cut of panels (top-left, bottom-right; buttons use about half) and the small one.
        public float CutBig = 0.020f;
        public float CutSmall = 0.005f;
        public float Border = 0.0014f;
        /// Room around a shape for its contact shadow and accent halo.
        public float GlowMargin = 0.016f;

        /// TextMeshPro (3D) font size for a line height in metres: world text is ~0.1 m per 1 pt.
        public float FontSizeFor(float lineHeight) => lineHeight * 10f * 0.72f;
        public float TitleSize = 0.019f;
        public float BodySize = 0.016f;
        public float SmallSize = 0.012f;

        public TMP_FontAsset Display;   // titles, buttons: Inter SemiBold
        public TMP_FontAsset Body;      // labels, lists: Inter Medium
        public TMP_FontAsset Mono;      // paths, code, logs: the terminal font
        public Material ShapeMaterial;  // MetaCodex/SciFiUI
        public Material BeamMaterial;   // MetaCodex/Beam

        public static Color Hex(int rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

        /// Vertex colours go to the shader as they are: convert sRGB design colours when the project is linear.
        public static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

        /// One accent for now (a standard appearance); kept as a table so a setting can return later.
        public static readonly Dictionary<string, int> Accents = new Dictionary<string, int> { { "blue", 0x35D0FF } };

        public void SetAccent(string name)
        {
            if (!Accents.TryGetValue(name ?? "", out int rgb)) rgb = 0x35D0FF;
            Accent = Hex(rgb);
            AccentSoft = Color.Lerp(Accent, Hex(0x0B2233), 0.22f);
            TextAccent = Color.Lerp(Accent, Color.white, 0.45f);
        }

        private static readonly HashSet<string> KeepUpper = new HashSet<string>
        {
            "AI", "MQH", "LLM", "TTS", "STT", "API", "URL", "ID", "OK", "UI", "PTY", "SSH", "MB", "GB", "VR", "EN", "IT", "TEE", "ENV", "PIN", "RAM", "FPS", "CPU", "GPU",
        };

        /// Labels written in capitals stay in capitals: the console look (titles and buttons in the display font,
        /// letter-spaced). Title Case was the 0.5 neutral look.
        public static bool TitleCase = false;

        /// UPPERCASE labels in Title Case when TitleCase is on ("INSTALL MODELS" -> "Install Models"), keeping
        /// acronyms; text with lower-case letters is left alone.
        public static string Pretty(string s)
        {
            if (string.IsNullOrEmpty(s) || !TitleCase) return s;
            bool hasLower = false, hasUpper = false;
            foreach (char ch in s) { if (ch >= 'a' && ch <= 'z') hasLower = true; else if (ch >= 'A' && ch <= 'Z') hasUpper = true; } // icons like ƒ are not words
            if (hasLower || !hasUpper) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (!char.IsLetter(s[i])) { sb.Append(s[i++]); continue; }
                int j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '\'')) j++;
                string w = s.Substring(i, j - i);
                sb.Append(KeepUpper.Contains(w) ? w : w.Substring(0, 1) + w.Substring(1).ToLowerInvariant());
                i = j;
            }
            return sb.ToString();
        }

        private static readonly Dictionary<TMP_FontAsset, Material> BendMaterials = new Dictionary<TMP_FontAsset, Material>();

        /// A copy of a font's material with the bending text shader (for font assets made elsewhere: the terminal's).
        public static Material BendMaterialFor(TMP_FontAsset font)
        {
            if (BendMaterials.TryGetValue(font, out var m) && m != null) return m;
            var sh = Resources.Load<Shader>("Shaders/TextBend") ?? Shader.Find("MetaCodex/TextBend");
            m = new Material(font.material) { name = font.name + " (bend)" };
            if (sh != null) m.shader = sh;
            BendMaterials[font] = m;
            return m;
        }

        /// Runtime SDF font asset from a TTF (dynamic atlas: glyphs are added as text needs them), with an
        /// optional fallback chain for symbols.
        public static TMP_FontAsset MakeFont(Font font, IList<Font> fallbacks = null, int size = 72)
        {
            if (font == null) return null;
            var fa = TMP_FontAsset.CreateFontAsset(font, size, 8, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            fa.name = font.name + " (ui SDF)";
            // no OpenType features at runtime: with Inter's large GPOS table they cost >1 GB as glyphs were added
            // (measured in the Editor); UI labels do not need kerning pairs
            fa.getFontFeatures = false;
            // TMP's mobile SDF shader with the panel bend (UiPanel bends texts on the GPU)
            var mobile = Resources.Load<Shader>("Shaders/TextBend") ?? Shader.Find("MetaCodex/TextBend") ?? Shader.Find("TextMeshPro/Mobile/Distance Field");
            if (mobile != null) fa.material.shader = mobile;
            if (fallbacks != null)
            {
                var chain = new List<TMP_FontAsset>();
                foreach (var f in fallbacks)
                {
                    if (f == null) continue;
                    var fb = TMP_FontAsset.CreateFontAsset(f, size, 8, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
                    fb.name = f.name + " (ui fallback SDF)";
                    fb.getFontFeatures = false;
                    if (mobile != null) fb.material.shader = mobile;
                    chain.Add(fb);
                }
                fa.fallbackFontAssetTable = chain;
            }
            return fa;
        }
    }

    /// One shape drawn by the SciFiUI shader. Position is the centre in the owner's local space (metres).
    public struct UiShape
    {
        public Vector3 Center;
        public Vector2 Size;
        public UiShapeKind Kind;
        public Color Tint;
        public float CutBig, CutSmall, Border, Margin, State;
        /// Radians, counter-clockwise in the panel plane (0: axis-aligned).
        public float Rotation;
        public bool Visible;
    }

    public enum UiShapeKind { Panel = 0, Button = 1, Header = 2, Selection = 3, Bar = 4, Line = 5, Dot = 6, Keycap = 7, Deck = 8 }

}
