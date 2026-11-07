using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.UI
{
    /// Short console sounds made in code (no audio assets): click, open, close, toast, alert, error.
    /// One AudioSource, clips synthesised once at start (~60 KB total), played with PlayOneShot.
    public sealed class UiSounds : MonoBehaviour
    {
        public static UiSounds Current { get; private set; }
        public bool Enabled = true;
        public float Volume = 0.35f;
        private AudioSource _src;
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private float _lastPlay;
        private const int Rate = 44100;

        private void Awake()
        {
            Current = this;
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            _src.priority = 200;
            Make("click", 0.045f, (t, d) => Tone(t, 1850f, 2400f, d) * Env(t, d, 0.002f, 0.035f) * 0.55f);
            Make("hover", 0.018f, (t, d) => Tone(t, 3200f, 3200f, d) * Env(t, d, 0.001f, 0.015f) * 0.12f);
            Make("open", 0.16f, (t, d) => (Tone(t, 520f, 1240f, d) * 0.6f + Tone(t, 1040f, 2480f, d) * 0.25f) * Env(t, d, 0.01f, 0.12f) * 0.45f);
            Make("close", 0.13f, (t, d) => Tone(t, 1100f, 420f, d) * Env(t, d, 0.005f, 0.11f) * 0.4f);
            Make("toast", 0.11f, (t, d) => (t < d * 0.5f ? Tone(t, 1320f, 1320f, d) : Tone(t, 1760f, 1760f, d)) * Env(t, d, 0.004f, 0.09f) * 0.35f);
            Make("alert", 0.32f, (t, d) => ((int)(t / 0.08f) % 2 == 0 ? Tone(t, 880f, 880f, d) : Tone(t, 1175f, 1175f, d)) * Env(t, d, 0.005f, 0.3f) * 0.45f);
            Make("error", 0.22f, (t, d) => Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 180f * t)) * Env(t, d, 0.004f, 0.2f) * 0.18f);
        }

        private void OnDestroy() { if (Current == this) Current = null; }

        private static float Tone(float t, float f0, float f1, float d)
        {
            // linear chirp from f0 to f1 over d seconds
            float k = (f1 - f0) / Mathf.Max(d, 1e-4f);
            return Mathf.Sin(2f * Mathf.PI * (f0 * t + 0.5f * k * t * t));
        }

        private static float Env(float t, float d, float attack, float release) =>
            Mathf.Clamp01(t / Mathf.Max(attack, 1e-4f)) * Mathf.Clamp01((d - t) / Mathf.Max(release, 1e-4f));

        private void Make(string name, float seconds, System.Func<float, float, float> f)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = f(i / (float)Rate, seconds);
            var clip = AudioClip.Create("mcx-" + name, n, 1, Rate, false);
            clip.SetData(data, 0);
            _clips[name] = clip;
        }

        public void Play(string name, float volumeScale = 1f)
        {
            if (!Enabled || _src == null || !Application.isPlaying) return;
            if (!_clips.TryGetValue(name, out var c)) return;
            if (name == "hover" && Time.unscaledTime - _lastPlay < 0.06f) return;
            _lastPlay = Time.unscaledTime;
            _src.PlayOneShot(c, Volume * volumeScale);
        }

        public static void Fire(string name) => Current?.Play(name);
    }
}
