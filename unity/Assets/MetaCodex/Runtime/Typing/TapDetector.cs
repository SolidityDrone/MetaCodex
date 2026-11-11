using System;
using System.Collections.Generic;

namespace MetaCodex.Typing
{
    /// One fingertip in one frame, on the keyboard's plane.
    public struct FingerSample
    {
        public bool Tracked;
        /// Metres from the key block's top-left corner (x to the right, y towards the user).
        public float X, Y;
        /// Metres above the keyboard's plane (the table).
        public float H;
    }

    /// A finger came down on the table like a key press.
    public struct TapEvent
    {
        /// hand * 5 + finger: 0 left thumb .. 4 left little, 5 right thumb .. 9 right little.
        public int Finger;
        public float Time;
        /// Where the tip came down (key block coordinates, metres).
        public float X, Y;
        /// The fastest it came down (m/s) and how high over the table it came from (m).
        public float Speed, Lift;
        /// Seen only after it lifted again (the table was higher than believed): typed late, once.
        public bool Late;
    }

    /// Which finger pressed, and when: ten fingertips over a table, from hand tracking, to key presses.
    ///
    /// How Meta's Surface Keyboard does it (TouchInsight, Meta Reality Labs + ETH Zurich, UIST 2024, and the shipped
    /// Experimental feature): the table is calibrated once with the hands on it, hands are tracked 30 times a second,
    /// and a network trained on 5.3 million recorded presses says per frame which finger touched, or none (F1 0.99 for
    /// the touch, 0.96 for the finger). There is no such training set here, so this is that decision written as rules,
    /// each one for a failure seen in the old height threshold:
    ///
    /// - The table stops a finger. Every finger has its own floor: the height its tip is tracked at when it touches
    ///   (not one height for the hand: tips differ, and the tracked height of a tip drifts by millimetres). The floor
    ///   follows the lowest heights down at once and comes back up only to the bottom of that finger's own last press.
    /// - A press is a stroke: the tip was lifted over the floor, comes down fast and arrives. A finger that sinks
    ///   slowly has come to rest (no key); a resting finger types nothing until it is lifted again.
    /// - One finger presses, its neighbours follow (they share tendons). When another finger of the hand is coming
    ///   down too, the press waits a moment: of two that land together the stronger stroke is the key, three or more
    ///   are a hand being put down. Right after a press the hand's other fingers cannot type.
    /// - A hand that comes down as a whole (its palm sinking, other fingers low) types nothing, however its fingers
    ///   are staggered.
    /// - Speeds are measured over 42 ms, never from one frame to the next: with 30 poses a second most frames repeat
    ///   or extrapolate the last pose.
    /// - A press alone fires at once (the old keyboard always waited 55 ms): it is seen a few millimetres before the
    ///   table, which takes back part of the tracking delay.
    /// - A stroke that stops short of the floor and lifts again like a press means the floor is too low (tracking
    ///   drifted): the floor moves up and that press is typed late, once.
    ///
    /// No Unity types: tests/typing runs it against a simulated typist with tracking noise.
    public sealed class TapDetector
    {
        public const int Count = 10;
        private const int N = 64;

        // ---- what a press is (metres, seconds, metres per second)
        /// A tip this close to its floor has arrived.
        public float ContactTol = 0.004f;
        /// A finger types again once it has been lifted this far over its floor.
        public float ArmHeight = 0.0065f;
        /// The fastest part of a stroke comes down at least this fast (measured over 42 ms).
        public float MinSpeed = 0.07f;
        /// The longest a stroke takes from the lift to the table.
        public float MaxFall = 0.45f;
        /// A tip that slides sideways faster than this is not pressing (a hand moving, a tracking jump).
        public float MaxSlide = 0.15f;
        /// How long a press waits when another finger of the hand is coming down with it.
        public float HoldTime = 0.040f, MaxHold = 0.09f;
        /// Of three fingers that land together, one this many times stronger than the next is a press with followers.
        public float LeadRatio = 1.6f;
        /// How long the hand's other fingers cannot type after a press, and how long one that was coming down with
        /// the pressing finger cannot.
        public float InhibitTime = 0.060f, FollowerQuiet = 0.08f;
        /// A tip that stays at the table this long without a stroke has come to rest.
        public float SettleTime = 0.08f;
        /// A stroke that stops this near the floor and lifts again was a press on a table that is higher than believed.
        public float StopTol = 0.011f;
        /// The floor follows a lower tip down no faster than this (a tracking jump does not move it far).
        public float FloorFall = 0.04f;
        /// The floor comes back up to the bottom of the last press this fast.
        public float FloorRise = 0.002f;
        /// A lifted finger this near the table shows which key it is over.
        public float HoverHeight = 0.035f;
        /// A palm sinking this fast with other fingers this near the table is a hand being put down.
        public float PalmFall = 0.08f, PalmNear = 0.012f;
        /// Fingers lying still together within this of their floors are resting on the table.
        public float RestBand = 0.0065f;

        private const float Window = 0.042f, PeakWindow = 0.16f, TopWindow = 0.35f, CalibrationBias = 0.0015f;

        private readonly float[][] _t = new float[Count][], _h = new float[Count][], _x = new float[Count][], _y = new float[Count][], _v = new float[Count][];
        private readonly int[] _n = new int[Count], _head = new int[Count], _still = new int[Count];
        private readonly bool[] _armed = new bool[Count], _down = new bool[Count], _hasFloor = new bool[Count], _hasBottom = new bool[Count], _shallow = new bool[Count];
        private readonly float[] _floor = new float[Count], _hs = new float[Count], _xs = new float[Count], _ys = new float[Count], _raw1 = new float[Count], _raw2 = new float[Count];
        private readonly float[] _armT = new float[Count], _settleT = new float[Count], _tapT = new float[Count], _bottom = new float[Count], _bottomRun = new float[Count];
        private readonly float[] _vNow = new float[Count], _shT = new float[Count], _shH = new float[Count], _shX = new float[Count], _shY = new float[Count], _shPeak = new float[Count];
        private readonly bool[] _bottomOpen = new bool[Count];
        private readonly float[] _quietUntil = new float[Count], _landT = new float[Count], _peakNow = new float[Count], _topNow = new float[Count], _slideNow = new float[Count];
        // per hand
        private readonly float[] _inhibitUntil = { -10f, -10f }, _deadline = new float[2], _firstPend = new float[2], _restSince = new float[2], _restT = new float[2];
        private readonly TapEvent[][] _pending = { new TapEvent[5], new TapEvent[5] };
        private readonly int[] _pendN = new int[2];
        private readonly float[][] _palmT = { new float[N], new float[N] }, _palmH = { new float[N], new float[N] };
        private readonly int[] _palmN = new int[2], _palmHead = new int[2];
        private float _lastT, _lastEmitT = -10f;
        private bool _hasT;

        /// Self-tests and the recorder: why presses were not typed.
        public int Landings { get; private set; }
        public int Inhibited { get; private set; }
        public int Settled { get; private set; }
        public int Rested { get; private set; }

        public TapDetector()
        {
            for (int i = 0; i < Count; i++)
            {
                _t[i] = new float[N]; _h[i] = new float[N]; _x[i] = new float[N]; _y[i] = new float[N]; _v[i] = new float[N];
                _settleT[i] = -1f; _armT[i] = -10f; _tapT[i] = -10f; _quietUntil[i] = -10f; _landT[i] = -10f;
            }
        }

        public void Reset()
        {
            for (int i = 0; i < Count; i++)
            {
                Forget(i);
                _hasFloor[i] = false; _hasBottom[i] = false;
                _armT[i] = -10f; _tapT[i] = -10f; _quietUntil[i] = -10f; _landT[i] = -10f; _shT[i] = -10f;
            }
            for (int h = 0; h < 2; h++) { _pendN[h] = 0; _inhibitUntil[h] = -10f; _palmN[h] = 0; _restSince[h] = 0f; }
            _hasT = false;
            _lastEmitT = -10f;
        }

        /// The hands rest flat on the table: this finger's tip is at `tipHeight` over the keyboard's plane while it
        /// touches. A little under it: a curled finger touches with its very tip, lower than a flat one.
        public void Calibrate(int finger, float tipHeight, float blend = 1f)
        {
            float f = tipHeight - CalibrationBias;
            _floor[finger] = _hasFloor[finger] ? _floor[finger] + (f - _floor[finger]) * Clamp01(blend) : f;
            _hasFloor[finger] = true;
            if (blend >= 1f) { _hasBottom[finger] = false; _down[finger] = true; _armed[finger] = false; _shallow[finger] = false; }
        }

        public bool Seen(int finger) => _n[finger] > 0;
        public float Floor(int finger) => _floor[finger];
        /// Metres over its floor now (smoothed).
        public float Rel(int finger) => _n[finger] > 0 ? _hs[finger] - _floor[finger] : float.MaxValue;
        public float X(int finger) => _xs[finger];
        public float Y(int finger) => _ys[finger];
        /// Lifted and near the table: the key under it is the one it would press.
        public bool Hovering(int finger) => _n[finger] > 0 && _armed[finger] && !_down[finger] && Rel(finger) < HoverHeight;
        public bool Down(int finger) => _n[finger] > 0 && _down[finger];
        /// Resting on the table or pressing it (for the touchpad: with its own slack).
        public bool Touching(int finger, float slack) => _n[finger] > 0 && Rel(finger) <= ContactTol + slack;

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private void Forget(int i)
        {
            _n[i] = 0; _head[i] = 0; _armed[i] = false; _down[i] = false; _settleT[i] = -1f; _still[i] = 0; _shallow[i] = false; _bottomOpen[i] = false;
        }

        private static float Median(float a, float b, float c) => a > b ? (b > c ? b : a > c ? c : a) : (a > c ? a : b > c ? c : b);

        private void Push(int i, float t, float h, float x, float y)
        {
            int k = _head[i];
            _t[i][k] = t; _h[i][k] = h; _x[i][k] = x; _y[i][k] = y; _v[i][k] = 0f;
            _head[i] = (k + 1) % N;
            if (_n[i] < N) _n[i]++;
        }

        private int At(int i, int back) => ((_head[i] - 1 - back) % N + N) % N; // back = 0: the newest

        /// The tip `ago` seconds before the newest sample (interpolated). False: not that much history yet.
        private bool Past(int i, float ago, out float h, out float x, out float y)
        {
            h = x = y = 0f;
            int n = _n[i];
            if (n < 2) return false;
            float want = _t[i][At(i, 0)] - ago;
            for (int b = 1; b < n; b++)
            {
                int k = At(i, b);
                if (_t[i][k] > want) continue;
                int k1 = At(i, b - 1);
                float span = _t[i][k1] - _t[i][k];
                float u = span > 1e-6f ? (want - _t[i][k]) / span : 0f;
                h = _h[i][k] + (_h[i][k1] - _h[i][k]) * u;
                x = _x[i][k] + (_x[i][k1] - _x[i][k]) * u;
                y = _y[i][k] + (_y[i][k1] - _y[i][k]) * u;
                return true;
            }
            return false;
        }

        /// The fastest fall and the highest place within the last `window` seconds.
        private void Recent(int i, float speedWindow, float topWindow, out float peak, out float top)
        {
            peak = 0f; top = float.MinValue;
            int n = _n[i];
            float now = _t[i][At(i, 0)];
            for (int b = 0; b < n; b++)
            {
                int k = At(i, b);
                float age = now - _t[i][k];
                if (age > topWindow && age > speedWindow) break;
                if (age <= speedWindow && _v[i][k] > peak) peak = _v[i][k];
                if (age <= topWindow && _h[i][k] > top) top = _h[i][k];
            }
        }

        private void PushPalm(int hand, float t, float h)
        {
            if (float.IsNaN(h)) { _palmN[hand] = 0; return; }
            int k = _palmHead[hand];
            _palmT[hand][k] = t; _palmH[hand][k] = h;
            _palmHead[hand] = (k + 1) % N;
            if (_palmN[hand] < N) _palmN[hand]++;
        }

        /// How fast the palm is sinking (m/s over 0.1 s; 0 when unknown).
        private float PalmSinking(int hand)
        {
            int n = _palmN[hand];
            if (n < 3) return 0f;
            int newest = ((_palmHead[hand] - 1) % N + N) % N;
            float now = _palmT[hand][newest];
            for (int b = 1; b < n; b++)
            {
                int k = ((_palmHead[hand] - 1 - b) % N + N) % N;
                float age = now - _palmT[hand][k];
                if (age >= 0.1f) return (_palmH[hand][k] - _palmH[hand][newest]) / age;
            }
            return 0f;
        }

        /// One frame: where the ten tips are (index = hand * 5 + finger), how high each palm is over the plane
        /// (NaN: unknown). Presses seen in this frame are added to `output`, the earliest first.
        public void Step(float t, FingerSample[] tips, float palmLeft, float palmRight, List<TapEvent> output)
        {
            float dt = _hasT ? Math.Min(Math.Max(t - _lastT, 1e-3f), 0.1f) : 1f / 72f;
            _lastT = t;
            _hasT = true;
            PushPalm(0, t, palmLeft);
            PushPalm(1, t, palmRight);
            // all ten are measured before any is judged: a finger that lands is judged against where the others are
            // in this frame, not the last one
            for (int i = 0; i < Count; i++) Measure(i, t, dt, tips[i]);
            for (int i = 0; i < Count; i++) Decide(i, t, output);
            for (int hand = 0; hand < 2; hand++) { Resolve(hand, t, output); Rest(hand, t); }
        }

        /// Three or more fingers of a hand lie still at the same small height over their floors for 0.4 s in all:
        /// they rest on the table, and the table is that much higher than believed (the tracked heights drifted).
        /// The floors of the hand move up to them. (Fingers held still that level just over the table are rare, and
        /// then the next press of each puts its floor back.)
        private void Rest(int hand, float t)
        {
            int n = 0;
            float lo = float.MaxValue, hi = float.MinValue, sum = 0f;
            for (int j = hand * 5 + 1; j < hand * 5 + 5; j++)
            {
                if (_n[j] == 0 || Math.Abs(_vNow[j]) > 0.03f) continue;
                float rel = _hs[j] - _floor[j];
                if (rel < -0.002f || rel > RestBand) continue;
                n++; sum += rel;
                if (rel < lo) lo = rel;
                if (rel > hi) hi = rel;
            }
            // counted up while it holds and down while it does not: a press in between does not start it over
            float dt = t - _restT[hand];
            _restT[hand] = t;
            if (dt < 0f || dt > 0.1f) dt = 0f;
            if (n < 3 || hi - lo > 0.0025f || sum / n < 0.0015f) { _restSince[hand] = Math.Max(0f, _restSince[hand] - dt); return; }
            _restSince[hand] += dt;
            if (_restSince[hand] < 0.4f) return;
            _restSince[hand] = 0f;
            float shift = sum / n - 0.0005f;
            for (int j = hand * 5; j < hand * 5 + 5; j++)
            {
                if (!_hasFloor[j]) continue;
                _floor[j] += shift;
                if (_n[j] > 0 && _hs[j] - _floor[j] <= ContactTol) { _down[j] = true; _armed[j] = false; }
            }
            Rested++;
        }

        private void Measure(int i, float t, float dt, FingerSample s)
        {
            if (!s.Tracked) { Forget(i); return; }
            // one wild frame is dropped (the middle of the last three), then a light smoothing
            float med = _n[i] >= 2 ? Median(s.H, _raw1[i], _raw2[i]) : s.H;
            _raw2[i] = _n[i] >= 1 ? _raw1[i] : s.H;
            _raw1[i] = s.H;
            if (_n[i] == 0) { _hs[i] = med; _xs[i] = s.X; _ys[i] = s.Y; }
            else
            {
                float a = 1f - (float)Math.Exp(-dt / 0.012f), b = 1f - (float)Math.Exp(-dt / 0.020f);
                _hs[i] += (med - _hs[i]) * a;
                _xs[i] += (s.X - _xs[i]) * b;
                _ys[i] += (s.Y - _ys[i]) * b;
            }
            float hs = _hs[i];
            Push(i, t, hs, _xs[i], _ys[i]);

            // the floor: down with the lowest heights, up only to the bottom of this finger's last press
            if (!_hasFloor[i]) { _floor[i] = hs; _hasFloor[i] = true; }
            if (hs < _floor[i]) _floor[i] = Math.Max(hs, _floor[i] - FloorFall * dt);
            else if (_hasBottom[i] && _bottom[i] > _floor[i]) _floor[i] = Math.Min(_bottom[i], _floor[i] + FloorRise * dt);
            float rel = hs - _floor[i];

            float v = 0f, slide = 0f;
            if (Past(i, Window, out float hp, out float xp, out float yp))
            {
                v = (hp - hs) / Window; // > 0: coming down
                float dx = _xs[i] - xp, dy = _ys[i] - yp;
                slide = (float)Math.Sqrt(dx * dx + dy * dy) / Window;
            }
            _v[i][At(i, 0)] = v;
            _vNow[i] = v;
            _slideNow[i] = slide;
            Recent(i, PeakWindow, TopWindow, out float peak, out float top);
            _peakNow[i] = peak;
            _topNow[i] = top;

            // the bottom of a press: the lowest the tip gets in the 0.15 s after it
            if (_bottomOpen[i])
            {
                if (hs < _bottomRun[i]) _bottomRun[i] = hs;
                if (t - _tapT[i] > 0.15f || rel > ArmHeight) { _bottom[i] = _bottomRun[i]; _hasBottom[i] = true; _bottomOpen[i] = false; }
            }
        }

        private void Decide(int i, float t, List<TapEvent> output)
        {
            if (_n[i] == 0) return;
            float hs = _hs[i], rel = hs - _floor[i], v = _vNow[i], slide = _slideNow[i], peak = _peakNow[i], top = _topNow[i];
            if (rel > ArmHeight) { _armed[i] = true; _down[i] = false; _armT[i] = t; _settleT[i] = -1f; }
            if (_down[i] || !_armed[i]) { _shallow[i] = false; _still[i] = 0; return; }

            if (rel <= ContactTol)
            {
                _shallow[i] = false; _still[i] = 0;
                bool stroke = peak >= MinSpeed && t - _armT[i] <= MaxFall;
                if (stroke && slide > MaxSlide) { _down[i] = true; _armed[i] = false; _landT[i] = t; Settled++; } // slid in: a hand moving, a tracking jump
                else if (stroke) Land(i, t, new TapEvent { Finger = i, Time = t, X = _xs[i], Y = _ys[i], Speed = peak, Lift = top - _floor[i] }, output);
                else if (_settleT[i] < 0f) _settleT[i] = t;
                else if (t - _settleT[i] >= SettleTime) { _down[i] = true; _armed[i] = false; Settled++; } // came to rest: no key
                return;
            }
            _settleT[i] = -1f;
            Shallow(i, t, rel, v, slide, peak, top, output);
        }

        /// A stroke that stopped short of the floor and lifts again like a press: the table is higher than believed.
        private void Shallow(int i, float t, float rel, float v, float slide, float peak, float top, List<TapEvent> output)
        {
            float hs = _hs[i];
            if (!_shallow[i])
            {
                bool stopped = rel <= StopTol && Math.Abs(v) < 0.02f && peak >= MinSpeed && top - hs >= 0.005f && slide <= MaxSlide;
                _still[i] = stopped ? _still[i] + 1 : 0;
                if (_still[i] >= 2) { _shallow[i] = true; _shT[i] = t; _shH[i] = hs; _shX[i] = _xs[i]; _shY[i] = _ys[i]; _shPeak[i] = peak; }
                return;
            }
            if (hs < _shH[i]) _shH[i] = hs;
            float age = t - _shT[i];
            if (age > 0.30f) { _shallow[i] = false; _still[i] = -1000; return; } // it stays there: a finger held over the keys
            if (hs - _shH[i] < 0.005f || -v < MinSpeed) return;
            _shallow[i] = false;
            _still[i] = 0;
            if (age < 0.03f) return;
            // it only followed a finger of its hand that pressed: no press of its own, and the floor is where it was
            for (int j = (i / 5) * 5; j < (i / 5) * 5 + 5; j++)
                if (j != i && _tapT[j] >= _shT[i] - 0.15f) return;
            _floor[i] = _shH[i] - 0.0005f;
            _bottom[i] = _shH[i];
            _hasBottom[i] = true;
            if (_lastEmitT < _shT[i]) // nothing typed since: it is still in order
            {
                int hand = i / 5;
                var ev = new TapEvent { Finger = i, Time = t, X = _shX[i], Y = _shY[i], Speed = _shPeak[i], Lift = top - _floor[i], Late = true };
                output.Add(ev);
                _lastEmitT = t;
                if (i % 5 != 0) _inhibitUntil[hand] = t + InhibitTime;
            }
        }

        /// A finger arrived with a stroke: a key, unless it only followed another finger or the hand is being put down.
        private void Land(int i, float t, TapEvent ev, List<TapEvent> output)
        {
            _down[i] = true;
            _armed[i] = false;
            _tapT[i] = t;
            _landT[i] = t;
            int hand = i / 5;
            bool thumb = i % 5 == 0;
            if (!thumb && (t < _inhibitUntil[hand] || t < _quietUntil[i])) { Inhibited++; return; }
            if (HandComingDown(hand, i)) { Landings++; return; }
            if (_pendN[hand] > 0 || Contender(hand, i))
            {
                if (_pendN[hand] == 0) { _deadline[hand] = t + HoldTime; _firstPend[hand] = t; }
                if (_pendN[hand] < 5) _pending[hand][_pendN[hand]++] = ev;
                return;
            }
            Emit(ev, output);
        }

        private void Emit(TapEvent ev, List<TapEvent> output)
        {
            output.Add(ev);
            _lastEmitT = ev.Time;
            // this finger is on the table now: the lowest it gets is where its floor is (a follower's lowest is not)
            _bottomRun[ev.Finger] = _hs[ev.Finger];
            _bottomOpen[ev.Finger] = true;
            _tapT[ev.Finger] = _lastT;
            if (ev.Finger % 5 == 0) return;
            int hand = ev.Finger / 5;
            float now = _lastT;
            _inhibitUntil[hand] = now + InhibitTime;
            // a finger that was on its way down with this one followed it: it stays quiet for longer
            for (int j = hand * 5 + 1; j < hand * 5 + 5; j++)
                if (j != ev.Finger && _n[j] > 0 && _armed[j] && !_down[j] && _hs[j] - _floor[j] <= ContactTol + 0.008f && _vNow[j] >= 0f) _quietUntil[j] = now + FollowerQuiet;
        }

        /// Another finger of the hand is lifted, near the table and coming down.
        private bool Contender(int hand, int self)
        {
            for (int j = hand * 5; j < hand * 5 + 5; j++)
            {
                if (j == self || _n[j] == 0 || !_armed[j] || _down[j]) continue;
                if (_hs[j] - _floor[j] <= ContactTol + 0.008f && _vNow[j] >= 0.03f) return true;
            }
            return false;
        }

        /// The palm is sinking and two more fingers of the hand have just landed or are coming down near the table.
        private bool HandComingDown(int hand, int self)
        {
            if (PalmSinking(hand) < PalmFall) return false;
            int with = 0;
            for (int j = hand * 5 + 1; j < hand * 5 + 5; j++)
            {
                if (j == self || _n[j] == 0) continue;
                if (_down[j] ? _lastT - _landT[j] < 0.2f : _vNow[j] >= 0.05f && _hs[j] - _floor[j] < PalmNear) with++;
            }
            return with >= 2;
        }

        private void Resolve(int hand, float t, List<TapEvent> output)
        {
            int n = _pendN[hand];
            if (n == 0 || t < _deadline[hand]) return;
            // one finger has landed, a stronger stroke of the same hand is still on its way down: that one is the
            // press and this one its follower. Wait for it (90 ms at most).
            if (n == 1 && _pending[hand][0].Finger % 5 != 0 && t - _firstPend[hand] < MaxHold)
            {
                var p0 = _pending[hand][0];
                for (int j = hand * 5 + 1; j < hand * 5 + 5; j++)
                {
                    if (j == p0.Finger || _n[j] == 0 || !_armed[j] || _down[j]) continue;
                    if (_hs[j] - _floor[j] <= ContactTol + 0.008f && _vNow[j] >= 0.03f && _peakNow[j] * (_topNow[j] - _floor[j]) > p0.Speed * p0.Lift * 1.3f) return;
                }
            }
            _pendN[hand] = 0;
            int best = -1, second = -1, thumb = -1;
            for (int k = 0; k < n; k++)
            {
                var p = _pending[hand][k];
                if (p.Finger % 5 == 0) { thumb = k; continue; }
                float sc = p.Speed * p.Lift;
                if (best < 0 || sc > _pending[hand][best].Speed * _pending[hand][best].Lift) { second = best; best = k; }
                else if (second < 0 || sc > _pending[hand][second].Speed * _pending[hand][second].Lift) second = k;
            }
            // three or more together: a hand put down, unless one stroke is far stronger than the rest (a press whose
            // neighbours touched the table with it)
            if (n >= 3 && !(best >= 0 && second >= 0 && _pending[hand][best].Speed * _pending[hand][best].Lift >= LeadRatio * _pending[hand][second].Speed * _pending[hand][second].Lift))
            { Landings++; return; }
            if (n >= 3) thumb = -1;
            if (best >= 0 && thumb >= 0 && _pending[hand][thumb].Time > _pending[hand][best].Time) { Emit(_pending[hand][best], output); Emit(_pending[hand][thumb], output); }
            else
            {
                if (thumb >= 0) Emit(_pending[hand][thumb], output);
                if (best >= 0) Emit(_pending[hand][best], output);
            }
            if (n - (thumb >= 0 ? 1 : 0) > 1) Inhibited++;
        }
    }
}
