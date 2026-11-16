using System;

namespace MetaCodex.AI
{
    /// The cheap voice gate in front of the "Hey Whisp" check: the energy of each block of mono samples against a
    /// noise floor that follows the quiet blocks (the rule of VoiceRecorder's end of speech). No allocation per block:
    /// the last seconds sit in a ring made once. A piece of speech is a voiced stretch that ends after HangSeconds of
    /// quiet. A piece of MinSeconds or more gives a segment: its samples with PreRollSeconds before (a soft "h") and
    /// TailSeconds after (the "sp" of "whisp"). A piece still going at MaxSeconds gives its first MaxSeconds (Cut).
    /// A phrase said in two pieces is caught whole: a piece too short to check alone ("hey", a pause, "whisp") is the
    /// start of the next segment when the next piece starts within MergeSeconds, and a short piece right after a
    /// checked one goes again with that one. Live audio (WakeListener), the WAV test path (`voice wake test`) and the
    /// PC tests (tests/voicefx, WakeTests.cs) go through this code.
    public sealed class WakeGate
    {
        // tuning, in one place
        /// A block below this RMS is never speech (VoiceRecorder's floor).
        public float MinLevel = 0.012f;
        /// Speech: RMS above the noise floor times this (as VoiceRecorder).
        public float NoiseRatio = 3.5f;
        /// The loudest block of a segment must reach this: quieter voices are far away (not the wearer).
        public float MinPeak = 0.02f;
        /// Voiced this long before a piece starts (a click or a tap is shorter).
        public float OnsetSeconds = 0.06f;
        /// Unvoiced this long ends a piece; the reaction to the phrase waits this long after it, then the check.
        public float HangSeconds = 0.22f;
        /// A segment holds this much speech at least (a fast "Hey Whisp" is about 0.35 s voiced), at most MaxSeconds.
        public float MinSeconds = 0.3f, MaxSeconds = 2.5f;
        public float PreRollSeconds = 0.25f, TailSeconds = 0.2f;
        /// Two pieces this close are one phrase when the first was too short to check, or the second is.
        public float MergeSeconds = 0.5f;
        /// A segment of at most this much speech, not cut, is the size of the phrase alone (WakeRate keeps a check for it).
        public float PhraseSeconds = 1.6f;

        public int Rate { get; private set; }
        /// The room's noise floor (block RMS) and the last block's RMS.
        public float Noise { get; private set; } = 0.004f;
        public float LastRms { get; private set; }
        /// A voiced stretch is going on now (the wearer still talks).
        public bool InSpeech => _state == 2 || _state == 3;
        /// Samples fed since Reset (the absolute index of the next one).
        public long Fed => _fed;

        // the segment ready to take (HasSegment); positions are absolute sample indexes (Fed)
        public bool HasSegment { get; private set; }
        public long SegmentStart { get; private set; }
        public long SegmentEnd { get; private set; }
        public bool SegmentCut { get; private set; }
        /// The segment reaches back over a pause to an earlier piece.
        public bool SegmentMerged { get; private set; }
        public float SegmentPeak { get; private set; }
        /// Seconds from its first voiced sample to its last.
        public float SegmentVoiced { get; private set; }
        /// The size of the phrase alone: not cut, at most PhraseSeconds of speech.
        public bool SegmentPhraseLike => !SegmentCut && SegmentVoiced <= PhraseSeconds;
        public int SegmentLength => (int)(SegmentEnd - SegmentStart);
        public int Segments { get; private set; }
        /// Segments that reached back over a pause.
        public int Merges { get; private set; }

        private float[] _ring = new float[0], _seg = new float[0];
        private long _fed, _floor, _onset, _lastVoiced, _quietSince;
        private int _state; // 0 quiet, 1 onset, 2 speech, 3 cut (waits for quiet)
        private float _peak;
        // the piece before this one: where it ended, where the segment it is in starts, that segment's peak, and
        // whether it was checked (long enough alone, or joined to one that was)
        private long _prevEnd = -1, _prevStart;
        private float _prevPeak;
        private bool _prevChecked;
        private long _winStart;   // where this piece's segment starts (its onset, or a short piece's before it)
        private float _winPeak;   // the peak of the pieces before it in that segment
        private bool _near;       // this piece started within MergeSeconds of the one before

        /// Starts over at `rate`: the ring (a few seconds) and the segment buffer are made here, once per rate.
        public void Reset(int rate)
        {
            if (rate <= 0) rate = 16000;
            if (rate != Rate || _ring.Length == 0)
            {
                Rate = rate;
                _seg = new float[(int)((PreRollSeconds + MaxSeconds + TailSeconds) * rate) + 64];
                _ring = new float[_seg.Length + rate]; // one second more than a segment
            }
            _fed = _floor = 0;
            Noise = 0.004f;
            LastRms = 0f;
            Break();
        }

        /// The audio before the next block is not the audio that comes next (a pause, TALK, the app speaking): the
        /// stretch under way is dropped and no segment reaches back over the gap.
        public void Break()
        {
            _state = 0;
            _peak = 0f;
            _floor = _fed;
            _prevEnd = -1;
            HasSegment = false;
        }

        /// The segment was taken or is not wanted.
        public void Clear() => HasSegment = false;

        /// One block of `count` mono samples (about 30 ms). No allocation.
        public void Feed(float[] block, int count)
        {
            if (Rate <= 0) Reset(16000);
            if (count <= 0) return;
            double sum = 0;
            int cap = _ring.Length;
            int w = (int)(_fed % cap);
            for (int i = 0; i < count; i++)
            {
                float v = block[i];
                sum += v * v;
                _ring[w] = v;
                if (++w == cap) w = 0;
            }
            long start = _fed;
            _fed += count;
            if (_fed - _floor > cap) _floor = _fed - cap;
            float rms = (float)Math.Sqrt(sum / count);
            LastRms = rms;
            bool voiced = rms > Math.Max(MinLevel, Noise * NoiseRatio);
            if (!voiced) Noise += (rms - Noise) * 0.05f;
            if (rms < Noise) Noise += (rms - Noise) * 0.3f;

            switch (_state)
            {
                case 0: // quiet
                    if (voiced) { _state = 1; _onset = start; _peak = rms; _lastVoiced = _fed; }
                    break;
                case 1: // onset: voiced long enough?
                    if (!voiced) { _state = 0; break; }
                    _peak = Math.Max(_peak, rms);
                    _lastVoiced = _fed;
                    if (_fed - _onset >= OnsetSeconds * Rate) StartPiece();
                    break;
                case 2: // speech
                    if (voiced)
                    {
                        _peak = Math.Max(_peak, rms);
                        _lastVoiced = _fed;
                        if (_fed - _winStart >= MaxSeconds * Rate) // still talking: the first MaxSeconds go now (the phrase is in front)
                        {
                            Emit(_winStart - (long)(PreRollSeconds * Rate), _winStart + (long)(MaxSeconds * Rate), true, Math.Max(_peak, _winPeak), _winStart != _onset);
                            _state = 3;
                            _quietSince = -1;
                            _prevEnd = -1; // nothing joins a cut segment
                        }
                    }
                    else if (_fed - _lastVoiced >= HangSeconds * Rate)
                    {
                        _state = 0;
                        EndPiece();
                    }
                    break;
                case 3: // after a cut: the rest of that utterance is not checked again
                    if (voiced) _quietSince = -1;
                    else if (_quietSince < 0) _quietSince = start;
                    else if (_fed - _quietSince >= HangSeconds * Rate) _state = 0;
                    break;
            }
        }

        /// A piece of speech begins (voiced for OnsetSeconds): it starts its own segment, or one a short piece began.
        private void StartPiece()
        {
            _state = 2;
            _near = _prevEnd >= 0 && _onset - _prevEnd <= (long)(MergeSeconds * Rate) && _prevStart >= _floor;
            bool fromShort = _near && !_prevChecked;
            _winStart = fromShort ? _prevStart : _onset;
            _winPeak = fromShort ? _prevPeak : 0f;
        }

        /// A piece of speech ended (HangSeconds of quiet): a segment when it is long enough alone (with a short piece
        /// before it), or when it is short and joins the piece before.
        private void EndPiece()
        {
            long min = (long)(MinSeconds * Rate), max = (long)(MaxSeconds * Rate), pre = (long)(PreRollSeconds * Rate), tail = (long)(TailSeconds * Rate);
            float peak = Math.Max(_peak, _winPeak);
            if (_lastVoiced - _onset >= min || _winStart != _onset && _lastVoiced - _winStart >= min)
            {
                // long enough: this piece (and a short one before it)
                Emit(_winStart - pre, _lastVoiced + tail, false, peak, _winStart != _onset);
                _prevChecked = true;
                _prevStart = _winStart;
                _prevPeak = peak;
            }
            else if (_near && _prevChecked && _lastVoiced - _prevStart <= max)
            {
                // a short piece right after a checked one ("heyyy", a pause, "whisp"): the two go again as one
                peak = Math.Max(_peak, _prevPeak);
                Emit(_prevStart - pre, _lastVoiced + tail, false, peak, true);
                _prevPeak = peak;
            }
            else if (_winStart != _onset && _lastVoiced - _winStart <= max)
            {
                // short pieces in a row, still too short together: kept as the start of the next segment
                _prevChecked = false;
                _prevStart = _winStart;
                _prevPeak = peak;
            }
            else
            {
                // a short piece alone: the start of the next segment if one follows soon
                _prevChecked = false;
                _prevStart = _onset;
                _prevPeak = _peak;
            }
            _prevEnd = _lastVoiced;
        }

        private void Emit(long from, long to, bool cut, float peak, bool merged)
        {
            if (peak < MinPeak) return; // far away
            if (from < _floor) from = _floor;
            if (to > _fed) to = _fed;
            if (to - from > _seg.Length) from = to - _seg.Length;
            if (to <= from) return;
            int cap = _ring.Length;
            int r = (int)(from % cap);
            int n = (int)(to - from);
            for (int i = 0; i < n; i++) { _seg[i] = _ring[r]; if (++r == cap) r = 0; }
            SegmentStart = from;
            SegmentEnd = to;
            SegmentCut = cut;
            SegmentPeak = peak;
            SegmentMerged = merged;
            long first = Math.Max(from + (long)(PreRollSeconds * Rate), from);
            SegmentVoiced = cut ? MaxSeconds : Math.Max(0, (to - (long)(TailSeconds * Rate)) - first) / (float)Rate;
            HasSegment = true;
            Segments++;
            if (merged) Merges++;
        }

        /// The ready segment as a 16 kHz mono 16-bit WAV (what the wake check takes); allocates the WAV only.
        public byte[] SegmentWav16k()
        {
            int n = SegmentLength;
            if (!HasSegment || n <= 0) return null;
            int m = Rate == 16000 ? n : (int)((long)n * 16000 / Rate);
            var pcm = new short[m];
            double step = Rate / 16000.0;
            for (int i = 0; i < m; i++)
            {
                double x = i * step;
                int i0 = (int)x;
                int i1 = Math.Min(i0 + 1, n - 1);
                float f = (float)(x - i0);
                float v = _seg[i0] * (1f - f) + _seg[i1] * f;
                int s = (int)Math.Round(v * 32767f);
                pcm[i] = (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
            }
            return Wav(pcm, 16000);
        }

        /// A mono 16-bit PCM WAV (the same bytes as VoiceRecorder.Wav; here so the gate needs no Unity: tests/voicefx).
        public static byte[] Wav(short[] pcm, int rate)
        {
            var b = new byte[44 + pcm.Length * 2];
            void Str(int o, string t) { for (int i = 0; i < t.Length; i++) b[o + i] = (byte)t[i]; }
            void U32(int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
            void U16(int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
            Str(0, "RIFF"); U32(4, 36 + pcm.Length * 2); Str(8, "WAVE"); Str(12, "fmt ");
            U32(16, 16); U16(20, 1); U16(22, 1); U32(24, rate); U32(28, rate * 2); U16(32, 2); U16(34, 16);
            Str(36, "data"); U32(40, pcm.Length * 2);
            Buffer.BlockCopy(pcm, 0, b, 44, pcm.Length * 2);
            return b;
        }
    }

    /// How many wake checks may go (a token bucket, no Unity): Burst in a row, one more every RefillSeconds. The last
    /// Reserve tokens are kept for a segment the size of the phrase alone, so talk nearby (long segments) cannot use up
    /// the check for a "Hey Whisp" that comes right after it. At most Burst + seconds / RefillSeconds checks.
    public sealed class WakeRate
    {
        public int Burst = 4;
        public float RefillSeconds = 4f;
        public float Reserve = 1f;
        public float Tokens { get; private set; } = 4f;
        private float _at = float.NaN;

        public void Reset(float now) { Tokens = Burst; _at = now; }

        /// A segment may go now (`phraseLike`: WakeGate.SegmentPhraseLike); takes its token when it may.
        public bool Take(float now, bool phraseLike)
        {
            if (float.IsNaN(_at)) Reset(now);
            Tokens = Math.Min(Burst, Tokens + Math.Max(0f, now - _at) / RefillSeconds);
            _at = now;
            if (Tokens < (phraseLike ? 1f : 1f + Reserve)) return false;
            Tokens -= 1f;
            return true;
        }
    }
}
