using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor.Android;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// A release-channel / store build (MCX_STORE=1) must not declare the permissions to install and update packages:
    /// the upload check of the headset maker's store rejects them (FAIL_FORBIDDEN_PERMISSION, 2026-11-18, for
    /// REQUEST_INSTALL_PACKAGES and UPDATE_PACKAGES_WITHOUT_USER_ACTION). They are removed here from the generated
    /// library manifest, so Assets/Plugins/Android/AndroidManifest.xml keeps them for the sideload build. Without them
    /// the app still builds native apps; installing one reports "this MetaCodex build cannot install apps"
    /// (MetaCodexInstaller.canInstallText, `mcx-app` exit code 3).
    public sealed class StoreManifest : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 1001; // after GradleMemory and Meta's OVRGradleGeneration

        private static readonly string[] Forbidden =
        {
            "android.permission.REQUEST_INSTALL_PACKAGES",
            "android.permission.UPDATE_PACKAGES_WITHOUT_USER_ACTION",
        };

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (Environment.GetEnvironmentVariable("MCX_STORE") != "1") return;
            var file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(file)) throw new Exception("[MetaCodex] store build: no manifest at " + file);
            string m = File.ReadAllText(file);
            foreach (var p in Forbidden)
                m = Regex.Replace(m, @"\s*<uses-permission[^>]*android:name=""" + Regex.Escape(p) + @"""[^>]*/>", "");
            foreach (var p in Forbidden)
                if (m.Contains(p)) throw new Exception("[MetaCodex] store build: " + p + " is still in " + file);
            File.WriteAllText(file, m);
            Debug.Log("[MetaCodex] store build: the manifest has no permission to install or update packages");
        }
    }
}
