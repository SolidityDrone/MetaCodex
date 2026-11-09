using System;
using System.Collections.Generic;
using MetaCodex.Hands;
using UnityEngine;

namespace MetaCodex.UI
{
    /// Short messages that float in the upper field of view and fade.
    public sealed class UiToasts : MonoBehaviour
    {
        public Transform Head;
        private sealed class Toast { public UiPanel Panel; public float Until; }
        private readonly List<Toast> _toasts = new List<Toast>();
        public string Last { get; private set; }

        public void Show(string text, string kind = "info", float seconds = 3.2f)
        {
            Last = text;
            var t = UiTheme.Current;
            var panel = UiPanel.Create<UiPanel>("toast", "", 0.42f, 0.052f, transform, header: false);
            panel.Closable = false;
            panel.Draggable = false;
            var tint = kind == "error" ? t.Danger : kind == "warn" ? t.Warn : kind == "ok" ? t.Ok : t.Accent;
            panel.Add(new UiLabel((kind == "error" ? "⚠ " : kind == "ok" ? "✓ " : "◆ ") + text, t.BodySize) { Rect = new Rect(0.016f, 0.006f, 0.39f, 0.04f), Color = Color.Lerp(tint, Color.white, 0.5f) });
            panel.SetHighlight(0.6f);
            panel.PlayOpen(); // comes in with the light transition of passing panels (UiPanelFx)
            UiSounds.Fire(kind == "error" ? "error" : "toast");
            _toasts.Insert(0, new Toast { Panel = panel, Until = Time.unscaledTime + seconds });
            while (_toasts.Count > 4) { _toasts[_toasts.Count - 1].Panel.CloseAndDestroy(); _toasts.RemoveAt(_toasts.Count - 1); }
            Layout(true);
        }

        private void Update()
        {
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < _toasts[i].Until) continue;
                if (_toasts[i].Panel != null) _toasts[i].Panel.CloseAndDestroy(); // and goes with it
                _toasts.RemoveAt(i);
            }
            Layout(false);
        }

        internal static void Kill(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }

        private void Layout(bool snap)
        {
            if (Head == null) return;
            var fwd = Head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            for (int i = 0; i < _toasts.Count; i++)
            {
                var target = Head.position + fwd * 0.75f + Vector3.up * (0.20f + i * 0.058f);
                var tr = _toasts[i].Panel.transform;
                tr.position = snap ? target : Vector3.Lerp(tr.position, target, 0.08f);
                tr.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            }
        }
    }
}
