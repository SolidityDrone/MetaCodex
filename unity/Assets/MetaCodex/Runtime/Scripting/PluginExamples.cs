using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.Scripting
{
    /// The example plugins that ship as real files: Resources/PluginExamples/<id>/plugin.json and main.lua.txt (TextAssets;
    /// installed as plugin.json and main.lua). One source of truth: the shell's examples list (ShellGestures.ExamplePlugins)
    /// and the PC test harness read the same files.
    public static class PluginExamples
    {
        public static readonly string[] Ids = { "timers", "calendar", "weather" };

        /// {"plugin.json": ..., "main.lua": ...} of one example; null when a file is missing (the example is then not listed).
        public static IReadOnlyDictionary<string, string> Files(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var manifest = Resources.Load<TextAsset>("PluginExamples/" + id + "/plugin");
            var main = Resources.Load<TextAsset>("PluginExamples/" + id + "/main.lua"); // main.lua.txt: Unity drops the last extension only
            if (manifest == null || main == null) return null;
            return new Dictionary<string, string> { ["plugin.json"] = manifest.text, ["main.lua"] = main.text };
        }
    }
}
