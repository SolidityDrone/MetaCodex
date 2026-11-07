using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Android;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Keeps the APK build's Gradle step inside the RAM this laptop has free next to a browser and WSL: Unity's
    /// generated gradle.properties gives the Gradle daemon -Xmx4096m and parallel workers, which pushed Windows
    /// under the batch guard's limit on a full (uncached) Gradle build. Here: 1.5 GB heap (MCX_GRADLE_XMX
    /// overrides), two workers, no parallel projects, Kotlin in-process, and no daemon left running afterwards.
    public sealed class GradleMemory : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 1000; // after Meta's OVRGradleGeneration

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var file = Path.GetFullPath(Path.Combine(path, "..", "gradle.properties"));
            if (!File.Exists(file)) { Debug.LogWarning("[MetaCodex] no gradle.properties at " + file); return; }
            string xmx = Environment.GetEnvironmentVariable("MCX_GRADLE_XMX");
            if (string.IsNullOrWhiteSpace(xmx)) xmx = "1536m";
            var set = new Dictionary<string, string>
            {
                ["org.gradle.jvmargs"] = $"-Xmx{xmx} -XX:MaxMetaspaceSize=512m -Dfile.encoding=UTF-8",
                ["org.gradle.parallel"] = "false",
                ["org.gradle.workers.max"] = "2",
                ["org.gradle.daemon"] = "false",
                ["kotlin.compiler.execution.strategy"] = "in-process",
            };
            var lines = File.ReadAllLines(file).ToList();
            foreach (var kv in set)
            {
                int i = lines.FindIndex(l => l.TrimStart().StartsWith(kv.Key + "="));
                string line = kv.Key + "=" + kv.Value;
                if (i >= 0) lines[i] = line; else lines.Add(line);
            }
            File.WriteAllLines(file, lines);
            Debug.Log($"[MetaCodex] gradle.properties: heap {xmx}, 2 workers, no parallel, no daemon");
        }
    }
}
