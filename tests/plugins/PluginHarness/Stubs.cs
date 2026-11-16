// The minimal Unity and hand-tracking types the real ScriptHost.cs needs, with trivial bodies. Only what ScriptHost.cs,
// LuaJson.cs, ViewSpecCheck.cs and VoiceCommands.cs use. Nothing here is the app's code.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace UnityEngine
{
    public static class Mathf
    {
        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Abs(float v) => v < 0f ? -v : v;
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity => new Quaternion { w = 1f };
        public static Vector3 operator *(Quaternion q, Vector3 v) => v;
    }

    public struct Pose
    {
        public Vector3 position;
        public Quaternion rotation;
        public Pose(Vector3 position, Quaternion rotation) { this.position = position; this.rotation = rotation; }
    }

    public static class Application
    {
        /// False: the host scans its folders at once on every Tick (like the Editor's edit-mode tests).
        public static bool isPlaying => false;
    }

    /// Unity's JsonUtility for the host's state file: public fields, no renaming, same layout as the app writes.
    public static class JsonUtility
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { IgnoreSerializableAttribute = true },
        };
        public static T FromJson<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
        public static string ToJson(object obj) => ToJson(obj, false);
        public static string ToJson(object obj, bool prettyPrint) =>
            JsonConvert.SerializeObject(obj, prettyPrint ? Formatting.Indented : Formatting.None, Settings);
    }
}

namespace MetaCodex.Hands
{
    using UnityEngine;

    public struct HandFrame
    {
        public bool Tracked, HighConfidence;
        public Vector3 Palm;
    }

    public struct GestureEvent
    {
        public string Name, Hand, Phase;
        public float Time;
        public Vector3 Position;
        public string Detail;
    }

    public sealed class GestureSettings
    {
        public float detectorRate = 36f;
    }

    public sealed class HandState
    {
        public string Side = "right";
        public HandFrame Frame;
        public bool Tracked, Pinching, Grabbing, Pointing, Open, PalmUp;
        public float Pinch;
        public float[] Fingers = new float[5];
        public Vector3 ThumbDir, PalmNormal, Forward, Velocity;
        public float HandSize = 0.085f;
    }

    public sealed class PoseSpec
    {
        public static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "little" };
        public static readonly string[] Directions = { "up", "down", "head", "away", "left", "right", "forward", "back" };
        public string Name = "";
        public string Description = "";
        public string Hand = "any";
        public float?[] Fingers = new float?[5];
        public float Tolerance = 0.3f;
        public string PalmFacing;
        public string ThumbPointing;
        public float DirectionMin = 0.6f;
        public float? PinchMin, PinchMax;
        public float Hold = 0.12f;
        public float Cooldown = 0.3f;
        public string Owner = "";
        public string Source = "";
    }

    /// A small stand-in for the app's gesture engine, enough to test gesture plugins without hands: the harness sets the
    /// hands (HandState fields) and calls Tick then RunCustomTick. Built-ins come from the hand's flags (grab, pinch,
    /// point, open, palm-up), poses from finger curls within their tolerance (and palm / thumb directions), Lua detectors
    /// from Feed (no hold, no cooldown). Every change becomes a start or end event in Events (the harness dispatches them).
    /// Not the app's code: the real engine's filters and timings are not modelled.
    public sealed class GestureEngine
    {
        public static readonly string[] Names = { "pinch", "grab", "point", "open", "palm-up", "clap", "select" };
        public readonly HandState Left = new HandState { Side = "left" };
        public readonly HandState Right = new HandState { Side = "right" };
        public GestureSettings Settings = new GestureSettings();
        public event Action<float, float> CustomTick;
        public Pose Head => new Pose(Vector3.zero, Quaternion.identity);
        public Vector3 ToHeadLocal(Vector3 world) => world;

        /// The direction's name by its largest axis (x right, y up, z forward).
        public string Nearest(Vector3 v, Vector3 from)
        {
            float ax = Math.Abs(v.x), ay = Math.Abs(v.y), az = Math.Abs(v.z);
            if (ax + ay + az < 1e-4f) return "";
            if (ay >= ax && ay >= az) return v.y > 0 ? "up" : "down";
            if (ax >= az) return v.x > 0 ? "right" : "left";
            return v.z > 0 ? "away" : "head";
        }

        /// Gestures the plugins added: (name, "pose" or "lua", owner).
        public readonly List<(string name, string kind, string owner)> Custom = new List<(string, string, string)>();
        public readonly List<PoseSpec> Poses = new List<PoseSpec>();
        /// Events since the harness last took them.
        public readonly List<GestureEvent> Events = new List<GestureEvent>();
        public readonly List<string> Emitted = new List<string>();
        private readonly HashSet<string> _active = new HashSet<string>();   // "name/hand"
        private readonly HashSet<string> _fed = new HashSet<string>();      // detectors true at the last feed

        public void RegisterCustom(string name, string kind, string owner, string description, string hand)
        {
            Custom.RemoveAll(c => c.name == name && c.owner == owner);
            Custom.Add((name, kind, owner));
        }

        public void AddPose(PoseSpec p)
        {
            Poses.RemoveAll(x => x.Name == p.Name);
            Poses.Add(p);
            Custom.RemoveAll(c => c.name == p.Name);
            Custom.Add((p.Name, "pose", p.Owner));
        }

        public void RemoveOwnedBy(string owner)
        {
            Custom.RemoveAll(c => c.owner == owner);
            Poses.RemoveAll(p => p.Owner == owner);
        }

        public IEnumerable<string> AllNames()
        {
            var all = new List<string>(Names);
            foreach (var c in Custom) if (!all.Contains(c.name)) all.Add(c.name);
            return all;
        }

        public bool IsActive(string name, string hand = null)
        {
            if (hand != null) return _active.Contains(name + "/" + hand);
            foreach (var a in _active) if (a.StartsWith(name + "/", StringComparison.Ordinal)) return true;
            return false;
        }

        public void RaiseExternal(string name, string hand, string phase, float time, Vector3 pos, string detail)
        {
            Emitted.Add(name + " " + hand + " " + phase);
            Events.Add(new GestureEvent { Name = name, Hand = hand, Phase = phase, Time = time, Position = pos, Detail = detail });
        }

        /// A Lua detector's value this pass (ScriptHost's OnCustomTick): true after false = start, false after true = end.
        public void Feed(string name, string hand, bool raw, float time, Vector3 pos, float hold, float cooldown)
        {
            string key = name + "/" + hand;
            if (raw && _fed.Add(key)) { _active.Add(key); Events.Add(new GestureEvent { Name = name, Hand = hand, Phase = "start", Time = time, Position = pos, Detail = "detector" }); }
            else if (!raw && _fed.Remove(key)) { _active.Remove(key); Events.Add(new GestureEvent { Name = name, Hand = hand, Phase = "end", Time = time, Position = pos, Detail = "detector" }); }
        }

        /// The built-ins and the poses from the hands now: all states first, then the start / end events.
        public void Tick(float time)
        {
            var now = new HashSet<string>();
            foreach (var h in new[] { Left, Right })
            {
                if (!h.Tracked) continue;
                if (h.Grabbing) now.Add("grab/" + h.Side);
                if (h.Pinching) now.Add("pinch/" + h.Side);
                if (h.Pointing) now.Add("point/" + h.Side);
                if (h.Open) now.Add("open/" + h.Side);
                if (h.PalmUp) now.Add("palm-up/" + h.Side);
                foreach (var p in Poses) if ((p.Hand == "any" || p.Hand == h.Side) && Matches(p, h)) now.Add(p.Name + "/" + h.Side);
            }
            var ended = new List<string>();
            foreach (var a in _active) if (!_fed.Contains(a) && !now.Contains(a)) ended.Add(a);
            var started = new List<string>();
            foreach (var a in now) if (!_active.Contains(a)) started.Add(a);
            foreach (var a in ended) _active.Remove(a);
            foreach (var a in started) _active.Add(a);
            foreach (var a in ended) Events.Add(Ev(a, "end", time));
            foreach (var a in started) Events.Add(Ev(a, "start", time));
        }

        /// Ends every active gesture (the hands go away).
        public void Clear(float time)
        {
            foreach (var a in _active) Events.Add(Ev(a, "end", time));
            _active.Clear();
            _fed.Clear();
        }

        private static GestureEvent Ev(string key, string phase, float time)
        {
            int i = key.LastIndexOf('/');
            return new GestureEvent { Name = key.Substring(0, i), Hand = key.Substring(i + 1), Phase = phase, Time = time, Detail = "harness" };
        }

        private bool Matches(PoseSpec p, HandState h)
        {
            for (int i = 0; i < 5; i++)
                if (p.Fingers[i].HasValue && Math.Abs(p.Fingers[i].Value - h.Fingers[i]) > p.Tolerance) return false;
            if (!string.IsNullOrEmpty(p.PalmFacing) && Nearest(h.PalmNormal, h.Frame.Palm) != p.PalmFacing) return false;
            if (!string.IsNullOrEmpty(p.ThumbPointing) && Nearest(h.ThumbDir, h.Frame.Palm) != p.ThumbPointing) return false;
            if (p.PinchMin.HasValue && h.Pinch < p.PinchMin.Value) return false;
            if (p.PinchMax.HasValue && h.Pinch > p.PinchMax.Value) return false;
            return true;
        }

        public void RunCustomTick(float time, float dt) => CustomTick?.Invoke(time, dt);
    }
}

namespace MetaCodex.AI
{
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    /// VoiceCommands.cs calls VoiceDecider.Fold (its number parsing). VoiceDecider.cs is not compiled here; this is the
    /// same function, copied.
    public static class VoiceDecider
    {
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char ch in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (ch == '\'' || ch == '’') { sb.Append(' '); continue; }
                sb.Append(char.IsLetterOrDigit(ch) || ch == '%' || ch == '.' ? ch : ' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }
    }
}
