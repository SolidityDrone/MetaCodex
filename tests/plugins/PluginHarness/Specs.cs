// --specs: the component library's rules in ViewSpecCheck (the same code the app runs in ui.panel): a window with every
// component passes with no error and no warning; each broken spec is refused with an error that names the component and
// says what to write instead; a few doubtful ones only warn.
using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.Scripting;
using Newtonsoft.Json.Linq;

namespace PluginHarness
{
    public static class Specs
    {
        private static int _fails;

        private static void Ok(string m) => Console.WriteLine("ok   [specs] " + m);
        private static void Fail(string m) { _fails++; Program.Fails++; Console.WriteLine("FAIL [specs] " + m); }

        private static (List<string> errors, List<string> warnings) CheckOne(JObject spec)
        {
            var e = new List<string>(); var w = new List<string>();
            ViewSpecCheck.Check(spec, e, w);
            return (e, w);
        }

        private static JObject Win(params JObject[] comps) => new JObject { ["title"] = "Test", ["components"] = new JArray(comps) };
        private static JObject J(string json) => JObject.Parse(json);

        /// One window with every library component (and the basic ones inside containers).
        public const string AllJson = @"{""title"": ""All"", ""components"": [
  {""type"": ""tabs"", ""id"": ""pages"", ""value"": ""form"", ""tabs"": [
    {""id"": ""form"", ""label"": ""Form"", ""components"": [
      {""type"": ""card"", ""title"": ""Choose"", ""components"": [
        {""type"": ""checkbox"", ""id"": ""cb"", ""label"": ""Remind me"", ""value"": true},
        {""type"": ""toggle"", ""id"": ""tg"", ""label"": ""Sound"", ""value"": false},
        {""type"": ""radio"", ""id"": ""rd"", ""options"": [""Low"", ""High""], ""value"": ""Low""},
        {""type"": ""segmented"", ""id"": ""sg"", ""label"": ""View"", ""options"": [""Day"", ""Week"", ""Month""], ""value"": ""Week""}
      ]},
      {""type"": ""row"", ""align"": ""center"", ""components"": [
        {""type"": ""stepper"", ""id"": ""st"", ""label"": ""Minutes"", ""min"": 1, ""max"": 90, ""step"": 1, ""value"": 25, ""weight"": 2},
        {""type"": ""spacer""},
        {""type"": ""buttons"", ""items"": [{""id"": ""go"", ""label"": ""START"", ""tone"": ""accent""}]}
      ]},
      {""type"": ""divider""},
      {""type"": ""slider"", ""id"": ""sl"", ""label"": ""Volume"", ""min"": 0, ""max"": 100, ""step"": 5, ""value"": 40, ""unit"": ""%""},
      {""type"": ""select"", ""id"": ""se"", ""label"": ""City"", ""options"": [""Oslo"", {""label"": ""Rome"", ""value"": ""rome""}], ""value"": ""rome""},
      {""type"": ""field"", ""id"": ""fd"", ""label"": ""Name"", ""value"": """", ""placeholder"": ""your name""}
    ]},
    {""id"": ""pick"", ""label"": ""Pick"", ""components"": [
      {""type"": ""date"", ""id"": ""dt"", ""value"": ""2026-11-20T09:30"", ""time"": true, ""min"": ""2026-11-01""},
      {""type"": ""row"", ""components"": [
        {""type"": ""time"", ""id"": ""tm"", ""label"": ""At"", ""value"": ""07:45"", ""step"": 15},
        {""type"": ""color"", ""id"": ""co"", ""value"": ""#3987E5""}
      ]}
    ]},
    {""id"": ""more"", ""label"": ""More"", ""components"": [
      {""type"": ""list"", ""id"": ""ls"", ""selected"": ""b"", ""items"": [{""id"": ""a"", ""text"": ""Milk""}, {""id"": ""b"", ""text"": ""Bread"", ""sub"": ""2"", ""tone"": ""ok""}]},
      {""type"": ""progress"", ""items"": [{""label"": ""today"", ""value"": 0.4}]},
      {""type"": ""text"", ""id"": ""note"", ""text"": ""A live text in a tab""}
    ]}
  ]},
  {""type"": ""canvas"", ""id"": ""cv"", ""width"": 100, ""height"": 50, ""selected"": ""r1"", ""background"": ""dim"", ""shapes"": [
    {""id"": ""r1"", ""type"": ""rect"", ""x"": 2, ""y"": 2, ""w"": 40, ""h"": 30, ""label"": ""Kitchen"", ""tone"": ""accent""},
    {""id"": ""c1"", ""type"": ""circle"", ""x"": 60, ""y"": 20, ""r"": 8, ""fill"": ""warn""},
    {""id"": ""l1"", ""type"": ""line"", ""x1"": 0, ""y1"": 45, ""x2"": 100, ""y2"": 45, ""stroke_width"": 2},
    {""id"": ""p1"", ""type"": ""polyline"", ""points"": [[50, 40], [60, 35], [70, 42]]},
    {""id"": ""g1"", ""type"": ""polygon"", ""points"": [[75, 5], [98, 5], [98, 30], [86, 20], [75, 30]], ""label"": ""L"", ""opacity"": 0.4},
    {""type"": ""text"", ""x"": 50, ""y"": 48, ""text"": ""1 unit = 10 cm"", ""size"": 8},
    {""id"": ""a1"", ""type"": ""arc"", ""x"": 20, ""y"": 40, ""r"": 6, ""from"": -120, ""to"": 120}
  ]},
  {""type"": ""canvas"", ""id"": ""map"", ""fit"": ""lonlat"", ""bounds"": [12.3, 41.8, 12.6, 42.0], ""shapes"": [
    {""id"": ""pt"", ""type"": ""circle"", ""x"": 12.48, ""y"": 41.9, ""r"": 0.01}
  ]}
]}";

        public static void Run()
        {
            Console.WriteLine("== specs (ViewSpecCheck: the component library)");
            var (e0, w0) = CheckOne(JObject.Parse(AllJson));
            if (e0.Count == 0 && w0.Count == 0) Ok("a window with every library component (tabs, card, row, every control, two canvases): no error, no warning");
            else Fail("the window with every component: " + string.Join("; ", e0.Concat(w0)));

            var bad = new List<(string what, JObject spec, string expect)>
            {
                ("slider without min/max", Win(J(@"{""type"":""slider"",""id"":""s"",""value"":3}")), "min and max must be numbers"),
                ("slider max below min", Win(J(@"{""type"":""slider"",""id"":""s"",""min"":5,""max"":1}")), "max must be greater than min"),
                ("slider value outside", Win(J(@"{""type"":""slider"",""id"":""s"",""min"":0,""max"":10,""value"":20}")), "value must be a number from min to max"),
                ("checkbox without id", Win(J(@"{""type"":""checkbox"",""label"":""Done""}")), "needs an id"),
                ("checkbox value as text", Win(J(@"{""type"":""checkbox"",""id"":""c"",""label"":""Done"",""value"":""yes""}")), "value must be true or false"),
                ("radio with one option", Win(J(@"{""type"":""radio"",""id"":""r"",""options"":[""a""]}")), "options must be a list of 2 or more"),
                ("segmented with six options", Win(J(@"{""type"":""segmented"",""id"":""g"",""options"":[""a"",""b"",""c"",""d"",""e"",""f""]}")), "the most is 5 (more: use a select)"),
                ("select with a repeated option", Win(J(@"{""type"":""select"",""id"":""s"",""options"":[""a"",""a""]}")), "is there twice"),
                ("date in another format", Win(J(@"{""type"":""date"",""id"":""d"",""value"":""20/11/2026""}")), "value must be \"YYYY-MM-DD\""),
                ("date with a bad time", Win(J(@"{""type"":""date"",""id"":""d"",""time"":true,""value"":""2026-11-20T25:00""}")), "YYYY-MM-DDTHH:MM"),
                ("time without a leading zero", Win(J(@"{""type"":""time"",""id"":""t"",""value"":""9:30""}")), "value must be \"HH:MM\""),
                ("color as a word", Win(J(@"{""type"":""color"",""id"":""c"",""value"":""blue""}")), "value must be a colour like"),
                ("stepper step 0", Win(J(@"{""type"":""stepper"",""id"":""s"",""step"":0}")), "step must be above 0"),
                ("tabs with one tab", Win(J(@"{""type"":""tabs"",""id"":""t"",""tabs"":[{""id"":""a"",""label"":""A"",""components"":[{""type"":""text"",""text"":""x""}]}]}")), "tabs must be a list of 2 or more"),
                ("tabs inside tabs", Win(J(@"{""type"":""tabs"",""id"":""t"",""tabs"":[{""id"":""a"",""label"":""A"",""components"":[{""type"":""tabs"",""id"":""u"",""tabs"":[]}]},{""id"":""b"",""label"":""B"",""components"":[{""type"":""text"",""text"":""x""}]}]}")), "tabs cannot be inside tabs"),
                ("card without components", Win(J(@"{""type"":""card"",""title"":""X""}")), "components must be a list of the components inside it"),
                ("row with seven children", Win(J(@"{""type"":""row"",""components"":[{""type"":""spacer""},{""type"":""spacer""},{""type"":""spacer""},{""type"":""spacer""},{""type"":""spacer""},{""type"":""spacer""},{""type"":""spacer""}]}")), "the most is 6"),
                ("a 3D scene inside a card", Win(J(@"{""type"":""card"",""components"":[{""type"":""scene3d"",""bodies"":[]}]}")), "cannot be inside a card, row or tab"),
                ("an unknown type in a card", Win(J(@"{""type"":""card"",""components"":[{""type"":""datepicker""}]}")), "unknown type 'datepicker'"),
                ("an id used in a card and outside", Win(J(@"{""type"":""checkbox"",""id"":""x"",""label"":""A""}"), J(@"{""type"":""card"",""components"":[{""type"":""checkbox"",""id"":""x"",""label"":""B""}]}")), "the id 'x' is used twice in this window"),
                ("canvas shape of an unknown type", Win(J(@"{""type"":""canvas"",""id"":""c"",""shapes"":[{""type"":""star"",""x"":1,""y"":1}]}")), "type must be rect, circle, line, polyline, polygon, text or arc"),
                ("canvas polygon of two points", Win(J(@"{""type"":""canvas"",""id"":""c"",""shapes"":[{""type"":""polygon"",""points"":[[0,0],[1,1]]}]}")), "points must be a list of 3 or more"),
                ("canvas rect without a size", Win(J(@"{""type"":""canvas"",""id"":""c"",""shapes"":[{""type"":""rect"",""x"":0,""y"":0}]}")), "w and h"),
                ("canvas shape id twice", Win(J(@"{""type"":""canvas"",""id"":""c"",""shapes"":[{""id"":""a"",""type"":""circle"",""x"":1,""y"":1,""r"":1},{""id"":""a"",""type"":""circle"",""x"":2,""y"":2,""r"":1}]}")), "used twice in this canvas"),
                ("canvas lonlat without bounds", Win(J(@"{""type"":""canvas"",""id"":""c"",""fit"":""lonlat"",""shapes"":[]}")), "needs bounds={west, south, east, north}"),
                ("canvas without shapes", Win(J(@"{""type"":""canvas"",""id"":""c"",""width"":10,""height"":10}")), "shapes must be a list"),
                ("a list row without id", Win(J(@"{""type"":""list"",""id"":""l"",""items"":[{""id"":""a"",""text"":""A""},{""text"":""B""}]}")), "no id (a list with ids needs one on every row)"),
            };
            // a polygon of 65 points, a canvas of 121 shapes, 49 components in all
            var many = new JArray(); for (int i = 0; i < 65; i++) many.Add(new JArray(i, i % 7));
            bad.Add(("canvas polygon of 65 points", Win(new JObject { ["type"] = "canvas", ["id"] = "c", ["shapes"] = new JArray(new JObject { ["type"] = "polygon", ["points"] = many }) }), "the most is 64 a shape"));
            var shapes = new JArray(); for (int i = 0; i < 121; i++) shapes.Add(new JObject { ["type"] = "circle", ["x"] = i, ["y"] = 1, ["r"] = 1 });
            bad.Add(("canvas of 121 shapes", Win(new JObject { ["type"] = "canvas", ["id"] = "c", ["shapes"] = shapes }), "121 shapes; the most is 120"));
            var cards = new List<JObject>();
            for (int i = 0; i < 5; i++) { var kids = new JArray(); for (int k = 0; k < 9; k++) kids.Add(new JObject { ["type"] = "divider" }); cards.Add(new JObject { ["type"] = "card", ["components"] = kids }); }
            bad.Add(("50 components in five cards", Win(cards.ToArray()), "the most is 48"));
            foreach (var (what, spec, expect) in bad)
            {
                var (e, _) = CheckOne(spec);
                var hit = e.FirstOrDefault(m => m.Contains(expect));
                if (hit != null) Ok("refused: " + what + ": " + hit);
                else Fail("not refused as expected (" + what + "): wanted an error with '" + expect + "', got: " + (e.Count == 0 ? "no error" : string.Join("; ", e)));
            }

            var warn = new List<(string what, JObject spec, string expect)>
            {
                ("select value not among its options", Win(J(@"{""type"":""select"",""id"":""s"",""options"":[""a"",""b""],""value"":""c""}")), "is not one of its options"),
                ("tabs value not a tab id", Win(J(@"{""type"":""tabs"",""id"":""t"",""value"":""z"",""tabs"":[{""id"":""a"",""label"":""A"",""components"":[{""type"":""text"",""text"":""x""}]},{""id"":""b"",""label"":""B"",""components"":[{""type"":""text"",""text"":""y""}]}]}")), "is not one of its tab ids"),
                ("canvas selected not a shape", Win(J(@"{""type"":""canvas"",""id"":""c"",""selected"":""q"",""shapes"":[{""id"":""a"",""type"":""circle"",""x"":1,""y"":1,""r"":1}]}")), "is not the id of one of its shapes"),
            };
            foreach (var (what, spec, expect) in warn)
            {
                var (e, w) = CheckOne(spec);
                if (e.Count == 0 && w.Any(m => m.Contains(expect))) Ok("warned: " + what + ": " + w.First(m => m.Contains(expect)));
                else Fail("not warned as expected (" + what + "): errors " + string.Join("; ", e) + " warnings " + string.Join("; ", w));
            }
            Console.WriteLine(_fails == 0 ? "PASS [specs]" : "FAILED (" + _fails + ") [specs]");
        }
    }
}
