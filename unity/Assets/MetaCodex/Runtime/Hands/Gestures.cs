using System;
using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.Hands
{
    public enum Handedness { Left = 0, Right = 1 }

    /// One hand in one frame, in world space (Unity: metres, left-handed, +Y up). Built from Meta's
    /// OVRHand/OVRSkeleton on the headset, or scripted by tests (SimulatedHands).
    public struct HandFrame
    {
        public bool Tracked;
        public bool HighConfidence;
        public bool SystemGesture;
        public Vector3 Wrist, Palm, ThumbKnuckle, ThumbTip, IndexTip, IndexKnuckle, MiddleTip, MiddleKnuckle, RingTip, RingKnuckle, LittleTip, LittleKnuckle;
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
        /// Custom gestures (pose templates and Lua detectors from plugins) on/off.
        public bool customEnabled = true;
        /// How often Lua detectors run (Hz); poses are checked every frame (cheap).
        public float detectorRate = 36f;
    }

    public struct GestureEvent
    {
        public string Name;     // pinch, grab, point, open, palm-up, clap, select, or a plugin's gesture
        public string Hand;     // left, right, both
        public string Phase;    // start, end, tap
        public float Time;
        public Vector3 Position;
        public string Detail;   // e.g. selection: "panel x y w h"; custom gestures: "pose" / "lua"
    }

    /// Per-hand recognised state, updated every frame.
    public sealed class HandState
    {
        public Handedness Hand;
        public HandFrame Frame;
        public bool Tracked, Pinching, Grabbing, Pointing, Open, PalmUp;
        public float Pinch;     // 0..1
        public float[] Curl = new float[4]; // index, middle, ring, little: 0 extended .. 1 curled
        /// All five fingers, thumb first: thumb, index, middle, ring, little (0 straight .. 1 curled).
        public float[] Fingers = new float[5];
        public float ThumbCurl => Fingers[0];
        public Vector3 ThumbDir, PalmNormal, Forward;
        /// Palm velocity in world space (m/s, smoothed over ~50 ms).
        public Vector3 Velocity;
        /// Wrist to middle knuckle (m): distances in poses are divided by it, so small and big hands match alike.
        public float HandSize = 0.085f;
        public string Side => Hand == Handedness.Left ? "left" : "right";
        internal Vector3 PrevPalm;
        internal bool HasPrev;
    }

    /// A named hand pose: target curls per finger plus optional palm / thumb directions. Loaded from plugin
    /// files (poses/*.json), defined from Lua (gesture.pose) or recorded in the Gestures panel.
    public sealed class PoseSpec
    {
        public static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "little" };
        public static readonly string[] Directions = { "up", "down", "head", "away", "left", "right", "forward", "back" };

        public string Name = "";
        public string Description = "";
        public string Hand = "any";            // left | right | any
        public float?[] Fingers = new float?[5];
        public float Tolerance = 0.3f;
        public string PalmFacing;              // one of Directions, or null = any
        public string ThumbPointing;           // one of Directions, or null = any
        public float DirectionMin = 0.6f;      // cos of the allowed angle to the named direction (0.6 ~ 53 degrees)
        public float? PinchMin, PinchMax;
        public float Hold = 0.12f;
        public float Cooldown = 0.3f;
        /// Who defined it: a plugin id, a script name, or "user".
        public string Owner = "";
        public string Source = "";

        public bool AppliesTo(Handedness h) => Hand == "any" || Hand == "both" || Hand == (h == Handedness.Left ? "left" : "right");

        public bool Matches(HandState s, GestureEngine eng)
        {
            if (!s.Tracked) return false;
            for (int i = 0; i < 5; i++)
                if (Fingers[i].HasValue && Mathf.Abs(s.Fingers[i] - Fingers[i].Value) > Tolerance) return false;
            if (PinchMin.HasValue && s.Pinch < PinchMin.Value) return false;
            if (PinchMax.HasValue && s.Pinch > PinchMax.Value) return false;
            if (!string.IsNullOrEmpty(PalmFacing) && PalmFacing != "any" && Vector3.Dot(s.PalmNormal, eng.Direction(PalmFacing, s.Frame.Palm)) < DirectionMin) return false;
            if (!string.IsNullOrEmpty(ThumbPointing) && ThumbPointing != "any" && Vector3.Dot(s.ThumbDir, eng.Direction(ThumbPointing, s.Frame.Palm)) < DirectionMin) return false;
            return true;
        }

        /// A readable one-line definition for panels and logs.
        public string Summary()
        {
            var parts = new List<string>();
            for (int i = 0; i < 5; i++) if (Fingers[i].HasValue) parts.Add($"{FingerNames[i]} {(Fingers[i].Value < 0.35f ? "straight" : Fingers[i].Value > 0.65f ? "curled" : "half")}");
            if (!string.IsNullOrEmpty(PalmFacing)) parts.Add("palm " + PalmFacing);
            if (!string.IsNullOrEmpty(ThumbPointing)) parts.Add("thumb " + ThumbPointing);
            if (PinchMin.HasValue) parts.Add($"pinch ≥ {PinchMin.Value:0.##}");
            if (PinchMax.HasValue) parts.Add($"pinch ≤ {PinchMax.Value:0.##}");
            return $"{Hand} hand: " + string.Join(", ", parts) + $" (±{Tolerance:0.##}, hold {Hold:0.##} s)";
        }
    }

    /// Meta's basics turned into named gestures with hysteresis: pinch (OVR pinch strength), grab (fist),
    /// point (index out, others curled), open palm, palm up, and the two-hand clap. Plugins add more: pose
    /// templates (checked here every frame) and Lua detectors (run by the script host through CustomTick and
    /// fed back with Feed). Every recognised gesture is one GestureEvent with a start/end (or tap) phase.
    public sealed class GestureEngine
    {
        public static readonly string[] Names = { "pinch", "grab", "point", "open", "palm-up", "clap", "select" };
        public readonly HandState Left = new HandState { Hand = Handedness.Left };
        public readonly HandState Right = new HandState { Hand = Handedness.Right };
        public GestureSettings Settings;
        public event Action<GestureEvent> OnGesture;
        /// After the built-in gestures and poses of a frame: the script host runs Lua detectors here.
        public event Action<float, float> CustomTick;
        public IHandSource Source;
        /// The user's head (for "palm facing me", left/right and head-relative motion); null = origin looking +Z.
        public Func<Pose> HeadPose;
        public float LastClapTime { get; private set; } = -10f;
        private float _prevPalmDist = -1f, _approachSpeed;
        private bool _clapArmed = true;
        private readonly Queue<GestureEvent> _recent = new Queue<GestureEvent>();

        /// What a custom gesture is (for the Gestures panel and gesture.list()).
        public sealed class CustomInfo { public string Name, Kind, Owner, Description, Hand; }
        private readonly Dictionary<string, PoseSpec> _poses = new Dictionary<string, PoseSpec>();
        private readonly Dictionary<string, CustomInfo> _custom = new Dictionary<string, CustomInfo>();
        private sealed class Live { public bool Raw, Active; public float RawSince, LastStart = -100f; public Vector3 Pos; }
        private readonly Dictionary<string, Live> _live = new Dictionary<string, Live>();

        private readonly HandState[] _both;

        public GestureEngine(IHandSource source, GestureSettings settings) { Source = source; Settings = settings; _both = new[] { Left, Right }; }

        public HandState Get(Handedness h) => h == Handedness.Left ? Left : Right;
        public IEnumerable<GestureEvent> Recent => _recent;
        public IEnumerable<PoseSpec> Poses => _poses.Values;
        public IEnumerable<CustomInfo> Custom => _custom.Values;

        public void Tick(float time, float dt)
        {
            UpdateHand(Left, time, dt);
            UpdateHand(Right, time, dt);
            UpdateClap(time, dt);
            if (Settings.customEnabled)
            {
                UpdatePoses(time);
                CustomTick?.Invoke(time, dt);
            }
        }

        private void Raise(string name, string hand, string phase, float time, Vector3 pos, string detail = null)
        {
            var e = new GestureEvent { Name = name, Hand = hand, Phase = phase, Time = time, Position = pos, Detail = detail };
            _recent.Enqueue(e);
            while (_recent.Count > 12) _recent.Dequeue();
            OnGesture?.Invoke(e);
        }

        /// Gestures recognised outside the engine (the selection tool, scripts' gesture.emit) go through the same channel.
        public void RaiseExternal(string name, string hand, string phase, float time, Vector3 pos, string detail) => Raise(name, hand, phase, time, pos, detail);

        // ------------------------------------------------------------------ head-relative directions

        public Pose Head => HeadPose != null ? HeadPose() : new Pose(new Vector3(0f, 1.4f, 0f), Quaternion.identity);

        /// Yaw-only frame of the head: x right, y world up, z forward (nodding does not change it).
        public void HeadFrame(out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            var f = Head.rotation * Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            forward = f.normalized;
            up = Vector3.up;
            right = Vector3.Cross(up, forward).normalized;
        }

        /// A world direction in the head's yaw frame (x right, y up, z forward).
        public Vector3 ToHeadLocal(Vector3 world)
        {
            HeadFrame(out var r, out var u, out var f);
            return new Vector3(Vector3.Dot(world, r), Vector3.Dot(world, u), Vector3.Dot(world, f));
        }

        /// A named direction as a world unit vector; "head" and "away" are relative to the point `from`.
        public Vector3 Direction(string name, Vector3 from)
        {
            HeadFrame(out var r, out var u, out var f);
            switch (name)
            {
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                case "left": return -r;
                case "right": return r;
                case "forward": return f;
                case "back": return -f;
                case "head": { var d = Head.position - from; return d.sqrMagnitude > 1e-6f ? d.normalized : -f; }
                case "away": { var d = from - Head.position; return d.sqrMagnitude > 1e-6f ? d.normalized : f; }
                default: return Vector3.zero;
            }
        }

        /// The named direction closest to a world vector (palmFacing / thumbPointing for Lua, recording,
        /// the live monitor): the nearest of up/down/left/right/forward/back, where forward and back become
        /// "away" and "head" when the vector points roughly away from / towards the eyes (a hand in front of
        /// the user is below and beside the eyes, so "away" is 30-45 degrees off "forward" there).
        public string Nearest(Vector3 v, Vector3 from)
        {
            string best = "up";
            float bestDot = -2f;
            foreach (var n in PoseSpec.Directions)
            {
                if (n == "head" || n == "away") continue;
                float d = Vector3.Dot(v, Direction(n, from));
                if (d > bestDot) { bestDot = d; best = n; }
            }
            if (best == "forward" || best == "back")
            {
                float toHead = Vector3.Dot(v, Direction("head", from));
                if (toHead > 0.5f) return "head";
                if (toHead < -0.5f) return "away";
            }
            return best;
        }

        // ------------------------------------------------------------------ hands

        private static float Curl(Vector3 wrist, Vector3 knuckle, Vector3 tip)
        {
            float kn = Vector3.Distance(wrist, knuckle);
            if (kn < 1e-4f) return 0f;
            float ratio = Vector3.Distance(wrist, tip) / kn; // ~1.9 extended, ~1.0 curled into the palm
            return Mathf.Clamp01((1.75f - ratio) / 0.65f);
        }

        /// Thumb: tip close to the middle knuckle (folded across the palm) = 1, far out to the side or up = 0.
        private static float ThumbCurlOf(in HandFrame f, float handSize)
        {
            float ratio = Vector3.Distance(f.ThumbTip, f.MiddleKnuckle) / Mathf.Max(handSize, 0.04f);
            return Mathf.Clamp01((0.75f - ratio) / 0.45f);
        }

        private void UpdateHand(HandState s, float time, float dt)
        {
            string hn = s.Side;
            bool tracked = Source != null && Source.TryGet(s.Hand, out s.Frame) && s.Frame.Tracked && !s.Frame.SystemGesture;
            if (!tracked)
            {
                if (s.Tracked) EndAll(s, hn, time);
                s.Tracked = false;
                s.HasPrev = false;
                s.Velocity = Vector3.zero;
                return;
            }
            s.Tracked = true;
            var f = s.Frame;
            s.Curl[0] = Curl(f.Wrist, f.IndexKnuckle, f.IndexTip);
            s.Curl[1] = Curl(f.Wrist, f.MiddleKnuckle, f.MiddleTip);
            s.Curl[2] = Curl(f.Wrist, f.RingKnuckle, f.RingTip);
            s.Curl[3] = Curl(f.Wrist, f.LittleKnuckle, f.LittleTip);
            s.HandSize = Mathf.Max(0.05f, Vector3.Distance(f.Wrist, f.MiddleKnuckle));
            if (f.ThumbKnuckle == Vector3.zero) f.ThumbKnuckle = Vector3.Lerp(f.Wrist, f.IndexKnuckle, 0.35f);
            s.Fingers[0] = ThumbCurlOf(f, s.HandSize);
            for (int i = 0; i < 4; i++) s.Fingers[i + 1] = s.Curl[i];
            var td = f.ThumbTip - f.ThumbKnuckle;
            s.ThumbDir = td.sqrMagnitude > 1e-8f ? td.normalized : Vector3.zero;
            s.PalmNormal = f.PalmNormal(s.Hand);
            var fw = f.MiddleKnuckle - f.Wrist;
            s.Forward = fw.sqrMagnitude > 1e-8f ? fw.normalized : Vector3.forward;
            if (s.HasPrev && dt > 1e-5f)
            {
                var v = (f.Palm - s.PrevPalm) / dt;
                s.Velocity = Vector3.Lerp(s.Velocity, v, 1f - Mathf.Exp(-dt / 0.05f));
            }
            else s.Velocity = Vector3.zero;
            s.PrevPalm = f.Palm;
            s.HasPrev = true;

            float geomPinch = Mathf.Clamp01((0.045f - Vector3.Distance(f.ThumbTip, f.IndexTip)) / 0.03f);
            s.Pinch = f.PinchStrength >= 0f ? f.PinchStrength : geomPinch;

            bool pinch = s.Pinching ? s.Pinch > Settings.pinchOff : s.Pinch >= Settings.pinchOn;
            int curled = 0, extended = 0;
            for (int i = 0; i < 4; i++) { if (s.Curl[i] > 0.62f) curled++; if (s.Curl[i] < 0.3f) extended++; }
            bool grab = !pinch && (s.Grabbing ? curled >= 3 : curled == 4);
            bool point = !pinch && s.Curl[0] < 0.3f && s.Curl[1] > 0.55f && s.Curl[2] > 0.55f && s.Curl[3] > 0.5f;
            bool open = !pinch && extended == 4;
            var n = s.PalmNormal;
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

        // ------------------------------------------------------------------ custom gestures (plugins)

        public void AddPose(PoseSpec p)
        {
            if (p == null || string.IsNullOrEmpty(p.Name)) return;
            RemoveCustom(p.Name);
            _poses[p.Name] = p;
            _custom[p.Name] = new CustomInfo { Name = p.Name, Kind = "pose", Owner = p.Owner, Description = string.IsNullOrEmpty(p.Description) ? p.Summary() : p.Description, Hand = p.Hand };
        }

        public PoseSpec GetPose(string name) => name != null && _poses.TryGetValue(name, out var p) ? p : null;

        /// A Lua detector (or any other source) announces a gesture name so panels and gesture.list() show it.
        public void RegisterCustom(string name, string kind, string owner, string description, string hand)
        {
            if (string.IsNullOrEmpty(name)) return;
            _custom[name] = new CustomInfo { Name = name, Kind = kind, Owner = owner, Description = description ?? "", Hand = hand ?? "any" };
        }

        /// Forgets a custom gesture (ending it if active).
        public void RemoveCustom(string name)
        {
            if (name == null) return;
            _poses.Remove(name);
            _custom.Remove(name);
            foreach (var hand in new[] { "left", "right", "both" }) EndLive(name, hand, Time.unscaledTime);
        }

        /// Forgets everything a plugin or script defined (on unload / reload / disable).
        public void RemoveOwnedBy(string owner)
        {
            var names = new List<string>();
            foreach (var c in _custom.Values) if (c.Owner == owner) names.Add(c.Name);
            foreach (var n in names) RemoveCustom(n);
        }

        public IEnumerable<string> AllNames()
        {
            foreach (var n in Names) yield return n;
            foreach (var c in _custom.Keys) yield return c;
        }

        public bool IsBuiltIn(string name) => Array.IndexOf(Names, name) >= 0;

        /// Feeds one frame of a custom gesture's raw state: it starts after being true for `hold` seconds
        /// (and not within `cooldown` of its last start) and ends as soon as it is false.
        public void Feed(string name, string hand, bool raw, float time, Vector3 pos, float hold, float cooldown)
        {
            string key = name + "|" + hand;
            if (!_live.TryGetValue(key, out var l)) { if (!raw) return; _live[key] = l = new Live(); }
            if (raw && !l.Raw) l.RawSince = time;
            l.Raw = raw;
            l.Pos = pos;
            if (raw && !l.Active && time - l.RawSince >= hold && time - l.LastStart >= cooldown)
            {
                l.Active = true;
                l.LastStart = time;
                Raise(name, hand, "start", time, pos, _custom.TryGetValue(name, out var c) ? c.Kind : "custom");
            }
            else if (!raw && l.Active)
            {
                l.Active = false;
                Raise(name, hand, "end", time, pos, _custom.TryGetValue(name, out var c2) ? c2.Kind : "custom");
            }
        }

        private void EndLive(string name, string hand, float time)
        {
            if (_live.TryGetValue(name + "|" + hand, out var l) && l.Active) { l.Active = false; l.Raw = false; Raise(name, hand, "end", time, l.Pos, "custom"); }
        }

        public bool IsActive(string name, string hand = null)
        {
            if (hand != null) return _live.TryGetValue(name + "|" + hand, out var l) && l.Active;
            foreach (var h in new[] { "left", "right", "both" }) if (_live.TryGetValue(name + "|" + h, out var l2) && l2.Active) return true;
            return BuiltInActive(name, Left) || BuiltInActive(name, Right);
        }

        private static bool BuiltInActive(string name, HandState s)
        {
            switch (name)
            {
                case "pinch": return s.Pinching;
                case "grab": return s.Grabbing;
                case "point": return s.Pointing;
                case "open": return s.Open;
                case "palm-up": return s.PalmUp;
                default: return false;
            }
        }

        /// Names active right now on a hand (built-in and custom), for monitors.
        public List<string> ActiveOn(HandState s)
        {
            var list = new List<string>();
            foreach (var n in Names) if (BuiltInActive(n, s)) list.Add(n);
            foreach (var kv in _live) if (kv.Value.Active && kv.Key.EndsWith("|" + s.Side)) list.Add(kv.Key.Substring(0, kv.Key.Length - s.Side.Length - 1));
            return list;
        }

        private void UpdatePoses(float time)
        {
            if (_poses.Count == 0) return;
            foreach (var p in _poses.Values)
            {
                foreach (var s in _both)
                {
                    if (!p.AppliesTo(s.Hand)) continue;
                    Feed(p.Name, s.Side, p.Matches(s, this), time, s.Frame.Palm, p.Hold, p.Cooldown);
                }
            }
        }

        /// A pose template from what a hand does right now (Gestures panel > RECORD).
        public PoseSpec Capture(Handedness h, string name, float tolerance = 0.3f)
        {
            var s = Get(h);
            if (!s.Tracked) return null;
            var p = new PoseSpec { Name = name, Hand = s.Side, Tolerance = tolerance, Owner = "user", Description = "recorded" };
            for (int i = 0; i < 5; i++) p.Fingers[i] = Mathf.Round(s.Fingers[i] * 100f) / 100f;
            p.PalmFacing = Nearest(s.PalmNormal, s.Frame.Palm);
            if (s.Fingers[0] < 0.4f) p.ThumbPointing = Nearest(s.ThumbDir, s.Frame.Palm);
            return p;
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
        private const int Wrist = 0, Palm = 1, ThumbTip = 2, IndexTip = 3, IndexK = 4, MiddleTip = 5, MiddleK = 6, RingTip = 7, RingK = 8, LittleTip = 9, LittleK = 10, ThumbK = 11;

        private static int SlotXr(OVRSkeleton.BoneId id)
        {
            if (id == OVRSkeleton.BoneId.XRHand_Wrist) return Wrist;
            if (id == OVRSkeleton.BoneId.XRHand_Palm) return Palm;
            if (id == OVRSkeleton.BoneId.XRHand_ThumbProximal) return ThumbK;
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
            if (id == OVRSkeleton.BoneId.Hand_Thumb2) return ThumbK;
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
            f.ThumbKnuckle = map.ContainsKey(ThumbK) ? P(ThumbK) : Vector3.Lerp(f.Wrist, f.IndexKnuckle, 0.35f);
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
            f.ThumbKnuckle = wrist + fingers * 0.03f - across * 0.025f;
            // the knuckles must lie so that PalmNormal(h) == palmFacing
            if (Vector3.Dot(f.PalmNormal(h), palmFacing) < 0f)
            {
                (f.IndexKnuckle, f.LittleKnuckle) = (f.LittleKnuckle, f.IndexKnuckle);
                (f.IndexTip, f.LittleTip) = (f.LittleTip, f.IndexTip);
            }
            Frames[(int)h] = f;
        }

        /// Any hand pose: a curl per finger (thumb, index, middle, ring, little; 0 straight .. 1 curled), the
        /// palm at `palm` facing `palmFacing`, the fingers along `fingers`; `thumbDir` points an extended thumb
        /// (default: out to the index side). Used by tests and by `mcx-ui hand pose ...` on the headset.
        public void SetPose(Handedness h, Vector3 palm, Vector3 fingers, Vector3 palmFacing, float thumb, float index, float middle, float ring, float little, Vector3? thumbDir = null, float pinch = 0f)
        {
            fingers = fingers.normalized;
            palmFacing = palmFacing.normalized;
            var across = Vector3.Cross(palmFacing, fingers).normalized * (h == Handedness.Right ? -1f : 1f);
            var wrist = palm - fingers * 0.045f;
            var f = new HandFrame { Tracked = true, HighConfidence = true, Wrist = wrist, Palm = palm, PinchStrength = pinch };
            Vector3 Knuckle(float side) => wrist + fingers * 0.085f + across * side;
            f.IndexKnuckle = Knuckle(-0.025f); f.MiddleKnuckle = Knuckle(-0.005f); f.RingKnuckle = Knuckle(0.013f); f.LittleKnuckle = Knuckle(0.03f);
            if (Vector3.Dot(f.PalmNormal(h), palmFacing) < 0f)
            {
                across = -across;
                f.IndexKnuckle = Knuckle(-0.025f); f.MiddleKnuckle = Knuckle(-0.005f); f.RingKnuckle = Knuckle(0.013f); f.LittleKnuckle = Knuckle(0.03f);
            }
            Vector3 Tip(Vector3 knuckle, float len, float c) => Vector3.Lerp(knuckle + fingers * len, knuckle - fingers * 0.01f + palmFacing * 0.02f, c);
            f.IndexTip = Tip(f.IndexKnuckle, 0.075f, index);
            f.MiddleTip = Tip(f.MiddleKnuckle, 0.08f, middle);
            f.RingTip = Tip(f.RingKnuckle, 0.075f, ring);
            f.LittleTip = Tip(f.LittleKnuckle, 0.06f, little);
            var indexSide = -across; // from the middle line towards the index finger (the thumb's side)
            var thumbBase = wrist + fingers * 0.03f + indexSide * 0.025f;
            var dir = (thumbDir ?? (indexSide * 0.85f + fingers * 0.5f)).normalized;
            var extended = thumbBase + dir * 0.065f;
            var folded = f.MiddleKnuckle + palmFacing * 0.025f - fingers * 0.01f;
            f.ThumbKnuckle = thumbBase;
            f.ThumbTip = pinch > 0.5f ? f.IndexTip : Vector3.Lerp(extended, folded, Mathf.Clamp01(thumb));
            Frames[(int)h] = f;
        }

        public void Hide(Handedness h) => Frames[(int)h] = default;
    }
}
