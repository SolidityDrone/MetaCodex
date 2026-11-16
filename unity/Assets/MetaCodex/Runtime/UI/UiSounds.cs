using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace MetaCodex.UI
{
    /// The app's short sounds, made in code (UiSoundSynth: no audio assets): click, hover, open, close, orb-on, orb-off,
    /// wake, ping, toast, alert, error. One AudioSource, PlayOneShot. The samples are made on a worker thread at start
    /// (pure float math: 10-20 ms on one PC core, more on the headset; none of it on the main thread); Update turns each
    /// into an AudioClip as it arrives (AudioClip.Create / SetData must run on the main thread) and switches itself off
    /// when all are there. A sound asked for before its clip exists does not play.
    public sealed class UiSounds : MonoBehaviour
    {
        public static UiSounds Current { get; private set; }
        public bool Enabled = true;
        public float Volume = 0.35f;
        private AudioSource _src;
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private readonly ConcurrentQueue<KeyValuePair<string, float[]>> _made = new ConcurrentQueue<KeyValuePair<string, float[]>>();
        private volatile bool _done, _stop;
        private volatile string _failed;
        private double _makeMs;
        private int _samples;
        private float _lastPlay;

        private void Awake()
        {
            Current = this;
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            _src.priority = 200;
            Task.Run(Make);
        }

        /// On a worker thread: every sound's samples, in UiSoundSynth.Names order (the ones needed first, first).
        private void Make()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (var name in UiSoundSynth.Names)
                {
                    if (_stop) break;
                    _made.Enqueue(new KeyValuePair<string, float[]>(name, UiSoundSynth.Render(name)));
                }
            }
            catch (System.Exception ex) { _failed = ex.GetType().Name + ": " + ex.Message; }
            _makeMs = sw.Elapsed.TotalMilliseconds;
            _done = true;
        }

        private void Update()
        {
            while (_made.TryDequeue(out var kv))
            {
                var clip = AudioClip.Create("mcx-" + kv.Key, kv.Value.Length, 1, UiSoundSynth.Rate, false);
                clip.SetData(kv.Value, 0);
                _clips[kv.Key] = clip;
                _samples += kv.Value.Length;
            }
            if (!_done || !_made.IsEmpty) return;
            if (_failed != null) Debug.LogWarning("[MetaCodex] sounds: made " + _clips.Count + " of " + UiSoundSynth.Names.Length + ", then " + _failed);
            else Debug.Log($"[MetaCodex] sounds: {_clips.Count} made off the main thread in {_makeMs:F1} ms ({_samples} samples, {_samples * 4 / 1024} KB)");
            enabled = false;
        }

        private void OnDestroy()
        {
            _stop = true;
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
