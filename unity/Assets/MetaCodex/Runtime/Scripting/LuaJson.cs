using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MoonSharp.Interpreter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MetaCodex.Scripting
{
    /// Lua values <-> JSON (json.encode / json.decode, and the view spec of ui.panel). No Unity types: a PC harness
    /// compiles this file alone.
    public static class LuaJson
    {
        public const int MaxDepth = 32;

        /// A Lua value as a JSON token. Tables with the keys exactly 1..n (and the empty table) become arrays, other
        /// tables objects (keys as strings, sorted, so equal values give equal text). Throws ScriptRuntimeException (message without a prefix) for
        /// functions, userdata, NaN, infinity, keys that are not strings or numbers, and depth over 32.
        public static JToken ToJson(DynValue v) => ToJson(v, 0, "value");

        /// The same, with the name the error messages use for the root (for example "components").
        public static JToken ToJson(DynValue v, string rootName) => ToJson(v, 0, rootName);

        /// json.encode: compact text.
        public static string Encode(DynValue v) => ToJson(v).ToString(Formatting.None);

        private static JToken ToJson(DynValue v, int depth, string path)
        {
            if (v == null) return JValue.CreateNull();
            switch (v.Type)
            {
                case DataType.Nil:
                case DataType.Void:
                    return JValue.CreateNull();
                case DataType.Boolean:
                    return new JValue(v.Boolean);
                case DataType.Number:
                    return Num(v.Number, path);
                case DataType.String:
                    return new JValue(v.String);
                case DataType.Table:
                    return TableToJson(v.Table, depth, path);
                default:
                    throw new ScriptRuntimeException("cannot encode a " + v.Type.ToString().ToLowerInvariant() + " (at " + path + "); use strings, numbers, booleans and tables");
            }
        }

        private static JToken Num(double d, string path)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new ScriptRuntimeException("cannot encode " + d.ToString(CultureInfo.InvariantCulture) + " (at " + path + ")");
            if (d == Math.Floor(d) && Math.Abs(d) < 9e15) return new JValue((long)d);
            return new JValue(d);
        }

        private static JToken TableToJson(Table t, int depth, string path)
        {
            if (depth >= MaxDepth) throw new ScriptRuntimeException("nested deeper than " + MaxDepth + " levels (at " + path + "); a table that contains itself?");
            int count = 0;
            bool array = true;
            foreach (var pair in t.Pairs)
            {
                count++;
                if (pair.Key.Type != DataType.Number) array = false;
            }
            if (count == 0) return new JArray();
            if (array)
            {
                // the keys are numbers: they must be exactly 1..count
                var items = new JToken[count];
                foreach (var pair in t.Pairs)
                {
                    double k = pair.Key.Number;
                    if (k != Math.Floor(k) || k < 1 || k > count) { array = false; break; }
                    items[(int)k - 1] = ToJson(pair.Value, depth + 1, path + "[" + (int)k + "]");
                }
                if (array)
                {
                    var arr = new JArray();
                    for (int i = 0; i < count; i++) arr.Add(items[i] ?? JValue.CreateNull());
                    return arr;
                }
            }
            var keys = new List<string>(count);
            var vals = new Dictionary<string, DynValue>(count);
            foreach (var pair in t.Pairs)
            {
                string key;
                if (pair.Key.Type == DataType.String) key = pair.Key.String;
                else if (pair.Key.Type == DataType.Number) key = pair.Key.Number.ToString(CultureInfo.InvariantCulture);
                else throw new ScriptRuntimeException("table keys must be strings or numbers (at " + path + ")");
                if (!vals.ContainsKey(key)) keys.Add(key);
                vals[key] = pair.Value;
            }
            keys.Sort(StringComparer.Ordinal);
            var obj = new JObject();
            foreach (var key in keys) obj[key] = ToJson(vals[key], depth + 1, path + "." + key);
            return obj;
        }

        /// A JSON token as a Lua value (JSON null = nil; integers and floats = numbers).
        public static DynValue FromJson(Script script, JToken token) => FromJson(script, token, 0);

        private static DynValue FromJson(Script script, JToken token, int depth)
        {
            if (token == null) return DynValue.Nil;
            if (depth > MaxDepth) throw new ScriptRuntimeException("nested deeper than " + MaxDepth + " levels");
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return DynValue.Nil;
                case JTokenType.Boolean:
                    return DynValue.NewBoolean((bool)token);
                case JTokenType.Integer:
                case JTokenType.Float:
                    return DynValue.NewNumber(Convert.ToDouble(((JValue)token).Value, CultureInfo.InvariantCulture));
                case JTokenType.Array:
                {
                    var t = new Table(script);
                    int i = 1;
                    foreach (var item in (JArray)token) t.Set(i++, FromJson(script, item, depth + 1));
                    return DynValue.NewTable(t);
                }
                case JTokenType.Object:
                {
                    var t = new Table(script);
                    foreach (var p in ((JObject)token).Properties()) t.Set(p.Name, FromJson(script, p.Value, depth + 1));
                    return DynValue.NewTable(t);
                }
                default:
                    return DynValue.NewString(token.ToString());
            }
        }

        /// json.decode: the value, or (nil, message) for bad text. Never throws.
        public static DynValue Decode(Script script, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return DynValue.NewTuple(DynValue.Nil, DynValue.NewString("json.decode: empty text"));
            try
            {
                JToken token;
                using (var sr = new StringReader(text))
                using (var r = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None, MaxDepth = MaxDepth + 1 })
                {
                    token = JToken.ReadFrom(r);
                    if (r.Read()) return DynValue.NewTuple(DynValue.Nil, DynValue.NewString("json.decode: extra text after the value"));
                }
                return FromJson(script, token, 0);
            }
            catch (Exception ex)
            {
                return DynValue.NewTuple(DynValue.Nil, DynValue.NewString("json.decode: " + ex.Message));
            }
        }
    }
}
