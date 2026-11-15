using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.AI
{
    /// A wake check that heard "Hey Whisp".
    public sealed class WakeHit
    {
        /// The wake check's text, and what was said after the phrase in it.
        public string Text, Rest;
        /// The segment stopped at WakeGate.MaxSeconds with the wearer still talking.
        public bool Cut;
        /// The segment in the microphone's loop (VoiceRecorder.StartAt); -1: not live audio (the WAV test).
        public int StartPos = -1, EndPos = -1;
        public int AudioMs, ServiceMs, RoundTripMs;
        /// The command came in the same breath ("Hey Whisp, open the terminal"): TALK takes the audio from the start
        /// of the segment and drops the phrase from its transcript (WakeWord.Strip).
        public bool Continues => Cut || !string.IsNullOrEmpty(Rest);
    }

    /// "Hey Whisp" (docs/voice.md): while on, it reads the shared microphone capture (VoiceRecorder.OpenShared, the
    /// same capture TALK records from) through the voice gate (WakeGate) and sends each short voiced segment to the AI
    /// service's wake check (POST /v1/audio/wake); WakeWord decides, Heard reports a match. Main thread, Tick each
    /// frame; no allocation per frame. It yields: nothing is read while Blocked gives a reason (TALK, the app speaking,
    /// the first start's setup, paused, headset off, no AI service); one check at a time; at most Burst in a row, one
    /// more every RefillSeconds, never two within MinGap; a check the service drops (busy, the local coder) makes it
    /// rest a while.
    public sealed class WakeListener
    {
        public readonly WakeGate Gate = new WakeGate();
        /// Why not to listen now (null: listen). "paused", "off head" and "no focus" also close the microphone.
        public Func<string> Blocked;
        /// Sends a 16 kHz WAV to the wake check with an extra query (null, or "audio_ctx=512&log=1" for tests); the
        /// callback gets (reply, null) or (null, error) on the main thread.
        public Action<byte[], string, Action<JToken, string>> Check;
        public event Action<WakeHit> Heard;

        public bool On { get; private set; }
        /// off | waiting (WaitReason) | listening | checking
        public string State { get; private set; } = "off";
        public string WaitReason { get; private set; }
        public string LastError { get; private set; }
        /// The last text that woke it (overheard speech that did not is not kept).
        public string LastHeard { get; private set; }
        public int Checks, Wakes, Skipped, Dropped, Errors;
        public int LastServiceMs, LastRoundTripMs;
        public float ServiceSeconds;

        // the rate (one place): a burst of 3 checks, one more every 5 s, never two within 1.5 s
        public int Burst = 3;
        public float RefillSeconds = 5f, MinGap = 1.5f;

        private float _tokens = 3f, _refillAt, _lastCheckAt = -100f, _restUntil, _retryMicAt;
        private bool _inFlight;
        private int _tap = -1, _captureId = -1;
        private float[] _block = new float[0];
        private VoiceRecorder _rec;

        public void SetOn(bool on, VoiceRecorder rec)
        {
            _rec = rec;
            if (on == On) return;
            On = on;
            _tap = -1;
            Gate.Break();
            if (on) { State = "waiting"; WaitReason = "starting"; _tokens = Burst; _retryMicAt = 0f; LastError = null; }
            else { rec?.CloseShared(); State = "off"; WaitReason = null; }
        }

        public void Tick(float now)
        {
            if (!On || _rec == null) return;
            string why = Blocked != null ? Blocked() : null;
            if (why == null && now < _restUntil) why = "resting";
            if (why != null)
            {
                State = "waiting";
                WaitReason = why;
                if ((why == "paused" || why == "off head" || why == "no focus") && _rec.Shared) _rec.CloseShared();
                _tap = -1; // start over from the microphone's position when it listens again
                return;
            }
            if (!_rec.SharedOpen)
            {
                if (now < _retryMicAt) { State = "waiting"; WaitReason = "microphone"; return; }
                if (!_rec.OpenShared())
                {
                    LastError = "microphone: " + _rec.LastError;
                    _retryMicAt = now + 10f;
                    State = "waiting";
                    WaitReason = "microphone";
                    return;
                }
                _tap = -1;
            }
            int head = _rec.Position;
            if (head < 0) return;
            if (_tap < 0 || _captureId != _rec.CaptureId)
            {
                _captureId = _rec.CaptureId;
                if (Gate.Rate != _rec.Rate) Gate.Reset(_rec.Rate); else Gate.Break();
                int n = Mathf.NextPowerOfTwo(Mathf.Max(256, _rec.Rate / 32)); // ~32 ms
                if (_block.Length != n) _block = new float[n];
                _tap = head;
                State = "listening";
                WaitReason = null;
                return;
            }
            State = _inFlight ? "checking" : "listening";
            WaitReason = null;
            int loop = _rec.LoopSamples;
            int behind = head - _tap;
            if (behind < 0) behind += loop;
            if (behind > _rec.Rate) { _tap = head; Gate.Break(); return; } // over a second behind (a long frame): from now
            for (int k = 0; k < 24 && _rec.ReadTap(_block, ref _tap, head); k++)
            {
                Gate.Feed(_block, _block.Length);
                if (Gate.HasSegment) OnSegment(now, loop);
            }
        }

        private static int Mod(long a, int m) => m <= 0 ? 0 : (int)(((a % m) + m) % m);

        private void OnSegment(float now, int loop)
        {
            if (_inFlight) { Dropped++; Gate.Clear(); return; }
            _tokens = Mathf.Min(Burst, _tokens + (now - _refillAt) / RefillSeconds);
            _refillAt = now;
            if (_tokens < 1f || now - _lastCheckAt < MinGap) { Dropped++; Gate.Clear(); return; }
            // where the segment sits in the microphone's loop (`_tap` is just after the block fed last)
            int start = Mod(_tap - (Gate.Fed - Gate.SegmentStart), loop);
            int end = Mod(_tap - (Gate.Fed - Gate.SegmentEnd), loop);
            bool cut = Gate.SegmentCut;
            int audioMs = Gate.SegmentLength * 1000 / Mathf.Max(1, Gate.Rate);
            var wav = Gate.SegmentWav16k();
            Gate.Clear();
            if (wav == null || Check == null) return;
            _tokens -= 1f;
            _lastCheckAt = now;
            _inFlight = true;
            Checks++;
            State = "checking";
            float sent = Time.realtimeSinceStartup;
            Check(wav, null, (r, e) => OnReply(r, e, start, end, cut, audioMs, sent));
        }

        private void OnReply(JToken r, string e, int start, int end, bool cut, int audioMs, float sent)
        {
            _inFlight = false;
            float now = Time.unscaledTime;
            LastRoundTripMs = (int)((Time.realtimeSinceStartup - sent) * 1000f);
            if (e != null) { Errors++; LastError = e; _restUntil = now + 10f; return; }
            string skipped = (string)r?["skipped"];
            if (skipped != null)
            {
                Skipped++;
                _restUntil = now + (skipped == "coder" ? 15f : skipped == "model" ? 60f : skipped == "paused" ? 10f : skipped == "speech" ? 2f : 0f);
                return;
            }
            LastServiceMs = (int?)r?["ms"] ?? 0;
            ServiceSeconds += LastServiceMs / 1000f;
            string text = (string)r?["text"] ?? "";
            if (!On || !WakeWord.Match(text, out string rest)) return;
            if (Blocked != null && Blocked() != null) return; // TALK started by hand, or the app speaks, meanwhile
            Wakes++;
            LastHeard = text;
            Heard?.Invoke(new WakeHit { Text = text, Rest = rest, Cut = cut, StartPos = start, EndPos = end, AudioMs = audioMs, ServiceMs = LastServiceMs, RoundTripMs = LastRoundTripMs });
        }

        /// One line for `voice wake` (the control port) and VOICE's status.
        public string Describe()
        {
            var sb = new StringBuilder("hey whisp ");
            sb.Append(On ? State : "off");
            if (On && WaitReason != null) sb.Append(" (").Append(WaitReason).Append(')');
            sb.Append(", checks ").Append(Checks).Append(", wakes ").Append(Wakes).Append(", service busy ").Append(Skipped)
              .Append(", dropped by the rate ").Append(Dropped).Append(", errors ").Append(Errors)
              .Append(", last check ").Append(LastServiceMs).Append(" ms (round trip ").Append(LastRoundTripMs).Append(" ms)")
              .Append(", service time ").Append(ServiceSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append(" s")
              .Append(", segments ").Append(Gate.Segments).Append(", noise ").Append(Gate.Noise.ToString("F4", CultureInfo.InvariantCulture));
            if (LastError != null) sb.Append(", last error: ").Append(LastError);
            if (LastHeard != null) sb.Append(", last heard: \"").Append(LastHeard).Append('"');
            return sb.ToString();
        }

        // ---- the test path without a microphone (`voice wake test <file.wav>`, docs/voice.md "Hey Whisp")

        /// Mono samples of a 16-bit PCM WAV (any channels) and its rate; null with `error`.
        public static float[] ReadWav(byte[] wav, out int rate, out string error)
        {
            rate = 0;
            error = null;
            if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") { error = "not a WAV file"; return null; }
            int channels = 0, bits = 0, off = 12, dataOff = -1, dataLen = 0;
            while (off + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, off, 4);
                int size = BitConverter.ToInt32(wav, off + 4);
                if (id == "fmt ") { channels = BitConverter.ToInt16(wav, off + 10); rate = BitConverter.ToInt32(wav, off + 12); bits = BitConverter.ToInt16(wav, off + 22); }
                else if (id == "data") { dataOff = off + 8; dataLen = Math.Min(size, wav.Length - dataOff); break; }
                off += 8 + size + (size & 1);
            }
            if (bits != 16 || channels < 1 || rate <= 0 || dataOff < 0) { error = $"want a 16-bit PCM WAV (this one: {bits} bit, {channels} channels, {rate} Hz)"; return null; }
            int frames = dataLen / 2 / channels;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float v = 0f;
                for (int c = 0; c < channels; c++) { int o = dataOff + (i * channels + c) * 2; v += (short)(wav[o] | wav[o + 1] << 8) / 32768f; }
                mono[i] = v / channels;
            }
            return mono;
        }

        /// The same gate (a copy of Gate's tuning) and the same wake check on a WAV file, stage by stage; `reply` gets one
        /// JSON line: the gate's time, then per segment its place, peak, the WAV's time, the round trip, the service's
        /// time and window, the text, the match and its time. `query` goes to the service (audio_ctx=512, max_tokens=12,
        /// prompt=...). `onWake` (may be null) gets the first segment that woke it.
        public void TestWav(byte[] file, string query, Action<string> reply, Action<WakeHit> onWake)
        {
            var samples = ReadWav(file, out int rate, out string err);
            if (samples == null) { reply("error: " + err); return; }
            var g = new WakeGate
            {
                MinLevel = Gate.MinLevel, NoiseRatio = Gate.NoiseRatio, MinPeak = Gate.MinPeak, OnsetSeconds = Gate.OnsetSeconds, HangSeconds = Gate.HangSeconds,
                MinSeconds = Gate.MinSeconds, MaxSeconds = Gate.MaxSeconds, PreRollSeconds = Gate.PreRollSeconds, TailSeconds = Gate.TailSeconds,
            };
            g.Reset(rate);
            int n = Mathf.NextPowerOfTwo(Mathf.Max(256, rate / 32));
            var block = new float[n];
            var segs = new List<(double start, double end, bool cut, float peak, byte[] wav, double wavMs)>();
            var sw = Stopwatch.StartNew();
            double wavMs = 0;
            // the file, then a second of silence (a phrase at its very end still ends)
            int total = samples.Length + rate;
            for (int i = 0; i < total; i += n)
            {
                for (int k = 0; k < n; k++) block[k] = i + k < samples.Length ? samples[i + k] : 0f;
                g.Feed(block, n);
                if (!g.HasSegment) continue;
                var w0 = sw.Elapsed.TotalMilliseconds;
                var wav = g.SegmentWav16k();
                double ms = sw.Elapsed.TotalMilliseconds - w0;
                wavMs += ms;
                segs.Add((g.SegmentStart / (double)rate, g.SegmentEnd / (double)rate, g.SegmentCut, g.SegmentPeak, wav, ms));
                g.Clear();
            }
            double gateMs = sw.Elapsed.TotalMilliseconds - wavMs;
            var outJ = new JObject
            {
                ["seconds"] = Math.Round(samples.Length / (double)rate, 2), ["rate"] = rate, ["gateMs"] = Math.Round(gateMs, 2),
                ["noise"] = Math.Round(g.Noise, 4), ["segments"] = new JArray(),
            };
            var arr = (JArray)outJ["segments"];
            bool fired = false;
            void Next(int i)
            {
                if (i >= segs.Count || Check == null) { reply(outJ.ToString(Newtonsoft.Json.Formatting.None)); return; }
                var s = segs[i];
                var j = new JObject
                {
                    ["start"] = Math.Round(s.start, 2), ["end"] = Math.Round(s.end, 2), ["cut"] = s.cut, ["peak"] = Math.Round(s.peak, 4),
                    ["wavMs"] = Math.Round(s.wavMs, 2),
                };
                arr.Add(j);
                var t0 = Stopwatch.StartNew();
                Check(s.wav, string.IsNullOrEmpty(query) ? "log=1" : query + "&log=1", (r, e) =>
                {
                    j["roundTripMs"] = Math.Round(t0.Elapsed.TotalMilliseconds, 1);
                    if (e != null) j["error"] = e;
                    else if (r?["skipped"] != null) j["skipped"] = r["skipped"];
                    else
                    {
                        foreach (var key in new[] { "ms", "reloadMs", "loadMs", "audioMs", "audioCtx", "text" }) if (r?[key] != null) j[key == "ms" ? "serviceMs" : key] = r[key];
                        var m0 = Stopwatch.StartNew();
                        bool wake = WakeWord.Match((string)r?["text"] ?? "", out string rest);
                        j["matchMs"] = Math.Round(m0.Elapsed.TotalMilliseconds, 3);
                        j["wake"] = wake;
                        if (wake) j["rest"] = rest;
                        if (wake && !fired && onWake != null)
                        {
                            fired = true;
                            onWake(new WakeHit { Text = (string)r["text"], Rest = rest, Cut = s.cut, AudioMs = (int)((s.end - s.start) * 1000), ServiceMs = (int?)r["ms"] ?? 0, RoundTripMs = (int)t0.Elapsed.TotalMilliseconds });
                        }
                    }
                    Next(i + 1);
                });
            }
            Next(0);
        }
    }
}
