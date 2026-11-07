using System;
using System.Collections.Generic;
using MetaCodex.Terminal;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace MetaCodex.App
{
    /// Index fingertips of the tracked hands (OVRSkeleton), for poking panels. Empty while hands are not
    /// tracked; the real hands stay visible through passthrough, so no hand mesh is drawn.
    public sealed class HandTips : MonoBehaviour
    {
        [SerializeField] private OVRSkeleton left;
        [SerializeField] private OVRSkeleton right;
        public readonly List<Vector3> Tips = new List<Vector3>(2);

        public void Set(OVRSkeleton l, OVRSkeleton r) { left = l; right = r; }

        private void Update()
        {
            Tips.Clear();
            Add(left);
            Add(right);
        }

        private void Add(OVRSkeleton s)
        {
            if (s == null || !s.IsInitialized || !s.IsDataValid || s.Bones == null) return;
            foreach (var b in s.Bones)
            {
                if ((b.Id == OVRSkeleton.BoneId.XRHand_IndexTip || b.Id == OVRSkeleton.BoneId.Hand_IndexTip) && b.Transform != null)
                {
                    Tips.Add(b.Transform.position);
                    return;
                }
            }
        }
    }

    /// The keys a headset keyboard lacks (Esc, Tab, Ctrl, arrows, ...) as a row of buttons under the
    /// terminal, pressed by poking with an index fingertip. Hands-first: everything in the terminal can be
    /// done with the system keyboard plus this bar.
    public sealed class KeyBar : MonoBehaviour
    {
        [SerializeField] private Material panelMaterial;
        [SerializeField] private float keyHeight = 0.038f;
        [SerializeField] private float gap = 0.006f;

        public TerminalInput Input { get; set; }
        public HandTips Hands { get; set; }
        public event Action<string> Action; // non-key buttons: "keyboard", "paste", "recenter", "scroll-up", ...

        private sealed class Button
        {
            public string Label, Id;
            public TermKey Key;
            public float Width;
            public Transform T;
            public MeshRenderer R;
            public Mesh M;
            public bool Down;
            public float Flash;
            public int Color = -1;
        }

        private readonly List<Button> _buttons = new List<Button>();
        private TMP_FontAsset _font;
        private static readonly int ColorIdle = 0x2A3140, ColorDown = 0x4F7FBF, ColorOn = 0x7A5C2E;

        public void Build(float width, TMP_FontAsset font)
        {
            _font = font;
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }
            _buttons.Clear();
            var spec = new (string label, string id, TermKey key, float w)[]
            {
                ("esc", "esc", TermKey.Escape, 1f), ("tab", "tab", TermKey.Tab, 1f), ("ctrl", "ctrl", TermKey.None, 1.1f),
                ("alt", "alt", TermKey.None, 1f), ("←", "left", TermKey.Left, 0.8f), ("↓", "down", TermKey.Down, 0.8f),
                ("↑", "up", TermKey.Up, 0.8f), ("→", "right", TermKey.Right, 0.8f), ("^C", "ctrl-c", TermKey.None, 0.9f),
                ("⇞", "scroll-up", TermKey.None, 0.8f), ("⇟", "scroll-down", TermKey.None, 0.8f),
                ("paste", "paste", TermKey.None, 1.2f), ("⌨ keys", "keyboard", TermKey.None, 1.7f),
                ("⟲", "recenter", TermKey.None, 0.8f), ("⏎ enter", "enter", TermKey.Enter, 1.8f),
            };
            float units = 0f;
            foreach (var s in spec) units += s.w;
            float unit = (width - gap * (spec.Length - 1)) / units;
            float x = 0f;
            foreach (var s in spec)
            {
                var b = new Button { Label = s.label, Id = s.id, Key = s.key, Width = s.w * unit };
                var go = new GameObject("key-" + s.id);
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(x, 0f, 0f);
                b.T = go.transform;
                b.M = new Mesh();
                SetQuad(b, keyHeight, ColorIdle);
                go.AddComponent<MeshFilter>().sharedMesh = b.M;
                b.R = go.AddComponent<MeshRenderer>();
                b.R.sharedMaterial = panelMaterial;
                b.R.shadowCastingMode = ShadowCastingMode.Off;
                var label = new GameObject("label").AddComponent<TextMeshPro>();
                label.transform.SetParent(go.transform, false);
                label.font = _font;
                label.fontSize = keyHeight * 4.2f;
                label.alignment = TextAlignmentOptions.Center;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                label.rectTransform.sizeDelta = new Vector2(b.Width, keyHeight);
                label.rectTransform.localPosition = new Vector3(b.Width / 2f, -keyHeight / 2f, -0.0008f);
                label.text = s.label;
                label.color = new Color(0.86f, 0.89f, 0.94f);
                _buttons.Add(b);
                x += b.Width + gap;
            }
        }

        private void Update()
        {
            if (Hands == null) return;
            foreach (var b in _buttons)
            {
                bool touching = false;
                foreach (var tip in Hands.Tips)
                {
                    var p = b.T.InverseTransformPoint(tip); // local: x right, y down is negative, -z towards the user
                    bool inside = p.x > -0.004f && p.x < b.Width + 0.004f && p.y < 0.004f && p.y > -keyHeight - 0.004f;
                    if (!inside) continue;
                    if (!b.Down && p.z > -0.006f && p.z < 0.03f) touching = true;
                    if (b.Down && p.z > -0.02f) touching = true; // stays pressed until the finger backs off 2 cm
                }
                if (touching && !b.Down) { b.Down = true; b.Flash = 0.15f; Press(b); }
                else if (!touching && b.Down) b.Down = false;
                if (b.Flash > 0f) b.Flash -= Time.deltaTime;
                int color = b.Down || b.Flash > 0f ? ColorDown : IsOn(b) ? ColorOn : ColorIdle;
                SetQuad(b, keyHeight, color);
            }
        }

        private bool IsOn(Button b) =>
            (b.Id == "ctrl" && Input != null && Input.StickyCtrl) || (b.Id == "alt" && Input != null && Input.StickyAlt) ||
            (b.Id == "keyboard" && Input != null && Input.SystemKeyboardOpen);

        /// Also used by the control port ("press <id>") and by tests.
        public bool PressById(string id)
        {
            foreach (var b in _buttons) if (b.Id == id) { b.Flash = 0.15f; Press(b); return true; }
            return false;
        }

        private void Press(Button b)
        {
            if (Input == null) return;
            switch (b.Id)
            {
                case "ctrl": Input.StickyCtrl = !Input.StickyCtrl; return;
                case "alt": Input.StickyAlt = !Input.StickyAlt; return;
                case "ctrl-c": Input.StickyCtrl = true; Input.TypeText("c"); return;
                case "keyboard": Input.ToggleSystemKeyboard(); return;
                case "paste": Input.Paste(GUIUtility.systemCopyBuffer); return;
            }
            if (b.Key != TermKey.None) Input.PressKey(b.Key);
            else Action?.Invoke(b.Id);
        }

        private static void SetQuad(Button b, float h, int rgb)
        {
            if (b.Color == rgb) return;
            b.Color = rgb;
            var c = new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) c = c.linear;
            float w = b.Width;
            b.M.SetVertices(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(w, 0, 0), new Vector3(w, -h, 0), new Vector3(0, -h, 0) });
            b.M.SetColors(new List<Color> { c, c, c, c });
            b.M.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            b.M.RecalculateBounds();
        }
    }
}
