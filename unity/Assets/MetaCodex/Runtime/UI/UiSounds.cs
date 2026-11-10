using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.UI
{
    /// Short console sounds made in code (no audio assets): click, open, close, ping, toast, alert, error, the orb.
    /// Open and close follow a panel's cast (UiPanelFx): two ticks as the brackets lock on, a rising sweep with a
    /// shimmer as the scan line crosses, a low thump and a clear note as it settles; a surge, a falling sweep as the
    /// line runs back, a quick rise as the spark flies home to the orb.
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
            Make("open", 0.66f, (t, d) =>
                Tone(t, 3800f, 3400f, 0.02f) * Env(t, 0.02f, 0.001f, 0.018f) * 0.20f                                    // the brackets lock on: two ticks
                + Chirp(t, 0.045f, 0.02f, 4300f, 3900f) * Decay(t, 0.045f, 90f) * 0.16f
                + (Chirp(t, 0.09f, 0.42f, 260f, 1250f) * 0.5f + Chirp(t, 0.09f, 0.42f, 523f, 2510f) * 0.14f) * Win(t, 0.08f, 0.54f) * 0.50f // the scan line crosses
                + (Mathf.Sin(2f * Mathf.PI * 2093f * t) + 0.7f * Mathf.Sin(2f * Mathf.PI * 3136f * t))
                  * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 31f * t)) * Win(t, 0.14f, 0.52f) * 0.05f                  // the beads of light in it
                + Mathf.Sin(2f * Mathf.PI * 140f * t) * Decay(t, 0.50f, 26f) * 0.30f                                     // it settles: a low thump
                + Mathf.Sin(2f * Mathf.PI * 2637f * t) * Decay(t, 0.505f, 18f) * 0.07f);                                 // and a clear note
            Make("close", 0.46f, (t, d) =>
                Tone(t, 4200f, 1800f, 0.025f) * Env(t, 0.025f, 0.001f, 0.02f) * 0.20f                                    // the surge
                + (Chirp(t, 0.04f, 0.29f, 1250f, 200f) * 0.5f + Chirp(t, 0.04f, 0.29f, 2510f, 400f) * 0.11f) * Win(t, 0.03f, 0.36f) * 0.48f // the line runs back
                + Chirp(t, 0.32f, 0.10f, 1500f, 4200f) * Win(t, 0.32f, 0.43f) * 0.14f                                    // the spark flies home
                + Mathf.Sin(2f * Mathf.PI * 196f * t) * Decay(t, 0.42f, 40f) * 0.18f);                                   // and is taken in
            // the orb ignites (a chord swelling, gliding up a little) and goes out
            Make("orb-on", 0.70f, (t, d) =>
                (Mathf.Sin(2f * Mathf.PI * (392f * t + 30f * t * t)) + 0.7f * Mathf.Sin(2f * Mathf.PI * (587f * t + 45f * t * t)) + 0.5f * Mathf.Sin(2f * Mathf.PI * (784f * t + 60f * t * t)))
                * (0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 9f * t)) * Win(t, 0f, 0.70f) * 0.13f
                + Mathf.Sin(2f * Mathf.PI * 1568f * t) * Decay(t, 0.42f, 9f) * 0.06f);
            Make("orb-off", 0.42f, (t, d) =>
                (Mathf.Sin(2f * Mathf.PI * (587f * t - 260f * t * t)) + 0.6f * Mathf.Sin(2f * Mathf.PI * (880f * t - 390f * t * t))) * Win(t, 0f, 0.42f) * 0.13f);
            Make("ping", 0.22f, (t, d) => (Chirp(t, 0f, 0.08f, 1800f, 2600f) * Decay(t, 0f, 30f) + Chirp(t, 0.09f, 0.1f, 2400f, 3200f) * Decay(t, 0.09f, 26f)) * 0.22f);
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

        /// A chirp that starts at t0.
        private static float Chirp(float t, float t0, float dur, float f0, float f1) => t < t0 ? 0f : Tone(t - t0, f0, f1, dur);

        /// A smooth window (Hann) from t0 to t1.
        private static float Win(float t, float t0, float t1) { float k = Mathf.Clamp01((t - t0) / (t1 - t0)); float s = Mathf.Sin(Mathf.PI * k); return s * s; }

        /// A struck sound from t0: a 3 ms attack, then an exponential decay.
        private static float Decay(float t, float t0, float rate) => t < t0 ? 0f : Mathf.Clamp01((t - t0) / 0.003f) * Mathf.Exp(-(t - t0) * rate);

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
