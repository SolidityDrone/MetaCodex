using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MetaCodex.UI
{
    /// The look of every panel: an original sci-fi "command console" style (cut corners, bevelled glowing
    /// frames, cyan accents on deep navy, amber warnings, condensed technical type). Sizes are metres.
    public sealed class UiTheme
    {
        public static UiTheme Current { get; set; }

        // colours are given in sRGB hex and converted to linear for the vertex-colour shaders
        public Color Accent = Hex(0x35D0FF);
        public Color AccentSoft = Hex(0x2A8FC0);
        public Color Frame = Hex(0x3C7FA6);
        public Color Warn = Hex(0xFFB547);
        public Color Danger = Hex(0xFF5468);
        public Color Ok = Hex(0x4DFFA6);
        public Color Violet = Hex(0xB28CFF);
        public Color Text = Hex(0xD8F3FF);
        public Color TextDim = Hex(0x9CC3D6); // was 7FA9BF: too faint for small text in the headset
        public Color TextAccent = Hex(0x8FEBFF);

        public float HeaderHeight = 0.034f;
        public float Padding = 0.012f;
        public float Gap = 0.006f;
        public float RowHeight = 0.030f;
        public float ButtonHeight = 0.034f;
        public float CutBig = 0.014f;
        public float CutSmall = 0.004f;
        public float Border = 0.0016f;
        public float GlowMargin = 0.010f;

        /// TextMeshPro (3D) font size for a line height in metres: world text is ~0.1 m per 1 pt.
        public float FontSizeFor(float lineHeight) => lineHeight * 10f * 0.72f;
        public float TitleSize = 0.019f;
        public float BodySize = 0.016f;
        public float SmallSize = 0.012f;

        public TMP_FontAsset Display;   // titles, buttons: Orbitron
        public TMP_FontAsset Body;      // labels, lists: Rajdhani
        public TMP_FontAsset Mono;      // paths, code, logs: the terminal font
        public Material ShapeMaterial;  // MetaCodex/SciFiUI
        public Material BeamMaterial;   // MetaCodex/Beam

        public static Color Hex(int rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

        /// Vertex colours go to the shader as they are: convert sRGB design colours when the project is linear.
        public static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

        public static readonly Dictionary<string, int> Accents = new Dictionary<string, int>
        {
            { "cyan", 0x35D0FF }, { "amber", 0xFFB547 }, { "green", 0x4DFFA6 }, { "violet", 0xB28CFF }, { "red", 0xFF5468 },
            { "ice", 0x9FE8FF }, { "magenta", 0xFF5BD8 }, { "gold", 0xFFD34D }, { "white", 0xE8F4FF },
        };

        public void SetAccent(string name)
        {
            if (!Accents.TryGetValue(name ?? "", out int rgb)) rgb = 0x35D0FF;
            Accent = Hex(rgb);
            AccentSoft = Color.Lerp(Accent, Hex(0x0B2233), 0.35f);
            TextAccent = Color.Lerp(Accent, Color.white, 0.45f);
        }

        /// Runtime SDF font asset from a TTF (dynamic atlas: glyphs are added as text needs them), with an
        /// optional fallback chain for symbols.
        public static TMP_FontAsset MakeFont(Font font, IList<Font> fallbacks = null, int size = 72)
        {
            if (font == null) return null;
            var fa = TMP_FontAsset.CreateFontAsset(font, size, 8, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            fa.name = font.name + " (ui SDF)";
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
        public bool Visible;
    }

    public enum UiShapeKind { Panel = 0, Button = 1, Header = 2, Selection = 3, Bar = 4, Line = 5, Dot = 6 }

}
