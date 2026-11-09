using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MetaCodex.App;
using MetaCodex.Linux;
using MetaCodex.Shell;
using MetaCodex.Terminal;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace MetaCodex.EditorTools
{
    /// Headless setup, self-test and build of the MetaCodex Quest app. Everything here runs in a batch
    /// Editor (no GUI): see the Batch* entry points and tools/unity-batch.sh.
    public static class MetaCodexSetup
    {
        public const string ScenePath = "Assets/Scenes/MetaCodex.unity";
        public const string PackageId = "com.pnncl.metacodex";
        public const string ApkPath = "Builds/MetaCodex.apk";
        public const string Version = "0.7.2";
        public const int VersionCode = 9;
        public const string PanelMaterialPath = "Assets/MetaCodex/Materials/Panel.mat";
        public const string ShapeMaterialPath = "Assets/MetaCodex/Materials/SciFiUI.mat";
        public const string BeamMaterialPath = "Assets/MetaCodex/Materials/Beam.mat";
        public const string DisplayFontPath = "Assets/MetaCodex/Fonts/Rajdhani-SemiBold.ttf"; // titles, buttons (console look)
        public const string BodyFontPath = "Assets/MetaCodex/Fonts/Inter-Medium.ttf";
        public const string ManifestPath = "Assets/Plugins/Android/AndroidManifest.xml";
        public const string MonoFontPath = "Assets/MetaCodex/Fonts/DejaVuSansMono.ttf";
        /// Glyphs DejaVu Sans Mono lacks, in order: general symbols, ⏺ (Symbols 2), ⎿ (Symbols), emoji.
        public static readonly string[] FallbackFontPaths =
        {
            "Assets/MetaCodex/Fonts/DejaVuSans.ttf",
            "Assets/MetaCodex/Fonts/NotoSansSymbols2-Regular.ttf",
            "Assets/MetaCodex/Fonts/NotoSansSymbols-Regular.ttf",
            "Assets/MetaCodex/Fonts/NotoEmoji-Regular.ttf",
        };
        private const string CameraRigPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        private const string HandPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRHandPrefab.prefab";
        public const string ReportDir = "Bench/selftest";

        // ------------------------------------------------------------------ batch entry points (-executeMethod)

        /// First open: configure the project, import TMP essentials, build the scene, run the self-test.
        public static void BatchSetup() => RunBatch(() =>
        {
            Log(ConfigureProject());
            Log(BuildScene());
            Log(SelfTest());
        });

        public static void BatchSelfTest() => RunBatch(() => Log(SelfTest()));

        /// Regenerates the scene first, so a build never ships a scene saved by older code.
        public static void BatchBuild() => RunBatch(() => { Log(BuildScene()); Log(BuildApk()); });

        /// Scene rebuild + both self-tests (terminal and shell UI): what a code change needs before an APK.
        public static void BatchCheck() => RunBatch(() =>
        {
            Log(BuildScene());
            Log(SelfTest());
            Log(MetaCodexUiTest.Run());
        });

        public static void BatchUiTest() => RunBatch(() => Log(MetaCodexUiTest.Run()));

        private static void RunBatch(Action body)
        {
            int code = 0;
            try { body(); }
            catch (Exception ex) { Debug.LogError("[MetaCodex] FAILED: " + ex); code = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        private static void Log(string s) => Debug.Log("[MetaCodex] " + s);

        // ------------------------------------------------------------------ project

        [MenuItem("MetaCodex/1. Configure Project")]
        public static string ConfigureProject()
        {
            ConfigurePlayer();
            ConfigureXR();
            ConfigureMetaProjectConfig();
            ConfigureUrp();
            string tmp = ImportTmpEssentials();
            AlwaysInclude("TextMeshPro/Mobile/Distance Field", "TextMeshPro/Distance Field");
            AssetDatabase.SaveAssets();
            return "project configured; " + tmp;
        }

        private static void ConfigurePlayer()
        {
            var android = NamedBuildTarget.Android;
            PlayerSettings.companyName = "pnncl";
            PlayerSettings.productName = "MetaCodex";
            PlayerSettings.bundleVersion = Version;
            PlayerSettings.Android.bundleVersionCode = VersionCode;
            PlayerSettings.SetApplicationIdentifier(android, PackageId);
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            // iteration speed: unoptimised C++ and shared generics compile far faster (quest-env-wsl 7)
            PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Debug);
            PlayerSettings.SetIl2CppCodeGeneration(android, Il2CppCodeGeneration.OptimizeSize);
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
            // the userland needs sockets (apt, npm, model APIs); Unity would only add INTERNET on its own
            // when it sees UnityWebRequest to the network
            PlayerSettings.Android.forceInternetPermission = true;
            // MQH, QVAC and local web apps answer on http://127.0.0.1 (loopback, no TLS)
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.enableFrameTimingStats = true;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
        }

        private static void ConfigureXR()
        {
            var getOrCreate = typeof(XRGeneralSettingsPerBuildTarget).GetMethod("GetOrCreate", BindingFlags.NonPublic | BindingFlags.Static);
            var perTarget = (XRGeneralSettingsPerBuildTarget)getOrCreate.Invoke(null, null);
            foreach (var group in new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone })
            {
                if (!perTarget.HasSettingsForBuildTarget(group)) perTarget.CreateDefaultSettingsForBuildTarget(group);
                if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
                perTarget.SettingsForBuildTarget(group).InitManagerOnStart = true;
                XRPackageMetadataStore.AssignLoader(perTarget.ManagerSettingsForBuildTarget(group), typeof(OpenXRLoader).FullName, group);
                var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                openxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                EditorUtility.SetDirty(openxr);
                SetFeatures(group, true,
                    "com.meta.openxr.feature.metaxr",
                    "com.unity.openxr.feature.input.oculustouch",
                    "com.unity.openxr.feature.input.metaquestplus",
                    "com.unity.openxr.feature.input.handinteraction");
            }
            // Ambient Focus reads the room's live depth (Meta's EnvironmentDepthManager): with the OpenXR loader the
            // depth comes through these two (com.unity.xr.meta-openxr; same setup as SandyBridge)
            SetFeatures(BuildTargetGroup.Android, true,
                "com.unity.openxr.feature.arfoundation-meta-session",
                "com.unity.openxr.feature.arfoundation-meta-occlusion");
            EditorUtility.SetDirty(perTarget);
        }

        private static void SetFeatures(BuildTargetGroup group, bool enabled, params string[] ids)
        {
            foreach (var id in ids)
            {
                var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(group, id);
                if (feature == null) { Debug.LogWarning($"[MetaCodex] OpenXR feature not found for {group}: {id}"); continue; }
                feature.enabled = enabled;
                EditorUtility.SetDirty(feature);
            }
        }

        private static void ConfigureMetaProjectConfig()
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            // hands are enough for everything (contest rule); controllers stay usable while developing
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            config.handTrackingFrequency = OVRProjectConfig.HandTrackingFrequency.HIGH;
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            // Ambient Focus: the Depth API needs the "spatial data" permission (USE_SCENE), no Space Setup scan; its
            // blur reads the passthrough camera (horizonos.permission.HEADSET_CAMERA, MR Utility Kit)
            config.anchorSupport = OVRProjectConfig.AnchorSupport.Disabled;
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
            config.isPassthroughCameraAccessEnabled = true;
            // TouchScreenKeyboard -> the headset's system keyboard over the immersive app
            config.requiresSystemKeyboard = true;
            // the Linux userland (compilers, node, agents) is CPU work; the scene is a few flat panels
            config.processorFavor = OVRProjectConfig.ProcessorFavor.FavorCPU;
            OVRProjectConfig.CommitProjectConfig(config);
        }

        /// Every quality level's URP asset: the batch Editor runs at the "PC" level while the headset uses
        /// "Mobile", whose template asset rendered at 0.8 scale without MSAA (blurry text on the Quest).
        private static void ConfigureUrp()
        {
            var assets = new List<UniversalRenderPipelineAsset>();
            for (int i = 0; i < QualitySettings.count; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset a && !assets.Contains(a)) assets.Add(a);
            var mobile = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset");
            if (mobile != null && !assets.Contains(mobile)) assets.Add(mobile);
            foreach (var asset in assets) ConfigureUrpAsset(asset);
        }

        private static void ConfigureUrpAsset(UniversalRenderPipelineAsset asset)
        {
            var so = new SerializedObject(asset);
            void SetBool(string p, bool v) { var x = so.FindProperty(p); if (x != null) x.boolValue = v; }
            void SetInt(string p, int v) { var x = so.FindProperty(p); if (x != null) x.intValue = v; }
            void SetFloat(string p, float v) { var x = so.FindProperty(p); if (x != null) x.floatValue = v; }
            SetBool("m_SupportsHDR", false);
            SetInt("m_MSAA", 4);
            SetFloat("m_RenderScale", 1f);
            SetBool("m_MainLightShadowsSupported", false);
            SetInt("m_AdditionalLightsRenderingMode", 0);
            SetBool("m_AdditionalLightShadowsSupported", false);
            SetBool("m_RequireDepthTexture", false);
            SetBool("m_RequireOpaqueTexture", false);
            SetBool("m_UseSRPBatcher", true);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        /// The TMP menu opens a modal dialog; a batch Editor can import the same package silently.
        public static string ImportTmpEssentials()
        {
            if (AssetDatabase.IsValidFolder("Assets/TextMesh Pro")) return "TMP essentials present";
            string pkg = Path.Combine(Path.GetFullPath("Packages/com.unity.ugui"), "Package Resources", "TMP Essential Resources.unitypackage");
            if (!File.Exists(pkg)) throw new FileNotFoundException("TMP essentials package not found", pkg);
            AssetDatabase.ImportPackage(pkg, false);
            AssetDatabase.Refresh();
            return AssetDatabase.IsValidFolder("Assets/TextMesh Pro") ? "TMP essentials imported" : "TMP essentials import queued";
        }

        private static void AlwaysInclude(params string[] shaderNames)
        {
            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in shaderNames)
            {
                var shader = Shader.Find(name);
                if (shader == null) { Debug.LogWarning("[MetaCodex] shader not found for Always Included: " + name); continue; }
                bool present = false;
                for (int i = 0; i < list.arraySize; i++) if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) present = true;
                if (present) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------ scene

        [MenuItem("MetaCodex/2. Build Scene")]
        public static string BuildScene()
        {
            var panelMaterial = GetOrCreateMaterial(PanelMaterialPath, "MetaCodex/PanelUnlit");
            var mono = AssetDatabase.LoadAssetAtPath<Font>(MonoFontPath) ?? throw new Exception("font missing: " + MonoFontPath);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var rigGo = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPrefab));
            var rig = rigGo.GetComponent<OVRCameraRig>();
            rig.EnsureGameObjectIntegrity(); // anchors are set in Awake; don't rely on a batch Editor having run it
            if (rig.centerEyeAnchor == null || rig.leftHandAnchor == null || rig.rightHandAnchor == null)
                throw new Exception("OVRCameraRig anchors missing after EnsureGameObjectIntegrity");
            ConfigureRig(rig);
            var leftHand = CreateTrackedHand(rig.leftHandAnchor, true);
            var rightHand = CreateTrackedHand(rig.rightHandAnchor, false);

            var workspace = new GameObject("Workspace").transform;
            var termGo = new GameObject("Terminal");
            termGo.transform.SetParent(workspace, false);
            var view = termGo.AddComponent<TerminalView>();
            var so = new SerializedObject(view);
            so.FindProperty("monoFont").objectReferenceValue = mono;
            SetFallbackFonts(so);
            so.FindProperty("panelMaterial").objectReferenceValue = panelMaterial;
            so.ApplyModifiedPropertiesWithoutUndo();

            var barGo = new GameObject("KeyBar");
            barGo.transform.SetParent(workspace, false);
            var bar = barGo.AddComponent<KeyBar>();
            so = new SerializedObject(bar);
            so.FindProperty("panelMaterial").objectReferenceValue = panelMaterial;
            so.ApplyModifiedPropertiesWithoutUndo();

            var statusGo = new GameObject("Status");
            statusGo.transform.SetParent(workspace, false);
            var status = statusGo.AddComponent<TextMeshPro>();
            status.fontSize = 0.14f;
            status.alignment = TextAlignmentOptions.BottomLeft;
            status.textWrappingMode = TextWrappingModes.NoWrap;
            status.rectTransform.pivot = new Vector2(0f, 0f);
            status.rectTransform.sizeDelta = new Vector2(1.2f, 0.03f);
            status.rectTransform.localPosition = new Vector3(0f, 0.006f, 0f);
            status.color = new Color(0.85f, 0.88f, 0.93f);
            status.text = "MetaCodex";

            var app = new GameObject("MetaCodex");
            var host = app.AddComponent<LinuxHost>();
            var input = app.AddComponent<TerminalInput>();
            var hands = app.AddComponent<HandTips>();
            hands.Set(leftHand.GetComponent<OVRSkeleton>(), rightHand.GetComponent<OVRSkeleton>());
            var mcx = app.AddComponent<MetaCodexApp>();
            so = new SerializedObject(mcx);
            so.FindProperty("host").objectReferenceValue = host;
            so.FindProperty("view").objectReferenceValue = view;
            so.FindProperty("input").objectReferenceValue = input;
            so.FindProperty("keyBar").objectReferenceValue = bar;
            so.FindProperty("hands").objectReferenceValue = hands;
            so.FindProperty("statusText").objectReferenceValue = status;
            so.FindProperty("head").objectReferenceValue = rig.centerEyeAnchor;
            so.ApplyModifiedPropertiesWithoutUndo();

            // the spatial shell: dock, panels, gestures, Lua scripts, MQH link
            var shell = app.AddComponent<MetaCodexShell>();
            so = new SerializedObject(shell);
            so.FindProperty("app").objectReferenceValue = mcx;
            so.FindProperty("leftHand").objectReferenceValue = leftHand;
            so.FindProperty("rightHand").objectReferenceValue = rightHand;
            so.FindProperty("displayFont").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Font>(DisplayFontPath) ?? throw new Exception("font missing: " + DisplayFontPath);
            so.FindProperty("bodyFont").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Font>(BodyFontPath) ?? throw new Exception("font missing: " + BodyFontPath);
            var sym = so.FindProperty("symbolFonts");
            sym.arraySize = FallbackFontPaths.Length;
            for (int i = 0; i < FallbackFontPaths.Length; i++) sym.GetArrayElementAtIndex(i).objectReferenceValue = AssetDatabase.LoadAssetAtPath<Font>(FallbackFontPaths[i]);
            so.FindProperty("shapeMaterial").objectReferenceValue = GetOrCreateMaterial(ShapeMaterialPath, "MetaCodex/SciFiUI");
            so.FindProperty("beamMaterial").objectReferenceValue = GetOrCreateMaterial(BeamMaterialPath, "MetaCodex/Beam");
            so.ApplyModifiedPropertiesWithoutUndo();

            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.5f);

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(silentMode: true);
            PatchManifest();
            AssetDatabase.SaveAssets();
            CheckSceneScripts(ScenePath);
            return "scene built: " + ScenePath;
        }

        private static void ConfigureRig(OVRCameraRig rig)
        {
            var manager = rig.GetComponent<OVRManager>() ?? rig.gameObject.AddComponent<OVRManager>();
            var so = new SerializedObject(manager);
            so.FindProperty("_trackingOriginType").intValue = (int)OVRManager.TrackingOrigin.FloorLevel;
            so.FindProperty("isInsightPassthroughEnabled").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();

            var passthrough = rig.GetComponent<OVRPassthroughLayer>() ?? rig.gameObject.AddComponent<OVRPassthroughLayer>();
            passthrough.overlayType = OVROverlay.OverlayType.Underlay;
            passthrough.textureOpacity = 1f;

            var eye = rig.centerEyeAnchor != null ? rig.centerEyeAnchor.GetComponent<Camera>() : null;
            if (eye == null) return;
            eye.clearFlags = CameraClearFlags.SolidColor;
            eye.backgroundColor = new Color(0f, 0f, 0f, 0f);
            eye.allowHDR = false;
            eye.allowMSAA = true;
            var data = eye.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = false;
            data.requiresDepthOption = CameraOverrideOption.Off;
            data.requiresColorOption = CameraOverrideOption.Off;
        }

        /// Meta's hand prefab for tracking data only (bones for poking); passthrough shows the real hands.
        private static OVRHand CreateTrackedHand(Transform anchor, bool left)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(HandPrefab), anchor);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = left ? "LeftHandTracking" : "RightHandTracking";
            foreach (var c in go.GetComponentsInChildren<OVRSkeletonRenderer>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<OVRMeshRenderer>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) UnityEngine.Object.DestroyImmediate(c);

            bool openXr = OVRRuntimeSettings.Instance.HandSkeletonVersion == OVRHandSkeletonVersion.OpenXR;
            var hand = go.GetComponent<OVRHand>();
            var so = new SerializedObject(hand);
            so.FindProperty("HandType").intValue = (int)(left ? OVRPlugin.Hand.HandLeft : OVRPlugin.Hand.HandRight);
            so.ApplyModifiedPropertiesWithoutUndo();
            var skeletonType = openXr
                ? (left ? OVRSkeleton.SkeletonType.XRHandLeft : OVRSkeleton.SkeletonType.XRHandRight)
                : (left ? OVRSkeleton.SkeletonType.HandLeft : OVRSkeleton.SkeletonType.HandRight);
            so = new SerializedObject(go.GetComponent<OVRSkeleton>());
            so.FindProperty("_skeletonType").intValue = (int)skeletonType;
            so.ApplyModifiedPropertiesWithoutUndo();
            var mesh = go.GetComponent<OVRMesh>();
            if (mesh != null)
            {
                var meshType = openXr
                    ? (left ? OVRMesh.MeshType.XRHandLeft : OVRMesh.MeshType.XRHandRight)
                    : (left ? OVRMesh.MeshType.HandLeft : OVRMesh.MeshType.HandRight);
                so = new SerializedObject(mesh);
                so.FindProperty("_meshType").intValue = (int)meshType;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            return hand;
        }

        /// What the OVR manifest generator does not add: (1) package visibility for the Horizon OS shell
        /// (system deep links: browser, settings, Files) and (2) boundaryless mode (Meta docs "Boundaryless Mode
        /// for Mixed Reality in Unity": passthrough-only apps may drop the boundary for the whole experience).
        public static void PatchManifest()
        {
            if (!File.Exists(ManifestPath)) throw new Exception("manifest missing: " + ManifestPath);
            string m = File.ReadAllText(ManifestPath);
            string before = m;
            if (!m.Contains("com.oculus.feature.BOUNDARYLESS_APP"))
                m = m.Replace("<application", "<uses-feature android:name=\"com.oculus.feature.BOUNDARYLESS_APP\" android:required=\"true\" />\n  <application");
            if (!m.Contains("<queries>"))
                m = m.Replace("<application", "<queries>\n    <package android:name=\"com.oculus.vrshell\" />\n  </queries>\n  <application");
            if (m != before) File.WriteAllText(ManifestPath, m);
            AssetDatabase.ImportAsset(ManifestPath);
        }

        public static void SetFallbackFonts(SerializedObject so)
        {
            var list = so.FindProperty("fallbackFonts");
            list.arraySize = FallbackFontPaths.Length;
            for (int i = 0; i < FallbackFontPaths.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = AssetDatabase.LoadAssetAtPath<Font>(FallbackFontPaths[i])
                    ?? throw new Exception("font missing: " + FallbackFontPaths[i]);
        }

        public static Material GetOrCreateMaterial(string path, string shaderName)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            var shader = Shader.Find(shaderName) ?? throw new Exception($"Shader {shaderName} not found (compile error?)");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            m = new Material(shader) { enableInstancing = true };
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // ------------------------------------------------------------------ self-test (Editor, no headset)

        /// Renders the terminal view with real program output (the emulator test corpus) through the same
        /// TMP + mesh path the headset uses, checks glyph coverage, shader errors and timing, and saves a
        /// picture: Bench/selftest/terminal.png + report.txt.
        [MenuItem("MetaCodex/3. Self-test")]
        public static string SelfTest()
        {
            Directory.CreateDirectory(ReportDir);
            var report = new StringBuilder();
            bool ok = true;
            void Line(string s) { report.AppendLine(s); }
            void Fail(string s) { ok = false; report.AppendLine("FAIL " + s); }

            var panelMaterial = GetOrCreateMaterial(PanelMaterialPath, "MetaCodex/PanelUnlit");
            foreach (var shaderName in new[] { "MetaCodex/PanelUnlit", "TextMeshPro/Mobile/Distance Field", "TextMeshPro/Distance Field" })
            {
                var sh = Shader.Find(shaderName);
                if (sh == null) { Fail("shader missing: " + shaderName); continue; }
                int errors = ShaderUtil.GetShaderMessages(sh).Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
                if (errors > 0) Fail($"shader {shaderName}: {errors} errors"); else Line($"shader {shaderName}: ok");
            }

            var root = new GameObject("MetaCodexSelfTest") { hideFlags = HideFlags.DontSave };
            try
            {
                var termGo = new GameObject("Terminal");
                termGo.transform.SetParent(root.transform, false);
                var view = termGo.AddComponent<TerminalView>();
                var so = new SerializedObject(view);
                so.FindProperty("monoFont").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Font>(MonoFontPath);
                SetFallbackFonts(so);
                so.FindProperty("panelMaterial").objectReferenceValue = panelMaterial;
                so.ApplyModifiedPropertiesWithoutUndo();

                var term = new VtTerminal(30, 96, 2000);
                view.Attach(term);
                string corpus = Path.GetFullPath("../tests/terminal/corpus/cases");
                foreach (var name in new[] { "bash_colors.bin", "unicode.bin" })
                {
                    string p = Path.Combine(corpus, name);
                    if (File.Exists(p)) term.Feed(File.ReadAllBytes(p));
                    term.Feed("\r\n");
                }
                term.Feed("\r\n\x1b[1mMetaCodex self-test\x1b[0m \x1b[38;5;114mgreen\x1b[0m \x1b[48;5;24m blue bg \x1b[0m \x1b[7m inverse \x1b[0m ╭─╮ ⏺ ✻ 中文\r\n$ ");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                view.Render();
                sw.Stop();
                Line($"first full render: {sw.Elapsed.TotalMilliseconds:F1} ms (Editor, includes glyph rasterisation)");
                for (int i = 0; i < 30; i++) term.Feed($"\r\x1b[2Kspinner frame {i} ✻");
                sw.Restart();
                view.Render();
                sw.Stop();
                Line($"one-row update: {sw.Elapsed.TotalMilliseconds:F2} ms");

                var font = view.Font;
                string needed = "abcABC123 ─│╭╮╰╯┌┐└┘├┤▏█░▒▓→←↑↓✓✗⏺✻⎿●•…";
                // searches the fallback (symbol) font too, adding glyphs to the dynamic atlases as it goes
                font.HasCharacters(needed, out uint[] missing, true, true);
                if (missing != null && missing.Length > 0)
                    Fail("glyphs missing in mono+fallback: " + string.Join(" ", missing.Select(c => "U+" + c.ToString("X4"))));
                else Line("glyph coverage ok: " + needed);

                var texts = termGo.GetComponentsInChildren<TextMeshPro>();
                int chars = 0;
                foreach (var t in texts) { t.ForceMeshUpdate(); chars += t.textInfo.characterCount; }
                Line($"{texts.Length} row texts, {chars} characters laid out");
                if (texts.Length != 30 || chars < 500) Fail("rows not laid out");

                // a picture through a camera, as a person would see it
                var camGo = new GameObject("cam");
                camGo.transform.SetParent(root.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.2f, 0.2f, 0.22f);
                cam.fieldOfView = 60f;
                float w = view.Width, h = view.Height;
                float tan = Mathf.Tan(30f * Mathf.Deg2Rad);
                float dist = Mathf.Max((h / 2f) / tan, (w / 2f) / (tan * 1.6f)) + 0.02f; // fit both sides at aspect 1.6
                cam.transform.position = new Vector3(w / 2f, -h / 2f, -dist);
                cam.transform.rotation = Quaternion.identity;
                var rt = new RenderTexture(1600, 1000, 24) { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.aspect = 1.6f;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(ReportDir, "terminal.png"), tex.EncodeToPNG());
                Line("picture: " + Path.GetFullPath(Path.Combine(ReportDir, "terminal.png")));
                UnityEngine.Object.DestroyImmediate(tex);
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                Line("screen text:\n" + term.DumpScreen());
            }
            catch (Exception ex) { Fail("exception: " + ex); }
            finally { UnityEngine.Object.DestroyImmediate(root); }

            string summary = (ok ? "SELFTEST OK" : "SELFTEST FAILED") + "\n" + report;
            File.WriteAllText(Path.Combine(ReportDir, "report.txt"), summary);
            if (!ok) throw new Exception(summary);
            return summary.Split('\n')[0] + " (" + Path.Combine(ReportDir, "report.txt") + ")";
        }

        // ------------------------------------------------------------------ build

        /// Every component in the scene must point at a real script asset (fileID 11500000 + guid). A component
        /// whose class does not live in a file of the same name gets an embedded stand-in script instead
        /// (`m_Script: {fileID: N}`): the Editor accepts it, the player then misreads the scene data and aborts
        /// with "level0 is corrupted! [Position out of bounds!]" (HandTips in KeyBar.cs, 2026-11-07).
        public static void CheckSceneScripts(string scenePath)
        {
            var bad = new List<string>();
            var lines = File.ReadAllLines(scenePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (!l.StartsWith("m_Script:")) continue;
                if (l.Contains("fileID: 11500000") && l.Contains("guid:")) continue;
                if (l == "m_Script: {fileID: 0}") { bad.Add($"line {i + 1}: missing script"); continue; }
                bad.Add($"line {i + 1}: {l}");
            }
            if (bad.Count > 0)
                throw new Exception("scene has components without a script asset (a MonoBehaviour class must live in a file of the same name): " + string.Join("; ", bad));
        }

        public static string BuildApk()
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            ConfigurePlayer();
            ConfigureMetaProjectConfig();
            OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(silentMode: true);
            PatchManifest();
            ConfigureUrp(); // full resolution + 4x MSAA on the headset's quality level, every build
            // fewer parallel C++ compiles: an IL2CPP build with 8 jobs needs ~6 GB on top of the Editor, which this
            // laptop rarely has free (browser + WSL); 2 jobs build slower but fit. MCX_IL2CPP_ARGS overrides (only when
            // set on the Windows side: WSLENV does not carry it into the Editor on this machine).
            PlayerSettings.SetAdditionalIl2CppArgs(Environment.GetEnvironmentVariable("MCX_IL2CPP_ARGS") ?? "--jobs=2 --bee-jobs=2");
            Directory.CreateDirectory("Builds");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development, // debuggable: run-as for the private files while the userland is young
            });
            var s = report.summary;
            string result = $"{s.result}: {s.totalErrors} errors, {s.totalTime.TotalSeconds:F0} s, {ApkPath} ({s.totalSize / (1024 * 1024)} MB)";
            if (s.result != BuildResult.Succeeded) throw new Exception("[MetaCodex] build failed: " + result);
            return result;
        }
    }
}
