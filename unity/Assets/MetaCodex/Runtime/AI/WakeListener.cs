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
        /// The wake check's text, and what was said after the phrase in it (WakeWord.Match: a repeated phrase and the
        /// model's noise after it are not a rest).
        public string Text, Rest;
        /// The segment stopped at WakeGate.MaxSeconds with the wearer still talking.
        public bool Cut;
        /// The segment in the microphone's loop (VoiceRecorder.StartAt); -1: not live audio (the WAV test).
        public int StartPos = -1, EndPos = -1;
        public int AudioMs, ServiceMs, RoundTripMs;
        /// From the end of the phrase (its last voiced sample) to the hit: the gate's wait for quiet and the check
        /// (-1: not live audio).
        public int ReactMs = -1;
        /// The command came in the same breath ("Hey Whisp, open the terminal"): TALK takes the audio from the start
        /// of the segment and drops the phrase from its transcript (WakeWord.Command).
        public bool Continues => Cut || !string.IsNullOrEmpty(Rest);
    }

    /// "Hey Whisp" (docs/voice.md): while on, it reads the shared microphone capture (VoiceRecorder.OpenShared, the
    /// same capture TALK records from) through the voice gate (WakeGate) and sends each short voiced segment to the AI
    /// service's wake check (POST /v1/audio/wake); WakeWord decides, Heard reports a match. Main thread, Tick each
    /// frame; no allocation per frame. It yields: nothing is read while Blocked gives a reason (TALK, the app speaking,
    /// the first start's setup, no microphone permission, paused, headset off, no AI service). One check at a time:
    /// a segment that comes while one runs waits for its reply (only the newest, at most PendingSeconds old). WakeRate
    /// bounds the checks (4 in a row, one more per 4 s, the last one kept for a phrase-sized segment). A check the
    /// service drops (busy, the local coder) makes it rest a while. While it listens and nothing was checked for
    /// WarmSeconds, one silent check keeps the speech model loaded (a cold one costs 1-4 s on the first "Hey Whisp").
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
        public int Checks, Wakes, Skipped, Dropped, Errors, Queued, NearMisses, Warms;
        /// Phrase-sized segments the rate dropped (a missed "Hey Whisp" would be one of these).
        public int DroppedPhrases;
        public int LastServiceMs, LastRoundTripMs;
        public float ServiceSeconds;
        /// A check heard a word like the name but did not wake (Heard is not raised); the text, for the session log.
        public event Action<string> NearMiss;

        /// The rate (one place): WakeRate's defaults, 4 in a row, one more per 4 s.
        public readonly WakeRate Rate = new WakeRate();
        /// A segment waits this long at most for the check before it (older: dropped, a late wake would surprise).
        public float PendingSeconds = 1.5f;
        /// One silent check after this long without a check keeps the speech model loaded (the service unloads it
        /// after 30 idle minutes); the first one this long after it starts listening.
        public float WarmSeconds = 20f * 60f, FirstWarmSeconds = 15f;

        private float _restUntil, _retryMicAt, _warmAt = -1f;
        private bool _inFlight;
        private int _tap = -1, _captureId = -1;
        private float[] _block = new float[0];
        private VoiceRecorder _rec;
        // the newest segment that came while a check ran
        private byte[] _pendWav;
        private int _pendStart, _pendEnd, _pendAudioMs;
        private bool _pendCut, _pendPhrase;
        private float _pendAt;
        private static byte[] _silentWav;

        public void SetOn(bool on, VoiceRecorder rec)
        {
            _rec = rec;
            if (on == On) return;
            On = on;
            _tap = -1;
            Gate.Break();
            _pendWav = null;
            if (on) { State = "waiting"; WaitReason = "starting"; Rate.Reset(Time.unscaledTime); _retryMicAt = 0f; _warmAt = -1f; LastError = null; }
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
                _pendWav = null;
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
                if (_warmAt < 0f) _warmAt = now + FirstWarmSeconds;
                return;
            }
            State = _inFlight ? "checking" : "listening";
            WaitReason = null;
            if (!_inFlight && _warmAt >= 0f && now >= _warmAt) Warm(now);
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
            // where the segment sits in the microphone's loop (`_tap` is just after the block fed last)
            int start = Mod(_tap - (Gate.Fed - Gate.SegmentStart), loop);
            int end = Mod(_tap - (Gate.Fed - Gate.SegmentEnd), loop);
            bool cut = Gate.SegmentCut, phrase = Gate.SegmentPhraseLike;
            int audioMs = Gate.SegmentLength * 1000 / Mathf.Max(1, Gate.Rate);
            if (_inFlight)
            {
                // it goes when the reply is back: the newest one only (a merged segment holds the one before it)
                if (_pendWav != null) Dropped++;
                _pendWav = Gate.SegmentWav16k();
                _pendStart = start; _pendEnd = end; _pendCut = cut; _pendPhrase = phrase; _pendAudioMs = audioMs; _pendAt = now;
                Queued++;
                Gate.Clear();
                return;
            }
            if (!Rate.Take(now, phrase)) { Dropped++; if (phrase) DroppedPhrases++; Gate.Clear(); return; }
            var wav = Gate.SegmentWav16k();
            Gate.Clear();
            Send(wav, start, end, cut, audioMs, now);
        }

        private void Send(byte[] wav, int start, int end, bool cut, int audioMs, float now)
        {
            if (wav == null || Check == null) return;
            _inFlight = true;
            _warmAt = now + WarmSeconds;
            Checks++;
            State = "checking";
            float sent = Time.realtimeSinceStartup;
            Check(wav, null, (r, e) => OnReply(r, e, start, end, cut, audioMs, sent));
        }

        /// One silent check: the service loads the speech model if it unloaded it (its text is not read).
        private void Warm(float now)
        {
            if (Check == null) return;
            if (_silentWav == null) _silentWav = VoiceRecorder.Wav(new short[16000 * 3 / 10], 16000); // 0.3 s, once
            _inFlight = true;
            _warmAt = now + WarmSeconds;
            Warms++;
            Check(_silentWav, null, (r, e) =>
            {
                _inFlight = false;
                float t = Time.unscaledTime;
                if (e != null || r?["skipped"] != null) _warmAt = t + 60f; // busy or down: later
                SendPending(t);
            });
        }

        /// The segment that waited for the last reply, if it is still fresh and the rate lets it go.
        private void SendPending(float now)
        {
            var wav = _pendWav;
            _pendWav = null;
            if (wav == null || !On || now - _pendAt > PendingSeconds || now < _restUntil || Blocked != null && Blocked() != null) { if (wav != null) Dropped++; return; }
            if (!Rate.Take(now, _pendPhrase)) { Dropped++; if (_pendPhrase) DroppedPhrases++; return; }
            Send(wav, _pendStart, _pendEnd, _pendCut, _pendAudioMs, now);
        }

        private void OnReply(JToken r, string e, int start, int end, bool cut, int audioMs, float sent)
        {
            _inFlight = false;
            float now = Time.unscaledTime;
            LastRoundTripMs = (int)((Time.realtimeSinceStartup - sent) * 1000f);
            if (e != null) { Errors++; LastError = e; _restUntil = now + 10f; _pendWav = null; return; }
            string skipped = (string)r?["skipped"];
            if (skipped != null)
            {
                Skipped++;
                _restUntil = now + (skipped == "coder" ? 15f : skipped == "model" ? 60f : skipped == "paused" ? 10f : skipped == "speech" ? 2f : 0f);
                SendPending(now);
                return;
            }
            LastServiceMs = (int?)r?["ms"] ?? 0;
            ServiceSeconds += LastServiceMs / 1000f;
            string text = (string)r?["text"] ?? "";
            if (!On || !WakeWord.Match(text, out string rest))
            {
                if (On && WakeWord.NearMiss(text)) { NearMisses++; NearMiss?.Invoke(text); }
                SendPending(now);
                return;
            }
            _pendWav = null;
            if (Blocked != null && Blocked() != null) return; // TALK started by hand, or the app speaks, meanwhile
            Wakes++;
            LastHeard = text;
            // from the phrase's last voiced sample (the segment's end less the gate's tail) to the microphone now
            int react = -1;
            if (_rec != null && _rec.Rate > 0 && _rec.LoopSamples > 0 && _rec.Position >= 0)
                react = (int)((Mod((long)_rec.Position - end, _rec.LoopSamples) + (long)(Gate.TailSeconds * _rec.Rate)) * 1000L / _rec.Rate);
            Heard?.Invoke(new WakeHit { Text = text, Rest = rest, Cut = cut, StartPos = start, EndPos = end, AudioMs = audioMs, ServiceMs = LastServiceMs, RoundTripMs = LastRoundTripMs, ReactMs = react });
        }

        /// One line for `voice wake` (the control port) and VOICE's status.
        public string Describe()
        {
            var sb = new StringBuilder("hey whisp ");
            sb.Append(On ? State : "off");
            if (On && WaitReason != null) sb.Append(" (").Append(WaitReason).Append(')');
            sb.Append(", checks ").Append(Checks).Append(", wakes ").Append(Wakes).Append(", near misses ").Append(NearMisses).Append(", service busy ").Append(Skipped)
              .Append(", waited for a check ").Append(Queued).Append(", dropped by the rate ").Append(Dropped).Append(" (phrase-sized ").Append(DroppedPhrases).Append(')')
              .Append(", keep-warm ").Append(Warms).Append(", errors ").Append(Errors)
              .Append(", last check ").Append(LastServiceMs).Append(" ms (round trip ").Append(LastRoundTripMs).Append(" ms)")
              .Append(", service time ").Append(ServiceSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append(" s")
              .Append(", segments ").Append(Gate.Segments).Append(" (joined over a pause ").Append(Gate.Merges).Append("), noise ").Append(Gate.Noise.ToString("F4", CultureInfo.InvariantCulture));
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
                MergeSeconds = Gate.MergeSeconds, PhraseSeconds = Gate.PhraseSeconds,
            };
            g.Reset(rate);
            int n = Mathf.NextPowerOfTwo(Mathf.Max(256, rate / 32));
            var block = new float[n];
            var segs = new List<(double start, double end, bool cut, float peak, byte[] wav, double wavMs, bool merged)>();
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
                segs.Add((g.SegmentStart / (double)rate, g.SegmentEnd / (double)rate, g.SegmentCut, g.SegmentPeak, wav, ms, g.SegmentMerged));
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
                    ["start"] = Math.Round(s.start, 2), ["end"] = Math.Round(s.end, 2), ["cut"] = s.cut, ["merged"] = s.merged, ["peak"] = Math.Round(s.peak, 4),
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
                        if (wake) j["command"] = WakeWord.Command((string)r?["text"] ?? "", true);
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
