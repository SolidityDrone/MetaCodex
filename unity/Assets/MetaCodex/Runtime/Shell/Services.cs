using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MetaCodex.Hands;
using MetaCodex.Linux;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MetaCodex.Shell
{
    /// Horizon OS system deep links (Meta docs "System Deep Linking"): the shell app com.oculus.vrshell opens
    /// the browser at a URL, Settings pages or the Files app. Needs <queries> for com.oculus.vrshell in the
    /// manifest (added by the project setup); falls back to the explicit activity of Meta's adb example.
    public static class SystemBridge
    {
        public static string LastResult { get; private set; } = "";
        /// Tests: record instead of launching anything.
        public static bool DryRun;

        public static bool OpenBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.Trim();
            if (!Regex.IsMatch(url, "^[a-zA-Z][a-zA-Z0-9+.-]*://")) url = "https://" + url;
            if (DeepLink("systemux://browser", url)) return true;
            if (DryRun) return true;
#if UNITY_ANDROID && !UNITY_EDITOR
            Application.OpenURL(url);
            LastResult = "OpenURL " + url;
            return true;
#else
            LastResult = "(editor) browser " + url;
            return false;
#endif
        }

        public static bool OpenHandSettings() => DeepLink("systemux://settings", "/hands");

        // ---- installing apps built in the Linux userland (MetaCodexInstaller.java: Android's PackageInstaller)

        private const string InstallerClass = "com.pnncl.metacodex.install.MetaCodexInstaller";

        /// The installer's last state: idle, installing, waiting for the user to confirm, success: pkg, failure: why.
        public static string InstallStatus()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { using (var c = new AndroidJavaClass(InstallerClass)) return c.CallStatic<string>("status"); }
            catch (Exception ex) { return "failure: " + ex.Message; }
#else
            return DryRun ? "idle (dry run)" : "not on the headset";
#endif
        }

        /// Installs an APK (an Android path the app can read). When the user has not let MetaCodex install apps yet,
        /// opens that switch instead and says so.
        public static string InstallApk(string androidPath)
        {
            if (DryRun) { LastResult = "install " + androidPath; return "installing (dry run)"; }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var c = new AndroidJavaClass(InstallerClass))
                {
                    string can = c.CallStatic<string>("canInstallText", activity);
                    if (can.StartsWith("unavailable")) return can;
                    if (!can.StartsWith("yes"))
                    {
                        string r = c.CallStatic<string>("openUnknownSourcesSettings", activity);
                        return r == "ok" ? "needs your OK: allow MetaCodex to install apps (the settings page is open), then install again" : r;
                    }
                    return c.CallStatic<string>("install", activity, androidPath);
                }
            }
            catch (Exception ex) { return "failure: " + ex.Message; }
#else
            return "not on the headset";
#endif
        }

        /// yes | no: the "install unknown apps" switch is off | unavailable: this build cannot install apps. Opens nothing.
        public static string CanInstall()
        {
            if (DryRun) return "yes (dry run)";
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var c = new AndroidJavaClass(InstallerClass))
                    return c.CallStatic<string>("canInstallText", activity);
            }
            catch (Exception ex) { return "failure: " + ex.Message; }
#else
            return "unavailable: not on the headset";
#endif
        }

        /// installed: pkg version (code), installer, last update | not installed: pkg.
        public static string AppInfo(string package)
        {
            if (DryRun) { LastResult = "app " + package; return "not installed: " + package + " (dry run)"; }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var c = new AndroidJavaClass(InstallerClass))
                    return c.CallStatic<string>("info", activity, package);
            }
            catch (Exception ex) { return "failure: " + ex.Message; }
#else
            return "not on the headset";
#endif
        }

        /// Starts an installed app by its package name; `launchId` goes to the app (its way back: MetaCodexReturn).
        public static string LaunchApp(string package, string launchId = null)
        {
            if (DryRun) { LastResult = "launch " + package + (launchId != null ? " id " + launchId : ""); return "ok (dry run)"; }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var c = new AndroidJavaClass(InstallerClass))
                    return c.CallStatic<string>("launch", activity, package, launchId ?? "");
            }
            catch (Exception ex) { return "failure: " + ex.Message; }
#else
            return "not on the headset";
#endif
        }
        /// A return from an app MetaCodex launched (its menu's Back / Report), read once from the activity's intent:
        /// "" or the JSON of MetaCodexReturn.take (the report's picture and log written into `dir`).
        public static string TakeReturn(string dir)
        {
            if (DryRun) return "";
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var c = new AndroidJavaClass("com.pnncl.metacodex.install.MetaCodexReturn"))
                    return c.CallStatic<string>("take", activity, dir ?? "");
            }
            catch (Exception) { return ""; }
#else
            return "";
#endif
        }
        public static bool OpenAppSettings() => DeepLink("systemux://settings", "/applications?package=" + Application.identifier);
        /// The headset's Settings (its first page: no deep link to Experimental is known to work).
        public static bool OpenSystemSettings() => DeepLink("systemux://settings", null);
        public static bool OpenFilesApp() => DeepLink("systemux://file-manager", null);

        private static bool DeepLink(string intentData, string uri)
        {
            if (DryRun) { LastResult = $"deep link {intentData} {uri}"; return true; }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var pm = activity.Call<AndroidJavaObject>("getPackageManager"))
                {
                    AndroidJavaObject intent = pm.Call<AndroidJavaObject>("getLaunchIntentForPackage", "com.oculus.vrshell");
                    if (intent == null)
                    {
                        intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.VIEW");
                        intent.Call<AndroidJavaObject>("setClassName", "com.oculus.vrshell", "com.oculus.vrshell.MainActivity");
                        using (var uriClass = new AndroidJavaClass("android.net.Uri"))
                            intent.Call<AndroidJavaObject>("setData", uriClass.CallStatic<AndroidJavaObject>("parse", intentData));
                    }
                    intent.Call<AndroidJavaObject>("putExtra", "intent_data", intentData);
                    if (!string.IsNullOrEmpty(uri)) intent.Call<AndroidJavaObject>("putExtra", "uri", uri);
                    activity.Call("startActivity", intent);
                    intent.Dispose();
                }
                LastResult = $"deep link {intentData} {uri}";
                return true;
            }
            catch (Exception ex)
            {
                LastResult = "deep link failed: " + ex.Message;
                Debug.LogWarning("[MetaCodex] " + LastResult);
                return false;
            }
#else
            LastResult = $"(editor) deep link {intentData} {uri}";
            return false;
#endif
        }
    }

    /// Everything the user sets in the headset, persisted as JSON in the app's files and mirrored to MQH
    /// (ui preference "quest.settings") so agents can read and change it.
    [Serializable]
    public sealed class ShellSettings
    {
        public GestureSettings gestures = new GestureSettings();
        public float panelDistance = 0.8f;
        /// "fixed": panels stay where you put them (world-locked). "float": a carousel that follows you horizontally.
        public string layout = "fixed";
        public float carouselRadius = 0.95f;
        public bool bendPanels = true;
        public float uiScale = 1.2f;
        /// MAIN MENU: false = the DOCK panel in front of you, true = the WRIST menu (look at the back of your left wrist).
        public bool wristMenu = false;
        /// Render resolution x (supersampling). Fixed at MetaCodexShell.RenderScale (1.6, owner 2026-11-09: no SHARPNESS
        /// slider any more); kept so older settings files and MQH's copy still load.
        public float renderScale = 1.6f;
        /// Fixed foveated rendering: saves GPU, blurs the edges of the view (off for a text-heavy app).
        public bool foveation = false;
        /// One-time upgrades of saved settings (2: readability defaults, 2026-11-07). 0 in files written before.
        public int settingsVersion = 0;
        public string accent = "blue";
        public bool scanlines = false;
        public string mqhUrl = "http://127.0.0.1:38990";
        public string mqhToken = "";
        public bool mqhSync = true;
        public string filesRoot = "/root";
        public List<string> bookmarks = new List<string> { "https://developers.meta.com/horizon/", "https://github.com", "http://127.0.0.1:5173" };
        /// Gesture -> action links (Gestures panel > BIND).
        public List<GestureBinding> bindings = new List<GestureBinding>();
        // look and feel
        public float panelOpacity = 0.92f;
        /// The image clipboard keeps this many captures (the newest); one more deletes the oldest from the Linux.
        public int clipboardMax = 20;
        public float motion = 0f;
        public bool grid = false;
        public bool sounds = true;
        public float soundVolume = 0.35f;
        // agents: defaults for new threads
        public string defaultProvider = "";
        public string defaultModel = "";
        public string permissionMode = "accept-edits";
        public string workspaceMode = "local";
        public string lastProject = "";
        /// Named panel layouts (DISPLAY > SAVE / LOAD).
        public List<SavedLayout> layouts = new List<SavedLayout>();
        /// Voice control and the local AI service (VOICE panel).
        public VoiceSettings voice = new VoiceSettings();
        /// Show the first terminal when the app starts (off: it runs hidden until TERMS on the menu shows it).
        public bool terminalAtStart = false;
        /// Agent views (charts, diagrams) open by themselves when a thread message brings one.
        public bool viewsAutoOpen = true;
        /// THREAD's sidebar, per thread: "threadId|open (1/0)|tab" (ShellThreadSidebar; the newest 80 threads).
        public List<string> threadSidebar = new List<string>();
        /// Ambient Focus: darken the room beyond a radius (Settings > FOCUS, FOCUS on the dock).
        public bool focusEnabled = false;
        public float focusRadius = 1.2f;
        public float focusIntensity = 0.75f;
        public float focusSoftness = 0.6f;
        /// Outside the dome: 0 = darkened only; above 0 the passthrough camera's image, soft (more = softer).
        public float focusBlur = 0f;
        /// Real things (hands, people, objects) in front of a window are shown, from the room's live depth.
        public bool occludeUi = true;
        /// The tip for the system's Surface keyboard (Settings > Experimental), the first time the keyboard opens in a session.
        public bool surfaceKeyboardTip = true;
        /// The depth is sharpened with the passthrough camera's picture (cleaner cut-outs; the camera runs).
        public bool depthCamera = true;
        /// SYSTEM > STORAGE: warn when the Linux userland grows past this many GB (0: no limit).
        public float linuxLimitGB = 0f;
        /// The orb: it casts the panels, speaks, and shows the app's messages beside it (Settings > LOOK).
        public bool orb = true;
        /// The user put the orb somewhere (a pinch): it keeps that place by them, yaw off the way they face and pitch in
        /// degrees, metres away. Not pinned: it chooses a free place up to the right.
        public bool orbPinned = false;
        public float orbYaw = 32f, orbPitch = 16f, orbDist = 0.52f;
        /// The one-time message that says what the orb is (touch it, pinch it) has been shown.
        public bool orbTipShown = false;

        public static string PathOnDisk => Path.Combine(Application.persistentDataPath, "metacodex-settings.json");

        public static ShellSettings Load(string path = null)
        {
            path ??= PathOnDisk;
            try
            {
                if (File.Exists(path))
                {
                    var s = JsonUtility.FromJson<ShellSettings>(File.ReadAllText(path)) ?? new ShellSettings();
                    if (s.voice != null) s.voice.language = "en"; // English only since 2026-11-12: a stored "it" / "auto" reads as "en"
                    return s;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[MetaCodex] settings unreadable, defaults used: " + ex.Message); }
            return new ShellSettings();
        }

        public void Save(string path = null)
        {
            path ??= PathOnDisk;
            try { File.WriteAllText(path, JsonUtility.ToJson(this, true)); }
            catch (Exception ex) { Debug.LogWarning("[MetaCodex] settings not saved: " + ex.Message); }
        }

        /// Flat key access for scripts and the daemon ("gestures.clickMode", "accent", ...).
        public string Get(string key)
        {
            var j = JObject.Parse(JsonUtility.ToJson(this));
            var tok = j.SelectToken(key);
            return tok == null ? null : tok.Type == JTokenType.String ? (string)tok : tok.ToString(Newtonsoft.Json.Formatting.None);
        }

        public bool Set(string key, string value)
        {
            var j = JObject.Parse(JsonUtility.ToJson(this));
            var tok = j.SelectToken(key);
            if (tok == null || tok is JContainer) return false;
            JToken v;
            switch (tok.Type)
            {
                case JTokenType.Integer: if (!long.TryParse(value, out long l)) return false; v = l; break;
                case JTokenType.Float: if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)) return false; v = f; break;
                case JTokenType.Boolean: if (!bool.TryParse(value, out bool b)) return false; v = b; break;
                default: v = value; break;
            }
            tok.Replace(v);
            JsonUtility.FromJsonOverwrite(j.ToString(), this);
            return true;
        }

        /// What is synced to MQH (quest.settings): everything except the MQH token itself.
        public JObject ToJObject()
        {
            var j = JObject.Parse(JsonUtility.ToJson(this));
            j.Remove("mqhToken");
            return j;
        }
    }

    /// Where the open panels were, relative to the user's head yaw, under a name.
    [Serializable]
    public sealed class SavedLayout
    {
        public string name = "";
        public string mode = "fixed";
        public List<SavedPanel> panels = new List<SavedPanel>();
    }

    [Serializable]
    public sealed class SavedPanel
    {
        public string id = "";
        /// position relative to the head (yaw frame: x right, y up, z forward) and yaw relative to the head's yaw
        public Vector3 offset;
        public float yaw;
        public float bend;
    }

    /// Talks to MQH's public API on loopback (HttpClient off the main thread; results come back through Post).
    public sealed class MqhClient
    {
        public string BaseUrl;
        public string Token;
        public string LastError { get; private set; }
        public bool Online { get; private set; }
        private static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private readonly ConcurrentQueue<Action> _main;

        static MqhClient()
        {
            // Mono's HttpClient sits on HttpWebRequest: 2 connections per host by default, so a few slow calls
            // (provider checks, thread start) queue everything behind them
            try { System.Net.ServicePointManager.DefaultConnectionLimit = Math.Max(System.Net.ServicePointManager.DefaultConnectionLimit, 16); } catch (Exception) { }
        }

        public MqhClient(string baseUrl, string token, ConcurrentQueue<Action> mainThread) { BaseUrl = baseUrl; Token = token; _main = mainThread; }

        /// Seconds a call may take: quick reads 15 s; starting threads, installs and provider checks longer.
        private static int TimeoutFor(HttpMethod m, string path) =>
            path.StartsWith("/api/v1/threads") && m == HttpMethod.Post && (path.Length <= "/api/v1/threads".Length + 1 || path.StartsWith("/api/v1/threads/fork")) ? 60 :
            path.Contains("/providers/state") || path.Contains("/execution-options") ? 45 :
            path.StartsWith("/api/v1/skills-registry") ? 60 : 15; // the registry reads skills.sh and GitHub

        private HttpRequestMessage Req(HttpMethod m, string path, JToken body = null)
        {
            var r = new HttpRequestMessage(m, BaseUrl.TrimEnd('/') + path);
            if (!string.IsNullOrEmpty(Token)) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            if (body != null) r.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            return r;
        }

        public async Task<JToken> Send(HttpMethod m, string path, JToken body = null)
        {
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(TimeoutFor(m, path))))
                using (var res = await Http.SendAsync(Req(m, path, body), cts.Token).ConfigureAwait(false))
                {
                    string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) throw new Exception($"{(int)res.StatusCode} {Truncate(text)}");
                    Online = true;
                    LastError = null;
                    return string.IsNullOrWhiteSpace(text) ? new JObject() : JToken.Parse(text);
                }
            }
            catch (Exception ex)
            {
                Online = false;
                LastError = ex is TaskCanceledException ? $"MQH did not answer in {TimeoutFor(m, path)} s ({path.Split('?')[0]})" : ex is HttpRequestException ? "MQH not reachable at " + BaseUrl : ex.Message;
                throw;
            }
        }

        private static string Truncate(string s) => s == null ? "" : s.Length > 160 ? s.Substring(0, 160) : s;

        private int _inFlight;
        /// Requests sent whose callback has not been queued yet (tests wait for 0).
        public int InFlight => System.Threading.Volatile.Read(ref _inFlight);

        /// Fire-and-forget with a main-thread callback (null result on failure).
        public void Call(HttpMethod m, string path, JToken body, Action<JToken> onMain)
        {
            System.Threading.Interlocked.Increment(ref _inFlight);
            Task.Run(async () =>
            {
                JToken r = null;
                try { r = await Send(m, path, body).ConfigureAwait(false); } catch (Exception) { }
                _main.Enqueue(() => onMain?.Invoke(r));
                System.Threading.Interlocked.Decrement(ref _inFlight);
            });
        }

        /// Like Call, but `work` runs on the request's pool thread (merging, parsing): only its result reaches the main
        /// thread (the THREAD chat: no frame pays for a timeline's JSON).
        public void CallThen<T>(HttpMethod m, string path, JToken body, Func<JToken, T> work, Action<T> onMain)
        {
            System.Threading.Interlocked.Increment(ref _inFlight);
            Task.Run(async () =>
            {
                JToken r = null;
                try { r = await Send(m, path, body).ConfigureAwait(false); } catch (Exception) { }
                T result = default;
                try { result = work(r); } catch (Exception ex) { LastError = "parsing " + path.Split('?')[0] + ": " + ex.Message; }
                _main.Enqueue(() => onMain?.Invoke(result));
                System.Threading.Interlocked.Decrement(ref _inFlight);
            });
        }

        public static string TimelinePath(string id, string afterTag, int segments) =>
            $"/api/v1/threads/{E(id)}/timeline?includeNestedRows=true" + (segments > 0 ? "&segmentLimit=" + segments : "") + (string.IsNullOrEmpty(afterTag) ? "" : "&afterTag=" + E(afterTag));

        public void Version(Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/system/version", null, cb);
        public void Plugins(Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/plugins", null, cb);
        /// Skills MQH gives the agents (its own, plugins', each provider's), seen from a project.
        public void Skills(string projectId, Action<JToken> cb) => Call(HttpMethod.Get, $"/api/v1/projects/{E(projectId)}/skills?environmentId=", null, cb);
        public void DeleteSkill(string projectId, string skillId, Action<JToken> cb) =>
            Call(HttpMethod.Delete, $"/api/v1/projects/{E(projectId)}/skills", new JObject { ["skillId"] = skillId, ["environmentId"] = null }, cb);
        /// The public skills registry (skills.sh), through MQH.
        public void SearchSkills(string query, Action<JToken> cb) =>
            Call(HttpMethod.Get, "/api/v1/skills-registry?page=0&perPage=24" + (string.IsNullOrWhiteSpace(query) ? "" : "&q=" + E(query.Trim())), null, cb);
        public void InstallSkill(string registrySkillId, Action<JToken> cb) =>
            Call(HttpMethod.Post, "/api/v1/skills-registry/install", new JObject { ["registrySkillId"] = registrySkillId }, cb);
        public void Providers(Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/system/providers", null, cb);
        public void SetPluginEnabled(string id, bool on, Action<JToken> cb) => Call(HttpMethod.Post, $"/api/v1/plugins/{Uri.EscapeDataString(id)}/{(on ? "enable" : "disable")}", new JObject(), cb);
        public void Preferences(Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/preferences/ui", null, cb);

        private static string E(string s) => Uri.EscapeDataString(s ?? "");

        // ---- agents: providers, sign-in state, models
        public void ProvidersState(Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/system/providers/state", null, cb);
        public void SetProviderEnabled(string id, bool on, Action<JToken> cb) => Call(HttpMethod.Put, $"/api/v1/system/providers/{E(id)}/enabled", new JObject { ["enabled"] = on }, cb);
        /// Install or update a provider CLI on a host (the daemon runs the vendor's installer). The reply is a
        /// text event stream; the callback gets a non-null token when the request was accepted.
        public void InstallProviderCli(string hostId, string providerId, bool update, Action<JToken> cb) =>
            CallRaw(HttpMethod.Post, $"/api/v1/hosts/{E(hostId)}/provider-clis/install", new JObject { ["provider"] = providerId, ["actionKind"] = update ? "update" : "install" }, cb, 600);
        public void Models(string providerId, string hostId, Action<JToken> cb) =>
            Call(HttpMethod.Get, $"/api/v1/system/execution-options?providerId={E(providerId)}" + (string.IsNullOrEmpty(hostId) ? "" : "&hostId=" + E(hostId)), null, cb);

        // ---- workspaces: hosts, projects, environments
        public void Hosts(Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/hosts", null, cb);
        public void Projects(Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/projects", null, cb);
        public void CreateProject(string name, string hostId, string path, Action<JToken> cb) =>
            Call(HttpMethod.Post, "/api/v1/projects", new JObject { ["name"] = name, ["source"] = new JObject { ["hostId"] = hostId, ["type"] = "local_path", ["path"] = path } }, cb);
        public void Environment(string id, Action<JToken> cb) => Call(HttpMethod.Get, $"/api/v1/environments/{E(id)}", null, cb);

        // ---- threads
        public void Threads(string projectId, Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/threads" + (string.IsNullOrEmpty(projectId) ? "" : "?projectId=" + E(projectId)), null, cb);
        public void Thread(string id, Action<JToken> cb) => Call(HttpMethod.Get, $"/api/v1/threads/{E(id)}", null, cb);
        public void CreateThread(JObject body, Action<JToken> cb) => Call(HttpMethod.Post, "/api/v1/threads", body, cb);
        public void Timeline(string id, Action<JToken> cb) => Call(HttpMethod.Get, $"/api/v1/threads/{E(id)}/timeline", null, cb);
        /// The timeline with every turn's steps (thinking, commands, edits); with afterTag (the last answer's `tag`)
        /// MQH answers with a delta (changed rows only) while it still knows that answer.
        public void Timeline(string id, string afterTag, int segments, Action<JToken> cb) => Call(HttpMethod.Get, TimelinePath(id, afterTag, segments), null, cb);
        /// The provider's plan and usage windows (5 hour, weekly, ...), as MQH's provider plugin reports them.
        public void UsageLimits(string providerId, string hostId, Action<JToken> cb) =>
            Call(HttpMethod.Get, "/api/v1/system/usage-limits?providerId=" + E(providerId) + (string.IsNullOrEmpty(hostId) ? "" : "&hostId=" + E(hostId)), null, cb);
        public void SendMessage(string id, string text, Action<JToken> cb) => SendMessage(id, text, null, cb);
        /// extra: the composer's choices for this and the next turns (model, reasoningLevel, permissionMode).
        public void SendMessage(string id, string text, JObject extra, Action<JToken> cb) => SendMessage(id, text, extra, null, cb);

        /// A message with attachments: MQH's localImage / localFile inputs after the text (paths in the Linux, where
        /// MQH and the agents run, so nothing is uploaded).
        public void SendMessage(string id, string text, JObject extra, JArray attachments, Action<JToken> cb) =>
            Call(HttpMethod.Post, $"/api/v1/threads/{E(id)}/send", SendBody(text, extra, attachments), cb);

        public static JObject SendBody(string text, JObject extra, JArray attachments)
        {
            var input = new JArray { TextInput(text) };
            if (attachments != null) foreach (var a in attachments) input.Add(a);
            var body = new JObject { ["input"] = input, ["mode"] = "auto" };
            if (extra != null) foreach (var p in extra.Properties()) body[p.Name] = p.Value;
            return body;
        }
        /// The model, effort and access the thread's next turn uses (its last ones, or the defaults).
        // workspaces (docs/workspaces.md): the environments (checkouts, worktrees) and what waits in a thread's queue
        public void Environments(string projectId, Action<JToken> cb) =>
            Call(HttpMethod.Get, "/api/v1/environments?limit=200" + (string.IsNullOrEmpty(projectId) ? "" : "&projectId=" + E(projectId)), null, cb);
        public void ArchiveEnvironmentThreads(string environmentId, Action<JToken> cb) => Call(HttpMethod.Post, $"/api/v1/environments/{E(environmentId)}/archive-threads", null, cb);
        public void DeleteEnvironment(string environmentId, Action<JToken> cb) => Call(HttpMethod.Delete, $"/api/v1/environments/{E(environmentId)}", null, cb);
        public void QueuedMessages(string threadId, Action<JToken> cb) => Call(HttpMethod.Get, $"/api/v1/threads/{E(threadId)}/queued-messages", null, cb);
        /// The user's Send now on a queued row: it skips the plugins' holds (MQH's workspace guard, the limits).
        public void SendQueuedNow(string threadId, string queuedId, Action<JToken> cb) =>
            Call(HttpMethod.Post, $"/api/v1/threads/{E(threadId)}/queued-messages/{E(queuedId)}/send", new JObject { ["mode"] = "auto" }, cb);
        public void DeleteQueuedMessage(string threadId, string queuedId, Action<JToken> cb) =>
            Call(HttpMethod.Delete, $"/api/v1/threads/{E(threadId)}/queued-messages/{E(queuedId)}", null, cb);
        public void ExecOptions(string id, Action<JToken> cb) => Call(HttpMethod.Get, $"/api/v1/threads/{E(id)}/default-execution-options", null, cb);
        public void ForkThread(JObject body, Action<JToken> cb) => Call(HttpMethod.Post, "/api/v1/threads/fork", body, cb);
        public void UpdateThread(string id, JObject patch, Action<JToken> cb) => Call(new HttpMethod("PATCH"), $"/api/v1/threads/{E(id)}", patch, cb);
        public void CompactThread(string id, Action<JToken> cb) => Call(HttpMethod.Post, $"/api/v1/threads/{E(id)}/compact", null, cb);
        public void StopThread(string id, Action<JToken> cb) => Call(HttpMethod.Post, $"/api/v1/threads/{E(id)}/stop", new JObject(), cb);
        public void Interactions(string id, Action<JToken> cb) => Call(HttpMethod.Get, $"/api/v1/threads/{E(id)}/interactions", null, cb);
        /// decision: allow_once | allow_for_session | deny
        public void ResolveApproval(string threadId, string interactionId, string decision, Action<JToken> cb) =>
            Call(HttpMethod.Post, $"/api/v1/threads/{E(threadId)}/interactions/{E(interactionId)}/resolve",
                decision == "deny" ? new JObject { ["decision"] = "deny" } : new JObject { ["decision"] = decision, ["grantedPermissions"] = null }, cb);
        public static JObject TextInput(string text) => new JObject { ["type"] = "text", ["text"] = text ?? "", ["mentions"] = new JArray() };

        // ---- secrets for agents: MQH's machine environment (values are write-only through the API and stored
        // encrypted; with MQH_MACHINE_ENV_KEY_COMMAND the key comes from the app's Android Keystore vault)
        public void MachineEnvironment(Action<JToken> cb) => Call(HttpMethod.Get, "/api/v1/settings/machine-environment", null, cb);
        public void SetMachineEnvironment(string name, string value, string note, Action<JToken> cb) =>
            Call(HttpMethod.Post, "/api/v1/settings/machine-environment", new JObject { ["name"] = name, ["value"] = value, ["note"] = note }, cb);
        public void DeleteMachineEnvironment(string name, Action<JToken> cb) =>
            Call(HttpMethod.Delete, "/api/v1/settings/machine-environment", new JObject { ["name"] = name }, cb);

        /// Like Call, but any 2xx reply counts (text streams); the callback gets the text as a JValue.
        public void CallRaw(HttpMethod m, string path, JToken body, Action<JToken> onMain, int timeoutSeconds = 30)
        {
            System.Threading.Interlocked.Increment(ref _inFlight);
            Task.Run(async () =>
            {
                JToken r = null;
                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    using (var res = await LongHttp.SendAsync(Req(m, path, body), cts.Token).ConfigureAwait(false))
                    {
                        string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!res.IsSuccessStatusCode) throw new Exception($"{(int)res.StatusCode} {Truncate(text)}");
                        Online = true; LastError = null;
                        r = new JValue(text);
                    }
                }
                catch (Exception ex) { LastError = ex.Message; }
                _main.Enqueue(() => onMain?.Invoke(r));
                System.Threading.Interlocked.Decrement(ref _inFlight);
            });
        }

        private static readonly HttpClient LongHttp = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        /// Writes quest.settings, retrying once with the server's revision when ours is stale.
        public void PushQuestSettings(JObject value, Action<bool> cb)
        {
            Preferences(prefs =>
            {
                int rev = prefs?["preferences"]?["quest.settings"]?["revision"]?.Value<int>() ?? 0;
                Call(new HttpMethod("PUT"), "/api/v1/preferences/ui/quest.settings", new JObject { ["expectedRevision"] = rev, ["value"] = value }, r => cb?.Invoke(r != null));
            });
        }
    }

    public sealed class GitEntry { public string Code, Path; }
    public sealed class GitBranch { public string Name, Upstream, Track, When; public bool Current; }
    public sealed class GitCommit { public string Hash, Refs, Subject, When; public string[] Parents; public int Lane; public int Lanes; }

    public sealed class GitStatus
    {
        public string Repo, Branch = "", Upstream = "", Oid = "";
        public int Ahead, Behind;
        public readonly List<GitEntry> Entries = new List<GitEntry>();
        public readonly List<GitBranch> Branches = new List<GitBranch>();
        public readonly List<GitCommit> Log = new List<GitCommit>();
        public string Error;
    }

    /// Git for the Git panel: on the headset `git` runs in the Debian userland (proot); in the Editor it runs
    /// through WSL. Read-only queries plus a few safe actions (fetch, pull --ff-only, commit -a, checkout).
    public sealed class GitService
    {
        private readonly LinuxHost _host;
        public GitService(LinuxHost host) { _host = host; }

        public bool InUserland => _host != null && _host.State == LinuxHost.HostState.Ready;

        /// Runs `git <args>` in a repo (Linux path) and returns (exit code, output).
        public Task<(int code, string output)> Git(string repo, string args, int timeoutMs = 20000) =>
            Shell($"git -c color.ui=never -c core.quotepath=off -C {Quote(repo)} {args}", timeoutMs);

        public static string Quote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

        public Task<(int code, string output)> Shell(string command, int timeoutMs = 20000)
        {
            if (InUserland) return RunUserland(command, timeoutMs);
            return Task.Run(() => RunWsl(command, timeoutMs));
        }

        private async Task<(int, string)> RunUserland(string command, int timeoutMs)
        {
            PtySession s;
            try { s = _host.StartShell(24, 240, command); }
            catch (Exception ex) { return (-1, ex.Message); }
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            using (s)
            {
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    while (s.TryRead(out var chunk)) sb.Append(Encoding.UTF8.GetString(chunk));
                    if (!s.IsRunning) { while (s.TryRead(out var chunk)) sb.Append(Encoding.UTF8.GetString(chunk)); break; }
                    await Task.Delay(15).ConfigureAwait(false);
                }
                return (s.IsRunning ? -2 : s.ExitCode, Clean(sb.ToString()));
            }
        }

        private static (int, string) RunWsl(string command, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo("wsl.exe", "-e bash -lc " + QuoteWin(command))
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return (-2, "timeout"); }
                    return (p.ExitCode, Clean(outTask.Result + errTask.Result));
                }
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        private static string QuoteWin(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07|\r", RegexOptions.Compiled);
        public static string Clean(string s) => Ansi.Replace(s ?? "", "");

        /// Windows path of this machine -> the same file seen from WSL (Editor only).
        public static string ToLinuxPath(string winPath)
        {
            var m = Regex.Match(winPath ?? "", @"^([A-Za-z]):[\\/](.*)$");
            return m.Success ? "/mnt/" + char.ToLowerInvariant(m.Groups[1].Value[0]) + "/" + m.Groups[2].Value.Replace('\\', '/') : (winPath ?? "").Replace('\\', '/');
        }

        public async Task<List<string>> FindRepos(string root, int depth = 3)
        {
            var (code, output) = await Shell($"find {Quote(root)} -maxdepth {depth + 1} -name .git -prune -print 2>/dev/null | head -40", 15000).ConfigureAwait(false);
            var list = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var l = line.Trim();
                if (l.EndsWith("/.git")) list.Add(l.Substring(0, l.Length - 5));
            }
            return list;
        }

        public async Task<GitStatus> Status(string repo)
        {
            var st = new GitStatus { Repo = repo };
            var (code, output) = await Git(repo, "status --porcelain=v2 --branch").ConfigureAwait(false);
            if (code != 0) { st.Error = output.Trim(); return st; }
            ParseStatus(output, st);
            var br = await Git(repo, "for-each-ref --sort=-committerdate --format='%(HEAD)%09%(refname:short)%09%(upstream:short)%09%(upstream:track)%09%(committerdate:relative)' refs/heads").ConfigureAwait(false);
            foreach (var line in br.output.Split('\n'))
            {
                var f = line.Split('\t');
                if (f.Length < 5) continue;
                st.Branches.Add(new GitBranch { Current = f[0].Trim() == "*", Name = f[1], Upstream = f[2], Track = f[3], When = f[4] });
            }
            var log = await Git(repo, "log --all --date-order -n 18 --format='%h%x09%p%x09%D%x09%s%x09%cr'").ConfigureAwait(false);
            foreach (var line in log.output.Split('\n'))
            {
                var f = line.Split('\t');
                if (f.Length < 5) continue;
                st.Log.Add(new GitCommit { Hash = f[0], Parents = f[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), Refs = f[2], Subject = f[3], When = f[4] });
            }
            AssignLanes(st.Log);
            return st;
        }

        public static void ParseStatus(string output, GitStatus st)
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("# branch.head ")) st.Branch = line.Substring(14);
                else if (line.StartsWith("# branch.oid ")) st.Oid = line.Substring(13);
                else if (line.StartsWith("# branch.upstream ")) st.Upstream = line.Substring(18);
                else if (line.StartsWith("# branch.ab "))
                {
                    var m = Regex.Match(line, @"\+(\d+) -(\d+)");
                    if (m.Success) { st.Ahead = int.Parse(m.Groups[1].Value); st.Behind = int.Parse(m.Groups[2].Value); }
                }
                else if (line.StartsWith("1 ") || line.StartsWith("2 "))
                {
                    var parts = line.Split(' ');
                    string xy = parts.Length > 1 ? parts[1] : "..";
                    int pathStart = line.StartsWith("1 ") ? 8 : 9;
                    var path = string.Join(" ", parts.Skip(pathStart));
                    if (line.StartsWith("2 ")) path = path.Split('\t')[0];
                    st.Entries.Add(new GitEntry { Code = xy.Replace(".", " ").Trim(), Path = path });
                }
                else if (line.StartsWith("? ")) st.Entries.Add(new GitEntry { Code = "??", Path = line.Substring(2) });
                else if (line.StartsWith("u ")) st.Entries.Add(new GitEntry { Code = "UU", Path = line.Split(' ').Last() });
            }
        }

        /// Simple lane assignment for a commit graph drawn top (newest) to bottom.
        public static void AssignLanes(List<GitCommit> log)
        {
            var lanes = new List<string>(); // which commit each lane is waiting for
            int maxLanes = 1;
            foreach (var c in log)
            {
                int lane = lanes.IndexOf(c.Hash);
                if (lane < 0) { lane = lanes.IndexOf(null); if (lane < 0) { lanes.Add(null); lane = lanes.Count - 1; } }
                for (int i = 0; i < lanes.Count; i++) if (i != lane && lanes[i] == c.Hash) lanes[i] = null; // merges into this lane
                c.Lane = lane;
                lanes[lane] = c.Parents.Length > 0 ? c.Parents[0] : null;
                for (int p = 1; p < c.Parents.Length; p++)
                {
                    if (lanes.Contains(c.Parents[p])) continue;
                    int free = lanes.IndexOf(null);
                    if (free < 0) { lanes.Add(c.Parents[p]); } else lanes[free] = c.Parents[p];
                }
                maxLanes = Mathf.Max(maxLanes, lanes.Count);
            }
            foreach (var c in log) c.Lanes = maxLanes;
        }
    }

    public sealed class FileEntry { public string Name, LinuxPath; public bool IsDir, IsLink; public long Size; public DateTime Modified; }

    /// The Linux userland's files for the Files panel. On the headset paths map into the rootfs (and
    /// /mnt/quest to the shared folder); in the Editor "/" is a chosen folder of this PC.
    public sealed class FileService
    {
        private readonly LinuxHost _host;
        public string EditorRoot;
        public FileService(LinuxHost host, string editorRoot) { _host = host; EditorRoot = editorRoot; }

        public string HostPath(string linuxPath)
        {
            linuxPath = Normalize(linuxPath);
            bool device = _host != null && _host.State == LinuxHost.HostState.Ready;
            if (device)
            {
                if (linuxPath == "/mnt/quest" || linuxPath.StartsWith("/mnt/quest/")) return _host.SharedDir + linuxPath.Substring(10);
                return _host.RootfsDir + linuxPath;
            }
            var drive = System.Text.RegularExpressions.Regex.Match(linuxPath, "^/mnt/([a-z])(/.*)?$");
            if (drive.Success && Application.platform == RuntimePlatform.WindowsEditor) return drive.Groups[1].Value.ToUpperInvariant() + ":" + (drive.Groups[2].Success ? drive.Groups[2].Value : "/");
            return Path.Combine(EditorRoot ?? Application.dataPath, linuxPath.TrimStart('/'));
        }

        public static string Normalize(string p)
        {
            var parts = new List<string>();
            foreach (var s in (p ?? "/").Replace('\\', '/').Split('/'))
            {
                if (s == "" || s == ".") continue;
                if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(s);
            }
            return "/" + string.Join("/", parts);
        }

        public static string Parent(string p) { p = Normalize(p); int i = p.LastIndexOf('/'); return i <= 0 ? "/" : p.Substring(0, i); }

        public List<FileEntry> List(string linuxPath, out string error)
        {
            error = null;
            var list = new List<FileEntry>();
            var dir = HostPath(linuxPath);
            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var fsi in di.EnumerateFileSystemInfos())
                {
                    bool link = (fsi.Attributes & FileAttributes.ReparsePoint) != 0;
                    bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                    list.Add(new FileEntry
                    {
                        Name = fsi.Name,
                        LinuxPath = Normalize(linuxPath + "/" + fsi.Name),
                        IsDir = isDir,
                        IsLink = link,
                        Size = !isDir && fsi is FileInfo fi ? fi.Length : 0,
                        Modified = fsi.LastWriteTime,
                    });
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return list.OrderBy(e => e.IsDir ? 0 : 1).ThenBy(e => e.Name.StartsWith(".") ? 1 : 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string Size(long b) => b < 1024 ? b + " B" : b < 1 << 20 ? (b / 1024f).ToString("0.#") + " KB" : b < 1 << 30 ? (b / 1048576f).ToString("0.#") + " MB" : (b / 1073741824f).ToString("0.##") + " GB";
    }
}
