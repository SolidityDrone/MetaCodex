using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MetaCodex.UI
{
    /// The look of every panel: a calm, standard style (rounded corners, dark frosted glass, a fine border lit
    /// from above, soft shadows, one blue accent, Inter type). Sizes are metres.
    public sealed class UiTheme
    {
        public static UiTheme Current { get; set; }

        // colours are given in sRGB hex and converted to linear for the vertex-colour shaders
        public Color Accent = Hex(0x5B9BFF);
        public Color AccentSoft = Hex(0x5B9BFF);
        public Color Frame = Hex(0x5B6475);
        public Color Warn = Hex(0xF5B841);
        public Color Danger = Hex(0xF2555A);
        public Color Ok = Hex(0x4CD787);
        public Color Violet = Hex(0xA98BFF);
        public Color Text = Hex(0xEEF1F6);
        public Color TextDim = Hex(0xA7AFBC); // readable at small sizes in the headset
        public Color TextAccent = Hex(0xA9C8FF);

        public float HeaderHeight = 0.036f;
        public float Padding = 0.013f;
        public float Gap = 0.007f;
        public float RowHeight = 0.030f;
        public float ButtonHeight = 0.034f;
        /// Corner radius of panels (buttons use half); the shape field keeps its old name.
        public float CutBig = 0.016f;
        public float CutSmall = 0f;
        public float Border = 0.0009f;
        /// Room around a shape for its soft shadow.
        public float GlowMargin = 0.014f;

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
        public static readonly Dictionary<string, int> Accents = new Dictionary<string, int> { { "blue", 0x5B9BFF } };

        public void SetAccent(string name)
        {
            if (!Accents.TryGetValue(name ?? "", out int rgb)) rgb = 0x5B9BFF;
            Accent = Hex(rgb);
            AccentSoft = Accent;
            TextAccent = Color.Lerp(Accent, Color.white, 0.45f);
        }

        private static readonly HashSet<string> KeepUpper = new HashSet<string>
        {
            "AI", "MQH", "LLM", "TTS", "STT", "API", "URL", "ID", "OK", "UI", "PTY", "SSH", "MB", "GB", "VR", "EN", "IT", "TEE", "ENV", "PIN", "RAM", "FPS", "CPU", "GPU",
        };

        /// UPPERCASE labels written in code shown in Title Case ("INSTALL MODELS" -> "Install Models"), keeping
        /// acronyms; text with lower-case letters is left alone.
        public static string Pretty(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
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
            var mobile = Shader.Find("TextMeshPro/Mobile/Distance Field");
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

    public enum UiShapeKind { Panel = 0, Button = 1, Header = 2, Selection = 3, Bar = 4, Line = 5, Dot = 6 }

}
