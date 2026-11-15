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

    /// Never created by the harness (the fake services return null), so the host runs without an engine.
    public sealed class GestureEngine
    {
        public static readonly string[] Names = { "pinch", "grab", "point", "open", "palm-up", "clap", "select" };
        public readonly HandState Left = new HandState { Side = "left" };
        public readonly HandState Right = new HandState { Side = "right" };
        public GestureSettings Settings = new GestureSettings();
        public event Action<float, float> CustomTick;
        public Pose Head => new Pose(Vector3.zero, Quaternion.identity);
        public Vector3 ToHeadLocal(Vector3 world) => world;
        public string Nearest(Vector3 v, Vector3 from) => "";
        public void Feed(string name, string hand, bool raw, float time, Vector3 pos, float hold, float cooldown) { }
        public void RegisterCustom(string name, string kind, string owner, string description, string hand) { }
        public void AddPose(PoseSpec p) { }
        public void RaiseExternal(string name, string hand, string phase, float time, Vector3 pos, string detail) { }
        public IEnumerable<string> AllNames() => Names;
        public bool IsActive(string name, string hand = null) => false;
        public void RemoveOwnedBy(string owner) { }
        internal void RunCustomTick(float time, float dt) => CustomTick?.Invoke(time, dt);
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
