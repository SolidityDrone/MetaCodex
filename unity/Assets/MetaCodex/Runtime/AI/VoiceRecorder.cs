using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace MetaCodex.AI
{
    /// Push-to-talk capture from the headset microphone: mono samples while recording, a level meter, and an
    /// end-of-speech detector (stop after ~1 s of silence that follows speech). The result is a 16 kHz mono
    /// 16-bit WAV, what speech-to-text wants. Main thread only (Unity's Microphone API); call Tick each frame.
    public sealed class VoiceRecorder
    {
        public bool Recording { get; private set; }
        /// Loudness of the last chunk, 0..1 (for the level bar).
        public float Level { get; private set; }
        public float Seconds => _rate > 0 ? _samples.Count / (float)_rate : 0f;
        public bool HeardSpeech { get; private set; }
        public float MaxSeconds = 12f;
        public bool AutoStop = true;
        public float SilenceSeconds = 1.0f;
        /// Give up when nothing was said for this long.
        public float NoSpeechSeconds = 6f;
        public string LastError { get; private set; }
        /// Raised when recording ends by itself (silence, time limit); null when no speech was heard.
        public event Action<byte[]> Finished;

        private AudioClip _clip;
        private string _device;
        private int _rate, _readPos;
        private readonly List<float> _samples = new List<float>();
        private float _speech, _silence, _noise = 0.004f;
        private float[] _buf = new float[0];

        public static bool HasPermission
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
                return true;
#endif
            }
        }

        /// Asks for RECORD_AUDIO (Horizon OS shows its dialog); done(true) when granted.
        public static void RequestPermission(Action<bool> done)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var cb = new PermissionCallbacks();
            cb.PermissionGranted += _ => done(true);
            cb.PermissionDenied += _ => done(false);
#pragma warning disable CS0618
            cb.PermissionDeniedAndDontAskAgain += _ => done(false);
#pragma warning restore CS0618
            Permission.RequestUserPermission(Permission.Microphone, cb);
#else
            done(true);
#endif
        }

        public bool Start()
        {
            if (Recording) return true;
            LastError = null;
            if (Microphone.devices.Length == 0) { LastError = "no microphone found"; return false; }
            _device = null; // the default input
            Microphone.GetDeviceCaps(_device, out int min, out int max);
            int want = min == 0 && max == 0 ? 16000 : Mathf.Clamp(16000, min, max);
            _clip = Microphone.Start(_device, true, 20, want); // a looping 20 s ring buffer
            if (_clip == null) { LastError = "the microphone did not start"; return false; }
            _rate = _clip.frequency;
            _readPos = 0;
            _samples.Clear();
            _speech = _silence = 0f;
            HeardSpeech = false;
            Level = 0f;
            Recording = true;
            return true;
        }

        public void Tick()
        {
            if (!Recording || _clip == null) return;
            int pos = Microphone.GetPosition(_device);
            if (pos < 0) return;
            int n = pos - _readPos;
            if (n < 0) n += _clip.samples;
            if (n <= 0) return;
            int ch = Mathf.Max(1, _clip.channels);
            if (_buf.Length != n * ch) _buf = new float[n * ch];
            _clip.GetData(_buf, _readPos); // wraps around the ring buffer by itself
            _readPos = pos;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float v = 0f;
                for (int c = 0; c < ch; c++) v += _buf[i * ch + c];
                v /= ch;
                _samples.Add(v);
                sum += v * v;
            }
            float rms = Mathf.Sqrt((float)(sum / n));
            float chunk = n / (float)_rate;
            Level = Mathf.Clamp01(Mathf.Max(rms * 8f, Level * 0.85f));
            // end-of-speech: speech is clearly above the room's noise floor, which follows the quietest chunks
            bool speaking = rms > Mathf.Max(0.012f, _noise * 3.5f);
            if (speaking) { _speech += chunk; _silence = 0f; if (_speech > 0.15f) HeardSpeech = true; }
            else { _silence += chunk; _noise = Mathf.Lerp(_noise, rms, 0.05f); }
            if (rms < _noise) _noise = Mathf.Lerp(_noise, rms, 0.3f);

            if (AutoStop && HeardSpeech && _silence >= SilenceSeconds) Finish();
            else if (Seconds >= MaxSeconds) Finish();
            else if (!HeardSpeech && Seconds >= NoSpeechSeconds) { Cancel(); Finished?.Invoke(null); }
        }

        /// Ends recording and returns the WAV (16 kHz mono 16-bit), or null when nothing was recorded.
        public byte[] Stop()
        {
            if (!Recording) return null;
            Tick(); // the last samples
            Microphone.End(_device);
            Recording = false;
            Level = 0f;
            if (_samples.Count < _rate / 5) return null; // under 0.2 s
            return Wav16k(_samples, _rate);
        }

        public void Cancel()
        {
            if (!Recording) return;
            Microphone.End(_device);
            Recording = false;
            Level = 0f;
            _samples.Clear();
        }

        private void Finish()
        {
            var wav = Stop();
            Finished?.Invoke(HeardSpeech ? wav : null);
        }

        /// Linear resampling to 16 kHz, 16-bit PCM WAV.
        public static byte[] Wav16k(IList<float> samples, int rate)
        {
            int n = rate == 16000 ? samples.Count : (int)((long)samples.Count * 16000 / rate);
            var pcm = new short[n];
            double r = rate / 16000.0;
            for (int i = 0; i < n; i++)
            {
                double x = i * r;
                int i0 = (int)x;
                int i1 = Math.Min(i0 + 1, samples.Count - 1);
                float f = (float)(x - i0);
                float v = samples[i0] * (1f - f) + samples[i1] * f;
                pcm[i] = (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32768, 32767);
            }
            return Wav(pcm, 16000);
        }

        public static byte[] Wav(short[] pcm, int rate)
        {
            var b = new byte[44 + pcm.Length * 2];
            void Str(int o, string s) { for (int i = 0; i < s.Length; i++) b[o + i] = (byte)s[i]; }
            void U32(int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
            void U16(int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
            Str(0, "RIFF"); U32(4, 36 + pcm.Length * 2); Str(8, "WAVE"); Str(12, "fmt ");
            U32(16, 16); U16(20, 1); U16(22, 1); U32(24, rate); U32(28, rate * 2); U16(32, 2); U16(34, 16);
            Str(36, "data"); U32(40, pcm.Length * 2);
            Buffer.BlockCopy(pcm, 0, b, 44, pcm.Length * 2);
            return b;
        }

        /// A 16-bit PCM WAV (as the AI service returns) as an AudioClip.
        public static AudioClip ClipFromWav(byte[] wav, string name = "speech")
        {
            if (wav == null || wav.Length < 44) return null;
            int channels = BitConverter.ToInt16(wav, 22);
            int rate = BitConverter.ToInt32(wav, 24);
            int bits = BitConverter.ToInt16(wav, 34);
            if (bits != 16 || channels < 1) return null;
            int off = 12;
            int dataOff = -1, dataLen = 0;
            while (off + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, off, 4);
                int size = BitConverter.ToInt32(wav, off + 4);
                if (id == "data") { dataOff = off + 8; dataLen = Math.Min(size, wav.Length - dataOff); break; }
                off += 8 + size + (size & 1);
            }
            if (dataOff < 0) return null;
            int frames = dataLen / 2 / channels;
            var data = new float[frames * channels];
            for (int i = 0; i < data.Length; i++) data[i] = BitConverter.ToInt16(wav, dataOff + i * 2) / 32768f;
            var clip = AudioClip.Create(name, frames, channels, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
