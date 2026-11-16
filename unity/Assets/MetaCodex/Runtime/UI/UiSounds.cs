using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace MetaCodex.UI
{
    /// The app's short sounds, made in code (UiSoundSynth: no audio assets): click, hover, open, close, orb-on, orb-off,
    /// wake, ping, toast, alert, error, in one of four styles (Style: hud, holo, core, classic). One AudioSource,
    /// PlayOneShot. The samples are made on a worker thread (pure float math, ~10-20 ms on one PC core, more on the
    /// headset; none of it on the main thread), starting in Start with the Style set by then; Update turns each into an
    /// AudioClip (AudioClip.Create / SetData must run on the main thread) and switches itself off when all are there.
    /// At start each clip plays as soon as it exists (a sound asked for before does not play). A new Style is made the
    /// same way while the old one keeps playing; when all eleven are there they replace the old clips (destroyed: only
    /// one style is in memory) and a "ping" in the new style plays.
    public sealed class UiSounds : MonoBehaviour
    {
        public static UiSounds Current { get; private set; }
        public bool Enabled = true;
        public float Volume = 0.35f;
        private AudioSource _src;
        private Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private Dictionary<string, AudioClip> _next; // a new style's clips until all are there
        private readonly ConcurrentQueue<Made> _made = new ConcurrentQueue<Made>();
        private string _style = UiSoundSynth.Styles[0];
        private bool _started;
        private volatile int _gen, _doneGen = -1;
        private volatile string _failed;
        private double _makeMs;
        private int _samples;
        private float _lastPlay;

        private struct Made { public int Gen; public string Name; public float[] Data; }

        /// The style (UiSoundSynth.Styles; anything else is hud). Set before Start: the first clips are made in it; set
        /// later: that style is made on the worker and replaces the clips when it is complete.
        public string Style
        {
            get => _style;
            set
            {
                string s = UiSoundSynth.StyleOf(value);
                if (s == _style) return;
                _style = s;
                if (_started) Begin();
            }
        }

        private void Awake()
        {
            Current = this;
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            _src.priority = 200;
        }

        private void Start() { _started = true; Begin(); }

        /// Starts making the current style (a newer request makes an older one stop and drop what it made).
        private void Begin()
        {
            int gen = ++_gen;
            if (_next != null) foreach (var c in _next.Values) Destroy(c);
            _next = _clips.Count > 0 ? new Dictionary<string, AudioClip>() : null; // nothing yet (start): fill _clips directly
            _failed = null;
            enabled = true;
            string style = _style;
            Task.Run(() => Make(gen, style));
        }

        /// On a worker thread: every sound's samples, in UiSoundSynth.Names order (the ones needed first, first).
        private void Make(int gen, string style)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (var name in UiSoundSynth.Names)
                {
                    if (gen != _gen) return; // a newer style was asked for
                    _made.Enqueue(new Made { Gen = gen, Name = name, Data = UiSoundSynth.Render(style, name) });
                }
            }
            catch (System.Exception ex) { if (gen == _gen) _failed = ex.GetType().Name + ": " + ex.Message; }
            if (gen != _gen) return;
            _makeMs = sw.Elapsed.TotalMilliseconds;
            _doneGen = gen;
        }

        private void Update()
        {
            while (_made.TryDequeue(out var m))
            {
                if (m.Gen != _gen || m.Data == null) continue;
                var clip = AudioClip.Create("mcx-" + _style + "-" + m.Name, m.Data.Length, 1, UiSoundSynth.Rate, false);
                clip.SetData(m.Data, 0);
                var into = _next ?? _clips;
                if (into.TryGetValue(m.Name, out var old)) Destroy(old);
                into[m.Name] = clip;
            }
            if (_doneGen != _gen || !_made.IsEmpty) return;
            if (_failed != null)
            {
                Debug.LogWarning("[MetaCodex] sounds: " + _style + " stopped after " + (_next ?? _clips).Count + " of " + UiSoundSynth.Names.Length + ": " + _failed);
                if (_next != null) { foreach (var c in _next.Values) Destroy(c); _next = null; } // keep the old style
            }
            else
            {
                bool swap = _next != null;
                if (swap)
                {
                    foreach (var c in _clips.Values) Destroy(c);
                    _clips = _next;
                    _next = null;
                }
                _samples = 0;
                foreach (var c in _clips.Values) _samples += c.samples;
                Debug.Log($"[MetaCodex] sounds: {_style}, {_clips.Count} made off the main thread in {_makeMs:F1} ms ({_samples} samples, {_samples * 4 / 1024} KB)");
                if (swap) Play("ping"); // the new style, heard at once
            }
            enabled = false;
        }

        private void OnDestroy()
        {
            _gen++; // a worker still running stops
            if (Current == this) Current = null;
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
