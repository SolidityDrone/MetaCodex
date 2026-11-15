using System;

namespace MetaCodex.AI
{
    /// The cheap voice gate in front of the "Hey Whisp" check: the energy of each block of mono samples against a
    /// noise floor that follows the quiet blocks (the rule of VoiceRecorder's end of speech). No allocation per block:
    /// the last seconds sit in a ring made once. When a voiced stretch of MinSeconds-MaxSeconds ends, or is still going
    /// at MaxSeconds (Cut), the gate has a segment: those samples with PreRollSeconds before (a soft "h") and
    /// TailSeconds after. Live audio (WakeListener) and the WAV test path (`voice wake test`) go through this code.
    public sealed class WakeGate
    {
        // tuning, in one place
        /// A block below this RMS is never speech (VoiceRecorder's floor).
        public float MinLevel = 0.012f;
        /// Speech: RMS above the noise floor times this (as VoiceRecorder).
        public float NoiseRatio = 3.5f;
        /// The loudest block of a segment must reach this: quieter voices are far away (not the wearer).
        public float MinPeak = 0.02f;
        /// Voiced this long before a segment starts (a click or a tap is shorter).
        public float OnsetSeconds = 0.06f;
        /// Unvoiced this long ends the segment (a short pause inside "Hey Whisp" does not).
        public float HangSeconds = 0.3f;
        public float MinSeconds = 0.4f, MaxSeconds = 2.5f;
        public float PreRollSeconds = 0.25f, TailSeconds = 0.1f;

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
        public float SegmentPeak { get; private set; }
        public int SegmentLength => (int)(SegmentEnd - SegmentStart);
        public int Segments { get; private set; }

        private float[] _ring = new float[0], _seg = new float[0];
        private long _fed, _floor, _onset, _lastVoiced, _quietSince;
        private int _state; // 0 quiet, 1 onset, 2 speech, 3 cut (waits for quiet)
        private float _peak;

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
                    if (_fed - _onset >= OnsetSeconds * Rate) _state = 2;
                    break;
                case 2: // speech
                    if (voiced)
                    {
                        _peak = Math.Max(_peak, rms);
                        _lastVoiced = _fed;
                        if (_fed - _onset >= MaxSeconds * Rate) // still talking: the first MaxSeconds go now (the phrase is in front)
                        {
                            Emit(_onset - (long)(PreRollSeconds * Rate), _onset + (long)(MaxSeconds * Rate), true);
                            _state = 3;
                            _quietSince = -1;
                        }
                    }
                    else if (_fed - _lastVoiced >= HangSeconds * Rate)
                    {
                        _state = 0;
                        if (_lastVoiced - _onset >= MinSeconds * Rate) Emit(_onset - (long)(PreRollSeconds * Rate), _lastVoiced + (long)(TailSeconds * Rate), false);
                    }
                    break;
                case 3: // after a cut: the rest of that utterance is not checked again
                    if (voiced) _quietSince = -1;
                    else if (_quietSince < 0) _quietSince = start;
                    else if (_fed - _quietSince >= HangSeconds * Rate) _state = 0;
                    break;
            }
        }

        private void Emit(long from, long to, bool cut)
        {
            if (_peak < MinPeak) return; // far away
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
            SegmentPeak = _peak;
            HasSegment = true;
            Segments++;
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
            return VoiceRecorder.Wav(pcm, 16000);
        }
    }
}
