using System;
using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.Hands
{
    public enum Handedness { Left = 0, Right = 1 }

    /// One hand in one frame, in world space. Built from Meta's OVRHand/OVRSkeleton on the headset, or
    /// scripted by tests (SimulatedHands).
    public struct HandFrame
    {
        public bool Tracked;
        public bool HighConfidence;
        public bool SystemGesture;
        public Vector3 Wrist, Palm, ThumbTip, IndexTip, IndexKnuckle, MiddleTip, MiddleKnuckle, RingTip, RingKnuckle, LittleTip, LittleKnuckle;
        /// OVR's own pinch strength 0..1 for thumb + index; negative when unknown (use the geometry).
        public float PinchStrength;
        public bool PointerValid;
        public Vector3 PointerOrigin, PointerDirection;

        /// Out of the palm (not the back of the hand).
        public Vector3 PalmNormal(Handedness h)
        {
            var n = Vector3.Cross(IndexKnuckle - Wrist, LittleKnuckle - Wrist).normalized;
            return h == Handedness.Right ? -n : n;
        }

        /// A pointing ray: OVR's pointer pose when valid, else from the wrist through the index knuckle.
        public Ray Ray()
        {
            if (PointerValid && PointerDirection.sqrMagnitude > 0.5f) return new Ray(PointerOrigin, PointerDirection.normalized);
            var dir = (IndexKnuckle - Wrist);
            return new Ray(IndexKnuckle, dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward);
        }
    }

    public interface IHandSource
    {
        bool TryGet(Handedness hand, out HandFrame frame);
    }

    /// Thresholds and choices the user sets in Settings > Gestures (persisted with the shell settings).
    [Serializable]
    public sealed class GestureSettings
    {
        /// How a UI element is clicked: 0 pinch (with the hand ray), 1 poke (fingertip), 2 both.
        public int clickMode = 2;
        /// How a panel is moved: 0 pinch its header, 1 close the hand (fist) while pointing at it, 2 both.
        public int grabMode = 2;
        /// When the hand ray shows: 0 always, 1 only in the pointing pose.
        public int pointMode = 0;
        public float pinchOn = 0.85f, pinchOff = 0.6f;
        public bool clapEnabled = true;
        public float clapDistance = 0.075f;
        public bool rayVisible = true;
    }

    public struct GestureEvent
    {
        public string Name;     // pinch, grab, point, open, palm-up, clap, select ...
        public string Hand;     // left, right, both
        public string Phase;    // start, end, tap
        public float Time;
        public Vector3 Position;
        public string Detail;   // e.g. selection: "panel x y w h"
    }

    /// Per-hand recognised state, updated every frame.
    public sealed class HandState
    {
        public Handedness Hand;
        public HandFrame Frame;
        public bool Tracked, Pinching, Grabbing, Pointing, Open, PalmUp;
        public float Pinch;     // 0..1
        public float[] Curl = new float[4]; // index, middle, ring, little: 0 extended .. 1 curled
    }

    /// Meta's basics turned into named gestures with hysteresis: pinch (OVR pinch strength), grab (fist),
    /// point (index out, others curled), open palm, palm up, and the two-hand clap. Raises GestureEvents
    /// that the UI router and Lua scripts consume. Sequences (double clap, ...) are left to scripts.
    public sealed class GestureEngine
    {
        public static readonly string[] Names = { "pinch", "grab", "point", "open", "palm-up", "clap", "select" };
        public readonly HandState Left = new HandState { Hand = Handedness.Left };
        public readonly HandState Right = new HandState { Hand = Handedness.Right };
        public GestureSettings Settings;
        public event Action<GestureEvent> OnGesture;
        public IHandSource Source;
        public float LastClapTime { get; private set; } = -10f;
        private float _prevPalmDist = -1f, _approachSpeed;
        private bool _clapArmed = true;
        private readonly Queue<GestureEvent> _recent = new Queue<GestureEvent>();

        public GestureEngine(IHandSource source, GestureSettings settings) { Source = source; Settings = settings; }

        public HandState Get(Handedness h) => h == Handedness.Left ? Left : Right;
        public IEnumerable<GestureEvent> Recent => _recent;

        public void Tick(float time, float dt)
        {
            UpdateHand(Left, time);
            UpdateHand(Right, time);
            UpdateClap(time, dt);
        }

        private void Raise(string name, string hand, string phase, float time, Vector3 pos, string detail = null)
        {
            var e = new GestureEvent { Name = name, Hand = hand, Phase = phase, Time = time, Position = pos, Detail = detail };
            _recent.Enqueue(e);
            while (_recent.Count > 12) _recent.Dequeue();
            OnGesture?.Invoke(e);
        }

        /// Gestures recognised outside the engine (the selection tool) go through the same channel.
        public void RaiseExternal(string name, string hand, string phase, float time, Vector3 pos, string detail) => Raise(name, hand, phase, time, pos, detail);

        private static float Curl(Vector3 wrist, Vector3 knuckle, Vector3 tip)
        {
            float kn = Vector3.Distance(wrist, knuckle);
            if (kn < 1e-4f) return 0f;
            float ratio = Vector3.Distance(wrist, tip) / kn; // ~1.9 extended, ~1.0 curled into the palm
            return Mathf.Clamp01((1.75f - ratio) / 0.65f);
        }

        private void UpdateHand(HandState s, float time)
        {
            string hn = s.Hand == Handedness.Left ? "left" : "right";
            bool tracked = Source != null && Source.TryGet(s.Hand, out s.Frame) && s.Frame.Tracked && !s.Frame.SystemGesture;
            if (!tracked)
            {
                if (s.Tracked) EndAll(s, hn, time);
                s.Tracked = false;
                return;
            }
            s.Tracked = true;
            var f = s.Frame;
            s.Curl[0] = Curl(f.Wrist, f.IndexKnuckle, f.IndexTip);
            s.Curl[1] = Curl(f.Wrist, f.MiddleKnuckle, f.MiddleTip);
            s.Curl[2] = Curl(f.Wrist, f.RingKnuckle, f.RingTip);
            s.Curl[3] = Curl(f.Wrist, f.LittleKnuckle, f.LittleTip);
            float geomPinch = Mathf.Clamp01((0.045f - Vector3.Distance(f.ThumbTip, f.IndexTip)) / 0.03f);
            s.Pinch = f.PinchStrength >= 0f ? f.PinchStrength : geomPinch;

            bool pinch = s.Pinching ? s.Pinch > Settings.pinchOff : s.Pinch >= Settings.pinchOn;
            int curled = 0, extended = 0;
            for (int i = 0; i < 4; i++) { if (s.Curl[i] > 0.62f) curled++; if (s.Curl[i] < 0.3f) extended++; }
            bool grab = !pinch && (s.Grabbing ? curled >= 3 : curled == 4);
            bool point = !pinch && s.Curl[0] < 0.3f && s.Curl[1] > 0.55f && s.Curl[2] > 0.55f && s.Curl[3] > 0.5f;
            bool open = !pinch && extended == 4;
            var n = f.PalmNormal(s.Hand);
            bool palmUp = open && (s.PalmUp ? n.y > 0.5f : n.y > 0.75f);

            Edge(ref s.Pinching, pinch, "pinch", hn, time, (f.ThumbTip + f.IndexTip) * 0.5f);
            Edge(ref s.Grabbing, grab, "grab", hn, time, f.Palm);
            Edge(ref s.Pointing, point, "point", hn, time, f.IndexTip);
            Edge(ref s.Open, open, "open", hn, time, f.Palm);
            Edge(ref s.PalmUp, palmUp, "palm-up", hn, time, f.Palm);
        }

        private void Edge(ref bool state, bool now, string name, string hand, float time, Vector3 pos)
        {
            if (state == now) return;
            state = now;
            Raise(name, hand, now ? "start" : "end", time, pos);
        }

        private void EndAll(HandState s, string hn, float time)
        {
            var p = s.Frame.Palm;
            Edge(ref s.Pinching, false, "pinch", hn, time, p);
            Edge(ref s.Grabbing, false, "grab", hn, time, p);
            Edge(ref s.Pointing, false, "point", hn, time, p);
            Edge(ref s.Open, false, "open", hn, time, p);
            Edge(ref s.PalmUp, false, "palm-up", hn, time, p);
        }

        /// Clap: both palms come together fast and touch. Re-armed once they move apart again.
        private void UpdateClap(float time, float dt)
        {
            if (!Settings.clapEnabled || !Left.Tracked || !Right.Tracked) { _prevPalmDist = -1f; return; }
            float d = Vector3.Distance(Left.Frame.Palm, Right.Frame.Palm);
            if (_prevPalmDist >= 0f && dt > 1e-5f)
            {
                float speed = (_prevPalmDist - d) / dt; // closing speed, m/s
                _approachSpeed = Mathf.Max(speed, _approachSpeed * Mathf.Exp(-dt / 0.15f));
            }
            _prevPalmDist = d;
            if (_clapArmed && d < Settings.clapDistance && _approachSpeed > 0.35f)
            {
                _clapArmed = false;
                LastClapTime = time;
                Raise("clap", "both", "tap", time, (Left.Frame.Palm + Right.Frame.Palm) * 0.5f);
            }
            else if (!_clapArmed && d > Settings.clapDistance * 1.9f) _clapArmed = true;
        }
    }

    /// Hands from Meta's OVRHand + OVRSkeleton (OpenXR or legacy skeleton).
    public sealed class OvrHandSource : IHandSource
    {
        private readonly OVRHand[] _hands = new OVRHand[2];
        private readonly OVRSkeleton[] _skeletons = new OVRSkeleton[2];
        private readonly Dictionary<int, Transform>[] _bones = { new Dictionary<int, Transform>(), new Dictionary<int, Transform>() };

        public OvrHandSource(OVRHand left, OVRHand right)
        {
            _hands[0] = left; _hands[1] = right;
            _skeletons[0] = left != null ? left.GetComponent<OVRSkeleton>() : null;
            _skeletons[1] = right != null ? right.GetComponent<OVRSkeleton>() : null;
        }

        // our joint slots
        private const int Wrist = 0, Palm = 1, ThumbTip = 2, IndexTip = 3, IndexK = 4, MiddleTip = 5, MiddleK = 6, RingTip = 7, RingK = 8, LittleTip = 9, LittleK = 10;

        private static int SlotXr(OVRSkeleton.BoneId id)
        {
            if (id == OVRSkeleton.BoneId.XRHand_Wrist) return Wrist;
            if (id == OVRSkeleton.BoneId.XRHand_Palm) return Palm;
            if (id == OVRSkeleton.BoneId.XRHand_ThumbTip) return ThumbTip;
            if (id == OVRSkeleton.BoneId.XRHand_IndexTip) return IndexTip;
            if (id == OVRSkeleton.BoneId.XRHand_IndexProximal) return IndexK;
            if (id == OVRSkeleton.BoneId.XRHand_MiddleTip) return MiddleTip;
            if (id == OVRSkeleton.BoneId.XRHand_MiddleProximal) return MiddleK;
            if (id == OVRSkeleton.BoneId.XRHand_RingTip) return RingTip;
            if (id == OVRSkeleton.BoneId.XRHand_RingProximal) return RingK;
            if (id == OVRSkeleton.BoneId.XRHand_LittleTip) return LittleTip;
            if (id == OVRSkeleton.BoneId.XRHand_LittleProximal) return LittleK;
            return -1;
        }

        private static int SlotLegacy(OVRSkeleton.BoneId id)
        {
            if (id == OVRSkeleton.BoneId.Hand_WristRoot) return Wrist;
            if (id == OVRSkeleton.BoneId.Hand_ThumbTip) return ThumbTip;
            if (id == OVRSkeleton.BoneId.Hand_IndexTip) return IndexTip;
            if (id == OVRSkeleton.BoneId.Hand_Index1) return IndexK;
            if (id == OVRSkeleton.BoneId.Hand_MiddleTip) return MiddleTip;
            if (id == OVRSkeleton.BoneId.Hand_Middle1) return MiddleK;
            if (id == OVRSkeleton.BoneId.Hand_RingTip) return RingTip;
            if (id == OVRSkeleton.BoneId.Hand_Ring1) return RingK;
            if (id == OVRSkeleton.BoneId.Hand_PinkyTip) return LittleTip;
            if (id == OVRSkeleton.BoneId.Hand_Pinky1) return LittleK;
            return -1;
        }

        public bool TryGet(Handedness hand, out HandFrame f)
        {
            f = default;
            int i = (int)hand;
            var h = _hands[i];
            var s = _skeletons[i];
            if (h == null || s == null || !h.IsTracked || !s.IsInitialized || !s.IsDataValid || s.Bones == null) return false;
            var map = _bones[i];
            if (map.Count == 0)
            {
                var type = s.GetSkeletonType();
                bool xr = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;
                foreach (var b in s.Bones)
                {
                    int slot = xr ? SlotXr(b.Id) : SlotLegacy(b.Id);
                    if (slot >= 0 && b.Transform != null) map[slot] = b.Transform;
                }
                if (map.Count < 10) { map.Clear(); return false; }
            }
            Vector3 P(int slot) => map.TryGetValue(slot, out var t) && t != null ? t.position : Vector3.zero;
            f.Tracked = true;
            f.HighConfidence = h.IsDataHighConfidence;
            f.SystemGesture = h.IsSystemGestureInProgress;
            f.Wrist = P(Wrist);
            f.ThumbTip = P(ThumbTip); f.IndexTip = P(IndexTip); f.IndexKnuckle = P(IndexK);
            f.MiddleTip = P(MiddleTip); f.MiddleKnuckle = P(MiddleK); f.RingTip = P(RingTip); f.RingKnuckle = P(RingK);
            f.LittleTip = P(LittleTip); f.LittleKnuckle = P(LittleK);
            f.Palm = map.ContainsKey(Palm) ? P(Palm) : Vector3.Lerp(f.Wrist, f.MiddleKnuckle, 0.55f);
            f.PinchStrength = h.GetFingerPinchStrength(OVRHand.HandFinger.Index);
            f.PointerValid = h.IsPointerPoseValid && h.PointerPose != null;
            if (f.PointerValid) { f.PointerOrigin = h.PointerPose.position; f.PointerDirection = h.PointerPose.forward; }
            return true;
        }
    }

    /// Scripted hands for the Editor and self-tests: set poses directly or ask for canned motions.
    public sealed class SimulatedHands : IHandSource
    {
        public readonly HandFrame[] Frames = new HandFrame[2];
        public bool TryGet(Handedness hand, out HandFrame frame) { frame = Frames[(int)hand]; return frame.Tracked; }

        /// An open hand with the palm centre at `palm`, fingers pointing along `fingers`, palm facing `palmFacing`.
        public void SetOpen(Handedness h, Vector3 palm, Vector3 fingers, Vector3 palmFacing, float curl = 0f, float pinch = 0f)
        {
            fingers = fingers.normalized;
            palmFacing = palmFacing.normalized;
            // across the palm, from the index side to the little side
            var across = Vector3.Cross(palmFacing, fingers).normalized * (h == Handedness.Right ? -1f : 1f);
            var wrist = palm - fingers * 0.045f;
            Vector3 Knuckle(float side) => wrist + fingers * 0.085f + across * side;
            Vector3 Tip(Vector3 knuckle, float len, float c) => Vector3.Lerp(knuckle + fingers * len, knuckle - fingers * 0.01f + palmFacing * 0.02f, c);
            var f = new HandFrame
            {
                Tracked = true, HighConfidence = true, Wrist = wrist, Palm = palm, PinchStrength = pinch,
                IndexKnuckle = Knuckle(-0.025f), MiddleKnuckle = Knuckle(-0.005f), RingKnuckle = Knuckle(0.013f), LittleKnuckle = Knuckle(0.03f),
            };
            f.IndexTip = Tip(f.IndexKnuckle, 0.075f, curl);
            f.MiddleTip = Tip(f.MiddleKnuckle, 0.08f, curl);
            f.RingTip = Tip(f.RingKnuckle, 0.075f, curl);
            f.LittleTip = Tip(f.LittleKnuckle, 0.06f, curl);
            f.ThumbTip = pinch > 0.5f ? f.IndexTip : wrist + fingers * 0.07f - across * 0.05f;
            // the knuckles must lie so that PalmNormal(h) == palmFacing
            if (Vector3.Dot(f.PalmNormal(h), palmFacing) < 0f)
            {
                (f.IndexKnuckle, f.LittleKnuckle) = (f.LittleKnuckle, f.IndexKnuckle);
                (f.IndexTip, f.LittleTip) = (f.LittleTip, f.IndexTip);
            }
            Frames[(int)h] = f;
        }

        public void Hide(Handedness h) => Frames[(int)h] = default;
    }
}
