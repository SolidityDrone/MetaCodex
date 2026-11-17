using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using IsKey = UnityEngine.InputSystem.Key;
using IsKeyboard = UnityEngine.InputSystem.Keyboard;

namespace MetaCodex.Shell
{
    /// WEB: the in-app browser window (owner 2026-11-10: "it's boring to have the system browser turn the app off";
    /// Horizon OS cannot show its browser beside an immersive app: even its Web Tasks send the app to the background).
    /// On the headset the page is Android's own WebView (MetaCodexWebView.java): the content of a hardware-accelerated
    /// window on a private virtual display that renders into an ImageReader (video plays, the page animates at the
    /// display's rate); each new frame is copied into one buffer (60 a second at most, nothing while the page is still),
    /// uploaded here into a texture on a flat quad (the window never bends: ShellLayout.StaysFlat, so the page and the
    /// pointer agree). If that display cannot be made, the old path draws the page off screen into the same buffer.
    /// A ray's pinch or a fingertip is a touch, a drag scrolls (a quick one flings), the pointer over the page hovers,
    /// ⌨ types into the page's focused field, ⎘ pastes the clipboard, ⏎ and ⌫ are the keys, - and + zoom. After a touch
    /// on the page a hardware keyboard types into it (all keys, with Ctrl / Shift / Alt / Meta) until a terminal or a
    /// text field takes the keys. On pages of this device (localhost: code editors) a drag scrolls with the wheel.
    /// OpenBrowser(url, large) opens it 1.25 times larger with a 1600 x 1000 page (more of the page, as sharp). ⟳ is ✕ (stop) while a page loads, a
    /// line under the toolbar shows how far it is; a page that fails shows why, ⟳ tries again. A page's full screen
    /// (video) fills the page area and EXIT FULL SCREEN takes the address's place. Page dialogs (alert, confirm) open
    /// as the app's dialogs; downloads, file pickers, other apps' links and camera, microphone or location requests
    /// are refused with a toast. Every page the app opens (GO, bookmarks, local servers, terminal links, voice, Lua
    /// browser.open) opens here; ⇗ sends the page to Horizon OS's browser (sign-in pages that refuse embedded browsers).
    /// A page's dropdown (<select>) and its date, time and colour pickers open as the app's chooser over the page (the
    /// engine's own ones are dialogs the headset never shows): see "a page's dropdowns and pickers" below.
    /// Remote debugging is on: chrome://inspect on the PC (adb) shows the page's console. In the Editor there is no
    /// WebView: the window works, the page area says so.
    public sealed partial class MetaCodexShell
    {
        public const int WebW = 1280, WebH = 800, WebLargeW = 1600, WebLargeH = 1000;
        public const float WebLargeScale = 1.25f;
        private static readonly Color32 WebDarkFill = new Color32(6, 16, 28, 255);
        private UiPanel _web;
        private UiTextField _webUrl;
        private UiLabel _webTitle, _webNote;
        private UiButton _webBack, _webFwd, _webReload, _webGo, _webZoomOut, _webZoomIn, _webExitFull;
        private int _webPageW = WebW, _webPageH = WebH; // the page's size now (WebW x WebH, or the large one)
        private bool _webLarge;
        private UiProgress _webProgress;
        private WebPage _webPage;
        private Texture2D _webTex;
        private Mesh _webQuad;
        private string _webCurrent = "", _webError = "", _webMode = "", _webTitleShown, _webNoteShown;
        private bool _webOnStart, _webLoading, _webFull, _webDark, _webReadState;
        private int _webProgressShown = -1, _webJsShown, _webJsAnswered, _webJsDismissed;
        private (int id, bool ok) _webLastJsAnswer;
        private float _webNextState;
        private Vector2 _webLastHover = new Vector2(-1f, -1f);
        private (int action, Vector2 page) _webLastTouch = (-1, default);
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _wv;
        private Unity.Collections.NativeArray<byte> _wvPixels; // a view of the Java buffer (no copy, no unsafe code)
        // the calls made every frame (poll, endRead) or per pointer move (touch, hover): raw JNI with the method ids
        // looked up once and one argument array, so they allocate nothing (AndroidJavaObject.Call builds a signature
        // string and boxes its arguments on every call)
        private IntPtr _wvPoll, _wvEndRead, _wvTouch, _wvHover, _wvKeyMeta;
        private readonly jvalue[] _wvArgs = new jvalue[3];
        private static readonly jvalue[] WvNoArgs = new jvalue[0];
#endif

        /// The page area: a pointer on it, in page pixels (top-left origin), goes to the page.
        private sealed class WebPage : UiElement
        {
            private readonly MetaCodexShell _shell;
            public override bool Interactive => true;
            public WebPage(MetaCodexShell shell) { _shell = shell; }
            protected override void Build() { }
            public Vector2 ToPage(Vector2 p) => new Vector2(Mathf.Clamp01((p.x - Rect.x) / Rect.width) * _shell._webPageW, Mathf.Clamp01((p.y - Rect.y) / Rect.height) * _shell._webPageH);
            public override void OnPress(UiPointerEvent e) => _shell.WebTouch(0, ToPage(e.Point));
            public override void OnDrag(UiPointerEvent e) => _shell.WebTouch(2, ToPage(e.Point));
            public override void OnRelease(UiPointerEvent e, bool inside) => _shell.WebTouch(1, ToPage(e.Point));
            public override void OnHover(UiPointerEvent e) => _shell.WebHover(ToPage(e.Point));
        }

        private void BuildWeb()
        {
            WebChoiceEnd(true); // built again (accent colour): an open chooser goes, the page keeps its value
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("web", "WEB", 0.80f, 0.61f);
            _web = p;
            var c = p.Content;
            float bh = t.ButtonHeight, sq = 0.036f, x = c.x;
            UiButton B(string text, Action act, float w, UiButtonStyle style = UiButtonStyle.Normal)
            {
                var b = p.Add(new UiButton(text, act, style) { Rect = new Rect(x, c.y, w, bh) });
                x += w + t.Gap;
                return b;
            }
            _webBack = B("◀", () => WebCall("back"), sq);       // in full screen: leaves it
            _webFwd = B("▶", () => WebCall("forward"), sq);
            _webReload = B("⟳", () => WebCall(_webLoading ? "stop" : "reload"), sq); // ✕ while a page loads: stops it
            float right = 0.056f + (sq + t.Gap) * 8f; // GO, then ⌨ ⎘ ⏎ ⌫ - + ☰ ⇗
            float urlX = x;
            _webUrl = p.Add(new UiTextField("", "", u => OpenBrowser(u), "an address, or words to search") { Rect = new Rect(urlX, c.y, c.xMax - right - urlX - t.Gap, bh) });
            x = c.xMax - right;
            _webGo = B("GO", () => OpenBrowser(_webUrl.Text), 0.056f, UiButtonStyle.Primary);
            // a page in full screen: one button where the address and GO are
            _webExitFull = p.Add(new UiButton("EXIT FULL SCREEN", () => WebCall("exitFullscreen"), UiButtonStyle.Primary) { Rect = new Rect(urlX, c.y, c.xMax - right + 0.056f - urlX, bh) });
            _webExitFull.SetVisible(false);
            B("⌨", WebType, sq);
            B("⎘", WebPaste, sq);          // the clipboard (terminals and CLIP fill it) into the page's focused field
            B("⏎", () => WebKey(66), sq);  // KEYCODE_ENTER
            B("⌫", () => WebKey(67), sq);  // KEYCODE_DEL
            _webZoomOut = B("-", () => WebCallInt("zoom", -1), sq);
            _webZoomIn = B("+", () => WebCallInt("zoom", 1), sq);
            B("☰", () => OpenPanel("links"), sq); // bookmarks and local servers, beside this window
            B("⇗", () => OpenSystemBrowser(_webCurrent), sq);
            // how far the page has loaded: a line under the toolbar, only while it loads
            _webProgress = p.Add(new UiProgress(0f) { Rect = new Rect(c.x, c.y + bh + 0.0008f, c.width, 0.0022f) });
            _webProgress.SetVisible(false);
            _webProgressShown = -1;
            float titleY = c.y + bh + t.Gap * 0.5f, top = titleY + 0.016f;
            _webTitle = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, titleY, c.width, 0.016f), Color = t.TextDim });
            _webTitleShown = null;
            float pw = c.width, ph = pw * WebH / WebW;
            if (top + ph > c.yMax) { ph = c.yMax - top; pw = ph * WebW / WebH; }
            var area = new Rect(c.x + (c.width - pw) * 0.5f, top, pw, ph);
            _webPage = p.Add(new WebPage(this) { Rect = area });
            if (_webTex == null) // kept when the panels are rebuilt (accent colour)
            {
                _webTex = new Texture2D(_webPageW, _webPageH, TextureFormat.RGBA32, false, false) { name = "web page", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                WebFillDark();
            }
            AddWebQuad(p, area);
            // the Editor's hint, a fatal error, or the page that did not open (its text over the darkened page)
            _webNote = p.Add(new UiLabel("", t.BodySize) { Rect = new Rect(area.x + 0.03f, area.y + area.height * 0.25f, area.width - 0.06f, area.height * 0.5f), Align = TextAlignmentOptions.Center, Color = t.TextDim, Wrap = true });
            _webNoteShown = null;
            _webFull = false;
            _webLoading = false;
            _webReadState = true; // a rebuilt window (accent colour) takes the page's state at once
            WebRefreshNote();
            p.Tick += _ => TickWeb();
            p.Opened += _ =>
            {
                WebEnsure();
                WebCallBool("setPaused", false);
                if (string.IsNullOrEmpty(_webCurrent)) { WebStartPage(); ProbeLocalServers(); }
            };
            p.Closed += _ => { WebCallBool("setPaused", true); WebTakeKeys(false); WebChoiceEnd(true); }; // the page's media pause; no drawing, no copying while closed
            WebApplyScale();
        }

        /// The page's quad, 1 mm in front of the window, its rows top first (the WebView's order).
        private void AddWebQuad(UiPanel p, Rect area)
        {
            if (_webQuad == null)
            {
                _webQuad = new Mesh { name = "web page" };
                _webQuad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
                _webQuad.uv = new[] { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f) };
                _webQuad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                _webQuad.RecalculateBounds();
            }
            var go = new GameObject("web page", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(p.transform, false);
            go.transform.localPosition = p.PanelToLocal(area.center, -0.0008f);
            go.transform.localScale = new Vector3(area.width, area.height, 1f);
            go.GetComponent<MeshFilter>().sharedMesh = _webQuad;
            var mr = go.GetComponent<MeshRenderer>();
            var sh = Resources.Load<Shader>("Shaders/ViewImage") ?? Shader.Find("MetaCodex/ViewImage");
            if (sh != null) mr.sharedMaterial = new Material(sh) { name = "web page" };
            var mpb = new MaterialPropertyBlock();
            mpb.SetTexture("_MainTex", _webTex);
            mr.SetPropertyBlock(mpb);
            p.TagRenderer(mr, Vector3.zero); // the window's slot: the page is cast with it
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        /// The page area in the window's dark colour (at start, and under a page that did not open). Writes into the
        /// texture's own memory: no array.
        private void WebFillDark()
        {
            if (_webTex == null) return;
            var px = _webTex.GetPixelData<Color32>(0);
            for (int i = 0; i < px.Length; i++) px[i] = WebDarkFill;
            _webTex.Apply(false, false);
        }

        /// What a typed address means: a scheme is kept; localhost, a LAN IP or a host with a port is http; a word with
        /// a dot is https; anything else is a search.
        public static string WebAddress(string typed)
        {
            string u = (typed ?? "").Trim();
            if (u.Length == 0 || u == "https://" || u == "http://") return "";
            if (u.Contains("://") || u.StartsWith("about:") || u.StartsWith("data:") || u.StartsWith("file:")) return u;
            if (u.Contains(" ")) return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(u);
            string host = u.Split('/')[0];
            bool local = host.StartsWith("localhost") || host.StartsWith("127.") || host.StartsWith("10.") || host.StartsWith("192.168.") || host.StartsWith("[::1]");
            if (local || host.Contains(":")) return "http://" + u;
            if (host.Contains(".")) return "https://" + u;
            return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(u);
        }

        /// Sign-in pages never open in the WEB window: its WebView keeps cookies, so an account's web session would
        /// stay inside MetaCodex (the app never handles or keeps sign-in credentials; the agent CLIs sign in through
        /// Horizon OS's browser, which shares the loopback with the userland, and keep their own tokens). Google also
        /// refuses sign-in inside embedded browsers. A host here matches itself and its subdomains.
        public static readonly string[] SignInHosts =
        {
            "claude.ai", "claude.com", "anthropic.com", "openai.com", "chatgpt.com", "accounts.google.com", "login.microsoftonline.com",
            "login.live.com", "appleid.apple.com", "auth0.com", "okta.com", "huggingface.co", "x.ai", "mistral.ai", "z.ai", "moonshot.ai", "qwen.ai",
        };
        /// Path parts of sign-in pages on any other site (github.com/login, …/oauth/authorize).
        public static readonly string[] SignInPaths = { "/login", "/signin", "/sign-in", "/sign_in", "/oauth", "/authorize", "/sso" };

        public static bool IsSignInPage(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return false;
            string host = uri.Host.ToLowerInvariant(), path = uri.AbsolutePath.ToLowerInvariant();
            if (host == "localhost" || host.StartsWith("127.") || host == "[::1]") return false; // dev servers and the CLIs' own callbacks
            foreach (var h in SignInHosts) if (host == h || host.EndsWith("." + h)) return true;
            foreach (var p in SignInPaths) if (path.Contains(p)) return true;
            return false;
        }

        /// Opens a page in the WEB window (every page the app opens comes here; sign-in pages go to the system browser).
        public void OpenBrowser(string url)
        {
            string u = WebAddress(url);
            if (IsSignInPage(u)) { OpenSystemBrowser(u); return; }
            OpenPanel("web");
            if (u.Length == 0) return;
            _webCurrent = u;
            _webOnStart = false;
            _webUrl?.Set(u);
            if (!WebEnsure()) { OpenSystemBrowser(u); return; } // no WebView on this device: the system's browser after all
            WebCall("load", u);
        }

        /// Opens a page in the WEB window at a size: large is 1.25 times the window with a 1600 x 1000 page (code editors:
        /// more lines and columns, the text as sharp); not large is the usual 1280 x 800. OpenBrowser(url) keeps the size.
        public void OpenBrowser(string url, bool large)
        {
            WebSetLarge(large);
            OpenBrowser(url);
            WebApplyScale(); // after opening: the window has its large scale at once
        }

        /// The window's size (large or not): the page's pixels follow (the WebView gets a display of that size, the
        /// texture is made again), the window's scale is the UI scale times 1.25 while large.
        public void WebSetLarge(bool large)
        {
            bool was = _webLarge;
            _webLarge = large;
            int w = large ? WebLargeW : WebW, h = large ? WebLargeH : WebH;
            if (w != _webPageW || h != _webPageH)
            {
                _webPageW = w;
                _webPageH = h;
                if (_webTex != null) { _webTex.Reinitialize(w, h); WebFillDark(); }
#if UNITY_ANDROID && !UNITY_EDITOR
                if (_wv != null) try { _wv.Call("resize", w, h); } catch (Exception ex) { Log("web resize: " + ex.Message); }
#endif
            }
            if (_web != null && was && !large) _web.BaseScale = Vector3.one * UiScaleNow;
            WebApplyScale();
        }

        /// While large, the window keeps 1.25 times the UI scale (the shell sets every window back to the UI scale when
        /// it changes). Not large: the window's scale is left alone.
        private void WebApplyScale()
        {
            if (!_webLarge || _web == null) return;
            float s = UiScaleNow * WebLargeScale;
            if (Mathf.Abs(_web.BaseScale.x - s) > 1e-4f) _web.BaseScale = Vector3.one * s;
        }

        /// The page in Horizon OS's browser (the app goes to the background while it shows).
        public void OpenSystemBrowser(string url)
        {
            string u = WebAddress(string.IsNullOrEmpty(url) ? _webCurrent : url);
            if (u.Length == 0) { Toast("No page open", "warn"); return; }
            bool ok = SystemBridge.OpenBrowser(u);
            Toast(ok ? "Opening " + u + " in the system browser" : SystemBridge.LastResult, ok ? "info" : "warn");
        }

        /// The start page: bookmarks and the local servers found, as big links.
        private void WebStartPage()
        {
            _webOnStart = true;
            var sb = new StringBuilder();
            sb.Append("<html><head><meta name=viewport content='width=device-width'><style>body{background:#06101c;color:#cfeaff;font-family:sans-serif;margin:40px 56px}" +
                      "h1{color:#8febff;letter-spacing:4px;font-size:30px}h2{color:#9dbdd2;font-size:20px;margin-top:34px}a{display:block;padding:18px 24px;margin:10px 0;" +
                      "background:#0d2236;border:1px solid #2a9fd6;border-radius:6px;color:#e2f4ff;font-size:24px;text-decoration:none}</style></head><body><h1>METACODEX · WEB</h1>");
            sb.Append("<h2>BOOKMARKS</h2>");
            foreach (var b in Settings.bookmarks) sb.Append("<a href=\"").Append(Escape(WebAddress(b))).Append("\">").Append(Escape(b)).Append("</a>");
            var local = _localServers?.Items?.Select(i => i.Tag as string).Where(s => !string.IsNullOrEmpty(s)).ToList();
            sb.Append("<h2>LOCAL SERVERS</h2>");
            if (local == null || local.Count == 0) sb.Append("<p>None found on the usual dev ports (3000, 5173, 8080…).</p>");
            else foreach (var l in local) sb.Append("<a href=\"").Append(Escape(l)).Append("\">").Append(Escape(l)).Append("</a>");
            sb.Append("</body></html>");
            _webTitle?.Set("start page");
            _webTitleShown = "start page";
            _webUrl?.Set("");
            WebCall("loadHtml", sb.ToString(), "about:blank");
        }

        private static string Escape(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// ⌨: what is typed on the system keyboard goes into the page's focused field (tap the field first).
        private void WebType() => UiKeyboard.Edit("", false, null, text => { if (!string.IsNullOrEmpty(text)) WebCall("text", text); }, "Type into the page");

        /// ⎘ (and Ctrl+V on a hardware keyboard): the clipboard into the page, as a paste the page can take (code
        /// editors) or typed into its focused field.
        private void WebPaste()
        {
            string clip = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(clip)) { Toast("The clipboard is empty", "info"); return; }
            _webLastPaste = clip;
            WebCall("paste", clip);
        }

        private void WebKey(int code) => WebCallInt("key", code);

        private void WebTouch(int action, Vector2 page)
        {
            // a press on the page while a chooser is open closes it (the page keeps its value), as a tap beside a
            // dropdown does; that touch goes nowhere, up to its release
            if (action == 0) { _webSwallow = _wcPanel != null; if (_webSwallow) { WebChoiceEnd(true); return; } }
            else if (_webSwallow) { if (action == 1) _webSwallow = false; return; }
            _webLastTouch = (action, page);
            if (action == 0) WebTakeKeys(true); // a hardware keyboard types into the page now
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv == null || _wvTouch == IntPtr.Zero) return;
            _wvArgs[0].i = action;
            _wvArgs[1].f = page.x;
            _wvArgs[2].f = page.y;
            AndroidJNI.CallVoidMethod(_wv.GetRawObject(), _wvTouch, _wvArgs);
            WebJniOk();
#endif
        }

        private void WebHover(Vector2 page)
        {
            if ((page - _webLastHover).sqrMagnitude < 4f) return; // only when it moved two pixels
            _webLastHover = page;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv == null || _wvHover == IntPtr.Zero) return;
            _wvArgs[0].f = page.x;
            _wvArgs[1].f = page.y;
            AndroidJNI.CallVoidMethod(_wv.GetRawObject(), _wvHover, _wvArgs);
            WebJniOk();
#endif
        }

        /// The WebView on the headset, made the first time the window opens (false where there is none: a failure).
        private bool WebEnsure()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_webError.StartsWith("webview:")) return false; // it failed once: the system browser from now on
            if (_wv != null) return true;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    _wv = new AndroidJavaObject("com.pnncl.metacodex.web.MetaCodexWebView", activity, _webPageW, _webPageH);
                _wv.Call("setExternal", string.Join("\n", SignInHosts), string.Join("\n", SignInPaths));
                using (var buf = _wv.Call<AndroidJavaObject>("pixels")) _wvPixels = AndroidJNI.GetDirectByteBuffer(buf.GetRawObject());
                _wvPoll = WebMethod("poll", "()I");
                _wvEndRead = WebMethod("endRead", "()V");
                _wvTouch = WebMethod("touch", "(IFF)V");
                _wvHover = WebMethod("hover", "(FF)V");
                _wvKeyMeta = WebMethod("keyMeta", "(II)V");
                return true;
            }
            catch (Exception ex) { Log("web: " + ex.Message); _webError = "webview: " + ex.Message; _wv = null; WebRefreshNote(); return false; }
#else
            return true; // the Editor: the window works without a page
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// A Java exception left by a raw JNI call is cleared (the next JNI call would fail otherwise).
        private static bool WebJniOk()
        {
            IntPtr ex = AndroidJNI.ExceptionOccurred();
            if (ex == IntPtr.Zero) return true;
            AndroidJNI.ExceptionClear();
            AndroidJNI.DeleteLocalRef(ex);
            return false;
        }

        private IntPtr WebMethod(string name, string signature)
        {
            IntPtr id = AndroidJNI.GetMethodID(_wv.GetRawClass(), name, signature);
            if (WebJniOk() && id != IntPtr.Zero) return id;
            Log("web: no method " + name + signature);
            return IntPtr.Zero;
        }
#endif

        private void WebCall(string method, params object[] args)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv == null && !WebEnsure()) return;
            try { _wv.Call(method, args); } catch (Exception ex) { Log("web " + method + ": " + ex.Message); }
#endif
        }

        private void WebCallBool(string method, bool v)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv != null) try { _wv.Call(method, v); } catch (Exception) { }
#endif
        }

        private void WebCallInt(string method, int v)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv != null) try { _wv.Call(method, v); } catch (Exception) { }
#endif
        }

        /// While the window is open, each frame: a new frame of the page into the texture (only when there is one; never
        /// waits: while the copy thread writes, the frame comes next time; nothing allocated), the toolbar from the page's
        /// state when it changed (four times a second at most).
        private void TickWeb()
        {
            WebDialogTick();
            WebKeysTick();
            WebApplyScale();
            if (_wcPanel != null) WebChoiceFollow();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv == null || _webTex == null || _wvPoll == IntPtr.Zero) return;
            IntPtr obj = _wv.GetRawObject();
            int bits = AndroidJNI.CallIntMethod(obj, _wvPoll, WvNoArgs);
            if (!WebJniOk()) return;
            if ((bits & 4) != 0) { WebTakeSize(); return; } // a new page size: its buffer, the texture (once per resize)
            if ((bits & 1) != 0) // a new frame, the buffer locked until endRead
            {
                bool upload = !_webDark && _wvPixels.IsCreated && _wvPixels.Length >= _webTex.width * _webTex.height * 4;
                try { if (upload) _webTex.LoadRawTextureData(_wvPixels); }
                catch (Exception ex) { upload = false; Log("web frame: " + ex.Message); }
                finally { AndroidJNI.CallVoidMethod(obj, _wvEndRead, WvNoArgs); WebJniOk(); }
                if (upload) _webTex.Apply(false, false);
            }
            if ((bits & 8) != 0) WebChoiceTake(); // a page's dropdown or picker opened (or the page it was on is gone)
            if (((bits & 2) == 0 && !_webReadState) || Time.unscaledTime < _webNextState) return;
            _webNextState = Time.unscaledTime + 0.25f;
            _webReadState = false;
            string state;
            try { state = _wv.Call<string>("state"); } catch (Exception) { return; }
            WebApplyState(state);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// The WebView has a new page size: a view of its new buffer, the texture at that size, then the old buffer goes.
        private void WebTakeSize()
        {
            try
            {
                int w = _wv.Call<int>("width"), h = _wv.Call<int>("height");
                using (var buf = _wv.Call<AndroidJavaObject>("pixels")) _wvPixels = AndroidJNI.GetDirectByteBuffer(buf.GetRawObject());
                if (_webTex.width != w || _webTex.height != h) { _webTex.Reinitialize(w, h); WebFillDark(); }
                _wv.Call("sizeTaken");
                Log("web: page " + w + "x" + h);
            }
            catch (Exception ex) { Log("web size: " + ex.Message); }
        }
#endif

        private static string WebField(string[] st, int i) => i < st.Length ? st[i] : "";

        /// The page's state (MetaCodexWebView.state(): tab separated) onto the toolbar, the note and the dialogs. Only
        /// when it changed; labels only when their text changes.
        private void WebApplyState(string state)
        {
            var st = (state ?? "").Split('\t');
            if (st.Length < 7) return;
            string signIn = WebField(st, 7), notice = WebField(st, 8);
            if (signIn.Length > 0) // a link on the page to a sign-in page: not followed here
            {
                Toast("Sign-in pages open in the system browser: MetaCodex keeps no sign-in sessions", "info");
                OpenSystemBrowser(signIn);
            }
            if (notice.Length > 0) Toast(notice, "info"); // a download, a refused permission, another app's link
            string url = st[0];
            if (url.Length > 0 && url != _webCurrent && !url.StartsWith("data:") && url != "about:blank") { _webCurrent = url; _webOnStart = false; _webUrl?.Set(url); }
            bool loading = st[5] == "1";
            if (loading != _webLoading) { _webLoading = loading; _webReload?.SetText(loading ? "✕" : "⟳"); _webProgress?.SetVisible(loading); }
            int.TryParse(st[2], out int progress);
            if (progress != _webProgressShown) { _webProgressShown = progress; _webProgress?.Set(progress / 100f); }
            string title = (loading ? "loading · " : "") + (_webOnStart ? "start page" : st[1]);
            if (title != _webTitleShown) { _webTitleShown = title; _webTitle?.Set(title); }
            _webBack?.SetEnabled(st[3] == "1" || WebField(st, 9) == "1");
            _webFwd?.SetEnabled(st[4] == "1");
            WebFullscreen(WebField(st, 9) == "1");
            string mode = WebField(st, 10);
            if (mode.Length > 0 && mode != _webMode)
            {
                _webMode = mode;
                Log(mode == "display" ? "web: capture display (the page renders on its own virtual display)"
                    : "web: capture " + mode + " (fallback: video may stop after a few frames; logcat tag MetaCodexWeb says why)");
            }
            if (!_webError.StartsWith("webview:")) _webError = st[6];
            WebRefreshNote();
            int.TryParse(WebField(st, 12), out int jsId);
            WebDialog(WebField(st, 11), jsId, WebField(st, 13));
        }

        /// A page in full screen (a video's ⛶): EXIT FULL SCREEN where the address and GO are; ◀ leaves it too.
        private void WebFullscreen(bool on)
        {
            if (on == _webFull) return;
            _webFull = on;
            _webUrl?.SetVisible(!on);
            _webGo?.SetVisible(!on);
            _webExitFull?.SetVisible(on);
            _webZoomOut?.SetEnabled(!on);
            _webZoomIn?.SetEnabled(!on);
        }

        /// The note over the page: a page that did not open (why, its address, what to do; the page under it dark and
        /// not updated until the next page starts), a WebView that could not be made, or the Editor's hint.
        private void WebRefreshNote()
        {
            bool fatal = _webError.StartsWith("webview:");
            bool failed = !fatal && _webError.Length > 0;
            if (failed != _webDark) { _webDark = failed; if (failed) WebFillDark(); }
            string note = fatal ? "⚠ " + _webError
                : failed ? "⚠ This page did not open\n" + _webError + "\n\n" + _webCurrent + "\n\n⟳ tries again · ◀ goes back"
                : Application.isEditor ? "The page shows on the headset (Android's WebView). ⇗ opens it in your browser here." : "";
            if (note == _webNoteShown) return;
            _webNoteShown = note;
            _webNote?.Set(note);
        }

        /// The page's alert or confirm as the app's dialog over the window; OK or CANCEL is the page's answer, a dialog
        /// closed with its ✕ answers CANCEL (the page waits for an answer).
        private void WebDialog(string kind, int id, string message)
        {
            if (kind.Length == 0 || id == 0 || id == _webJsShown || Router == null) return;
            _webJsShown = id;
            string text = message.Length > 400 ? message.Substring(0, 400) + "…" : message;
            var dlg = UiModal.Show(Router, Head, "THE PAGE SAYS", text, kind == "confirm" ? new[] { "OK", "CANCEL" } : new[] { "OK" },
                k => WebAnswer(id, k == 0), _root, _web);
            // closed by any means (its buttons close it before they answer): no answer by the next frame is CANCEL
            if (dlg != null) dlg.Closed += _ => _webJsDismissed = id;
        }

        private void WebAnswer(int id, bool ok)
        {
            if (_webJsAnswered == id) return;
            _webJsAnswered = id;
            _webLastJsAnswer = (id, ok);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv != null) try { _wv.Call("jsAnswer", id, ok); } catch (Exception ex) { Log("web jsAnswer: " + ex.Message); }
#endif
        }

        private void WebDialogTick()
        {
            if (_webJsDismissed == 0) return;
            int id = _webJsDismissed;
            _webJsDismissed = 0;
            if (_webJsAnswered != id) WebAnswer(id, false);
        }

        // ---- a hardware keyboard into the page (after a touch on the page, until a terminal or a text field takes the keys)

        /// Keys with no text: KeyEvent codes (Enter, Backspace, Tab, Esc, arrows, Home / End, Page Up / Down, Insert,
        /// Delete, F1-F12), sent with the modifiers held; they repeat while held.
        private static readonly (IsKey key, int code)[] WebNavKeys =
        {
            (IsKey.Enter, 66), (IsKey.NumpadEnter, 160), (IsKey.Backspace, 67), (IsKey.Tab, 61), (IsKey.Escape, 111),
            (IsKey.UpArrow, 19), (IsKey.DownArrow, 20), (IsKey.LeftArrow, 21), (IsKey.RightArrow, 22), (IsKey.Home, 122), (IsKey.End, 123),
            (IsKey.PageUp, 92), (IsKey.PageDown, 93), (IsKey.Insert, 124), (IsKey.Delete, 112),
            (IsKey.F1, 131), (IsKey.F2, 132), (IsKey.F3, 133), (IsKey.F4, 134), (IsKey.F5, 135), (IsKey.F6, 136),
            (IsKey.F7, 137), (IsKey.F8, 138), (IsKey.F9, 139), (IsKey.F10, 140), (IsKey.F11, 141), (IsKey.F12, 142),
        };
        /// Keys that are chords with Ctrl, Alt or Meta (Ctrl+S, Ctrl+Shift+P, Alt+1...): letters, digits, space, signs.
        private static readonly (IsKey key, int code)[] WebChordKeys =
        {
            (IsKey.A, 29), (IsKey.B, 30), (IsKey.C, 31), (IsKey.D, 32), (IsKey.E, 33), (IsKey.F, 34), (IsKey.G, 35), (IsKey.H, 36),
            (IsKey.I, 37), (IsKey.J, 38), (IsKey.K, 39), (IsKey.L, 40), (IsKey.M, 41), (IsKey.N, 42), (IsKey.O, 43), (IsKey.P, 44),
            (IsKey.Q, 45), (IsKey.R, 46), (IsKey.S, 47), (IsKey.T, 48), (IsKey.U, 49), (IsKey.V, 50), (IsKey.W, 51), (IsKey.X, 52),
            (IsKey.Y, 53), (IsKey.Z, 54), (IsKey.Digit0, 7), (IsKey.Digit1, 8), (IsKey.Digit2, 9), (IsKey.Digit3, 10), (IsKey.Digit4, 11),
            (IsKey.Digit5, 12), (IsKey.Digit6, 13), (IsKey.Digit7, 14), (IsKey.Digit8, 15), (IsKey.Digit9, 16), (IsKey.Space, 62),
            (IsKey.Minus, 69), (IsKey.Equals, 70), (IsKey.LeftBracket, 71), (IsKey.RightBracket, 72), (IsKey.Backslash, 73),
            (IsKey.Semicolon, 74), (IsKey.Quote, 75), (IsKey.Slash, 76), (IsKey.Comma, 55), (IsKey.Period, 56), (IsKey.Backquote, 68),
        };
        private const int WebKeyV = 50, WebKeyInsert = 124, WebKeyEscape = 111;
        private bool _webKeys, _webTermWasOn, _webFocusHooked;
        private IsKeyboard _webKb;
        private readonly StringBuilder _webTyped = new StringBuilder(32);
        private IsKey _webRepeatKey;
        private int _webRepeatCode = -1;
        private float _webRepeatAt;
        private string _webLastPaste;
        private (int code, int mods) _webLastKey = (-1, 0);

        /// The page takes the hardware keyboard (a touch on it) or gives it back (a terminal focused, a text field or the
        /// terminal's system keyboard opened, the window closed). While the page has it, the terminal's keyboard input is
        /// off, so a key never types in both.
        private void WebTakeKeys(bool on)
        {
            if (on == _webKeys) return;
            var input = app != null ? app.Input : null;
            if (on)
            {
                if (UiKeyboard.Active) return;
                _webKeys = true;
                if (input != null) { _webTermWasOn = input.enabled; input.enabled = false; }
                if (!_webFocusHooked && app != null) { _webFocusHooked = true; app.FocusChanged += _ => WebTakeKeys(false); }
                WebHookKeyboard(IsKeyboard.current);
            }
            else
            {
                _webKeys = false;
                WebHookKeyboard(null);
                _webTyped.Clear();
                _webRepeatCode = -1;
                if (input != null && _webTermWasOn) input.enabled = true;
            }
        }

        private void WebHookKeyboard(IsKeyboard kb)
        {
            if (kb == _webKb) return;
            if (_webKb != null) _webKb.onTextInput -= WebOnText;
            _webKb = kb;
            if (_webKb != null) _webKb.onTextInput += WebOnText;
        }

        private void WebOnText(char ch)
        {
            if (!_webKeys || _webKb == null || SystemKeyboard.Busy || UiKeyboard.Active) return;
            bool ctrl = _webKb.ctrlKey.isPressed, alt = _webKb.altKey.isPressed, meta = _webKb.leftMetaKey.isPressed || _webKb.rightMetaKey.isPressed;
            if (meta || ctrl != alt) return;     // chords come from the keys (AltGr, Ctrl + Alt, still types its sign)
            if (ch < 0x20 || ch == 0x7F) return; // Enter, Tab, Backspace come as keys
            _webTyped.Append(ch);
        }

        /// Each frame while the page has the keyboard: the text typed since the last frame (as key events), the keys
        /// pressed (with their modifiers), a held key repeating. No allocation unless something was typed.
        private void WebKeysTick()
        {
            if (!_webKeys) return;
            var input = app != null ? app.Input : null;
            if (UiKeyboard.Active || (input != null && SystemKeyboard.IsOpenFor(input))) { WebTakeKeys(false); return; }
            var kb = IsKeyboard.current;
            WebHookKeyboard(kb);
            if (kb == null || SystemKeyboard.Busy) { _webTyped.Clear(); _webRepeatCode = -1; return; } // the system keyboard's own keys
            if (_webTyped.Length > 0) { WebCall("typeText", _webTyped.ToString()); _webTyped.Clear(); }
            int mods = (kb.ctrlKey.isPressed ? 1 : 0) | (kb.shiftKey.isPressed ? 2 : 0) | (kb.altKey.isPressed ? 4 : 0) | (kb.leftMetaKey.isPressed || kb.rightMetaKey.isPressed ? 8 : 0);
            foreach (var (key, code) in WebNavKeys)
            {
                if (!kb[key].wasPressedThisFrame) continue;
                if (code == WebKeyInsert && mods == 2) { WebPaste(); continue; } // Shift+Insert
                WebKeyMods(code, mods);
                _webRepeatKey = key;
                _webRepeatCode = code;
                _webRepeatAt = Time.unscaledTime + 0.45f;
            }
            if (_webRepeatCode >= 0)
            {
                if (!kb[_webRepeatKey].isPressed) _webRepeatCode = -1;
                else if (Time.unscaledTime >= _webRepeatAt) { WebKeyMods(_webRepeatCode, mods); _webRepeatAt = Time.unscaledTime + 0.04f; }
            }
            if ((mods & (1 | 4 | 8)) == 0) return;
            foreach (var (key, code) in WebChordKeys)
            {
                if (!kb[key].wasPressedThisFrame) continue;
                if (code == WebKeyV && (mods & 1) != 0 && (mods & (4 | 8)) == 0) { WebPaste(); continue; } // Ctrl+V, Ctrl+Shift+V: the clipboard
                WebKeyMods(code, mods);
            }
        }

        /// A key with modifiers (bits 1 Ctrl, 2 Shift, 4 Alt, 8 Meta) into the page; raw JNI, nothing allocated.
        private void WebKeyMods(int code, int mods)
        {
            if (code == WebKeyEscape && mods == 0 && _wcPanel != null) { WebChoiceEnd(true); return; } // Esc closes the chooser
            _webLastKey = (code, mods);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv == null || _wvKeyMeta == IntPtr.Zero) return;
            _wvArgs[0].i = code;
            _wvArgs[1].i = mods;
            AndroidJNI.CallVoidMethod(_wv.GetRawObject(), _wvKeyMeta, _wvArgs);
            WebJniOk();
#endif
        }

        // ---- a page's dropdowns and pickers: the app's own chooser over the page (2026-11-17)
        //
        // The engine opens a <select>'s list and the date, time and colour pickers as dialogs on the app's activity,
        // which the headset never shows in the panel: the page could not use them. MetaCodexWebView's script stops
        // those and sends the element (its options, or its value, min and max, and where it is on the page); here a
        // chooser opens over the page, just under the element (above it when there is no room): a scrolling list, a
        // SEARCH field over 15 entries, optgroups as dim titles with their options indented, disabled options dim and
        // not chosen. One tap chooses (a multiple select ticks, DONE sends them); CANCEL, its ✕, Esc, a press on the
        // page beside it, the page changing or the window closing leave the page as it was. Date, time, datetime-local
        // (the date, then the time), month and week fields list the values round the current one (or today) inside the
        // page's min and max, a colour field a palette; TYPE takes any value on the keyboard (checked), CLEAR empties a
        // field that may be empty. The answer goes back through selectAnswer and the page sees input and change.

        /// A page's <select> (or date / time / colour field) the user opened: MetaCodexWebView.selectPayload, read.
        public sealed class WebChoice
        {
            public int Id;
            public string Kind = "select", Label = "", Value = "", Min = "", Max = "", Step = "";
            public bool Multiple, Required;
            /// The element on the page: fractions of the page shown (top-left origin).
            public Rect Box;
            public readonly List<WebChoiceOption> Options = new List<WebChoiceOption>();
            public readonly List<string> Groups = new List<string>();
        }

        public struct WebChoiceOption { public string Text; public bool Disabled, Selected, Hidden; public int Group; }

        /// More entries than this: a SEARCH field. Rows shown at once (the rest scroll).
        public const int WebChoiceSearchOver = 15, WebChoiceRows = 9;
        private const float WebChoiceRowH = 0.034f, WebChoiceGap = 0.03f; // a row; how far in front of the window it floats
        private UiPanel _wcPanel;
        private UiList _wcList;
        private UiTextField _wcSearch;
        private WebChoice _wc;
        private readonly HashSet<int> _wcPicked = new HashSet<int>(); // a multiple select: the options ticked
        private readonly List<(string value, string title, string sub)> _wcValues = new List<(string, string, string)>(); // a field's values
        private string _wcFilter = "", _wcDate = "";
        private int _wcStep, _wcCurrentRow = -1; // datetime-local: 0 the date, 1 the time; the row of the value it has
        private Vector3 _wcWebPos;
        private Quaternion _wcWebRot;
        private float _wcWebScale;
        private bool _webSwallow;
        private (int id, string answer) _webLastChoice;

        private static readonly (string name, string hex)[] WebColours =
        {
            ("black", "#000000"), ("white", "#ffffff"), ("grey", "#808080"), ("silver", "#c0c0c0"), ("red", "#ff0000"),
            ("maroon", "#800000"), ("orange", "#ffa500"), ("yellow", "#ffff00"), ("olive", "#808000"), ("lime", "#00ff00"),
            ("green", "#008000"), ("teal", "#008080"), ("cyan", "#00ffff"), ("sky blue", "#87ceeb"), ("blue", "#0000ff"),
            ("navy", "#000080"), ("purple", "#800080"), ("magenta", "#ff00ff"), ("pink", "#ffc0cb"), ("brown", "#a52a2a"),
        };

        /// A text from the page for the app's labels: one line, cut, and never TMP markup (a list row's <noparse> cannot
        /// be closed from the page; a title has no <noparse>, so it has no angle brackets at all).
        private static string WebPageText(string s, int max, bool title = false)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
            s = title ? s.Replace("<", "").Replace(">", "") : Regex.Replace(s, "</(noparse)", "</ $1", RegexOptions.IgnoreCase);
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        /// What the WebView sent (its checked JSON), or null when it cannot be read.
        public static WebChoice ParseWebChoice(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var o = JObject.Parse(json);
                var c = new WebChoice
                {
                    Id = (int?)o["id"] ?? 0,
                    Kind = (string)o["kind"] ?? "",
                    Label = WebPageText((string)o["label"], 40, true),
                    Value = (string)o["value"] ?? "",
                    Min = (string)o["min"] ?? "",
                    Max = (string)o["max"] ?? "",
                    Step = (string)o["step"] ?? "",
                    Multiple = (bool?)o["multiple"] ?? false,
                    Required = (bool?)o["required"] ?? false,
                };
                if (o["box"] is JArray b && b.Count == 4) c.Box = new Rect((float)b[0], (float)b[1], (float)b[2], (float)b[3]);
                if (o["groups"] is JArray g) foreach (var x in g) c.Groups.Add(WebPageText((string)x, 120));
                if (o["options"] is JArray opts)
                    foreach (var x in opts)
                        if (x is JObject op)
                            c.Options.Add(new WebChoiceOption
                            {
                                Text = WebPageText((string)op["t"], 160),
                                Disabled = (bool?)op["d"] ?? false,
                                Selected = (bool?)op["s"] ?? false,
                                Hidden = (bool?)op["h"] ?? false,
                                Group = (int?)op["g"] ?? -1,
                            });
                bool known = c.Kind == "select" ? c.Options.Count > 0 : WebChoiceNoun(c.Kind) != null;
                return c.Id > 0 && known ? c : null;
            }
            catch (Exception) { return null; }
        }

        /// A field's kind in words (null: not one the chooser knows), the example a typed value follows, the title.
        public static string WebChoiceNoun(string kind) => kind switch
        {
            "select" => "option", "date" => "date", "time" => "time", "datetime-local" => "date and time",
            "month" => "month", "week" => "week", "color" => "colour", _ => null,
        };

        private static string WebChoiceExample(string kind) => kind switch
        {
            "date" => "2026-11-17", "time" => "09:30", "datetime-local" => "2026-11-17 09:30", "month" => "2026-11",
            "week" => "2026-W47", "color" => "#3366ff", _ => "",
        };

        private static string WebChoiceTitle(string kind) => kind == "select" ? "CHOOSE" : (WebChoiceNoun(kind) ?? "CHOOSE").ToUpperInvariant();

#if UNITY_ANDROID && !UNITY_EDITOR
        /// Poll bit 8: the page's request (or "", none waits any more).
        private void WebChoiceTake()
        {
            string json;
            try { json = _wv.Call<string>("selectPayload"); } catch (Exception ex) { Log("web selectPayload: " + ex.Message); return; }
            WebChoiceOpen(json);
        }
#endif

        /// A request from the page opens the chooser; "" (the page it was on is gone) closes it.
        private void WebChoiceOpen(string json)
        {
            if (string.IsNullOrEmpty(json)) { if (_wc != null) WebChoiceEnd(false); return; }
            var c = ParseWebChoice(json);
            if (c == null) { Log("web: a chooser request from the page could not be read"); return; }
            if (_wc != null && _wc.Id == c.Id) return;
            WebChoiceEnd(false); // an older one: the WebView keeps only the newest
            if (_web == null || !_web.IsOpen || _webPage == null || Router == null) { WebChoiceCancel(c.Id); return; }
            _wc = c;
            _wcStep = 0;
            _wcDate = "";
            _wcPicked.Clear();
            if (c.Multiple) for (int i = 0; i < c.Options.Count; i++) if (c.Options[i].Selected) _wcPicked.Add(i);
            Log($"web: chooser {c.Kind}{(c.Multiple ? " (multiple)" : "")}{(c.Kind == "select" ? ", " + c.Options.Count + " options" : "")}");
            WebChoiceBuild();
        }

        /// What the chooser shows now: the kind, or for datetime-local the date (step 0) then the time.
        private string WebChoiceKindNow() => _wc == null ? "" : _wc.Kind != "datetime-local" ? _wc.Kind : _wcStep == 0 ? "date" : "time";

        /// The field's value for what the chooser shows now (datetime-local: its date or its time part).
        private string WebChoiceCurrent()
        {
            string v = _wc?.Value ?? "";
            if (_wc == null || _wc.Kind != "datetime-local") return v;
            int t = v.IndexOf('T');
            return t < 0 ? "" : _wcStep == 0 ? v.Substring(0, t) : v.Substring(t + 1);
        }

        /// The page's min and max for what the chooser shows now (datetime-local: the date's, then the time's on the
        /// first and the last day allowed).
        private void WebChoiceRange(out string min, out string max)
        {
            min = _wc.Min;
            max = _wc.Max;
            if (_wc.Kind != "datetime-local") return;
            string Part(string s, bool date) { int t = s.IndexOf('T'); return t < 0 ? (date ? s : "") : date ? s.Substring(0, t) : s.Substring(t + 1); }
            if (_wcStep == 0) { min = Part(_wc.Min, true); max = Part(_wc.Max, true); return; }
            min = Part(_wc.Min, true) == _wcDate ? Part(_wc.Min, false) : "";
            max = Part(_wc.Max, true) == _wcDate ? Part(_wc.Max, false) : "";
        }

        /// The chooser's panel, over the WEB window at the element (once per opening: it may allocate).
        private void WebChoiceBuild()
        {
            var t = UiTheme.Current;
            var c = _wc;
            if (c == null || _web == null || _webPage == null || Router == null) return;
            string kind = WebChoiceKindNow();
            if (kind != "select") WebChoiceValues(kind);
            int n = kind == "select" ? c.Options.Count(o => !o.Hidden) + c.Groups.Count : _wcValues.Count;
            bool search = n > WebChoiceSearchOver;
            int rows = Mathf.Clamp(n, 1, WebChoiceRows);
            float bh = t.ButtonHeight, w = Mathf.Clamp(c.Box.width * _webPage.Rect.width + 0.02f, 0.30f, 0.42f);
            float h = t.HeaderHeight + t.Padding + (search ? bh + t.Gap : 0f) + rows * WebChoiceRowH + t.Gap + bh + t.Padding;
            string title = c.Label.Length > 0 ? c.Label : WebChoiceTitle(c.Kind);
            if (c.Kind == "datetime-local") title = (c.Label.Length > 0 ? c.Label + " · " : "") + (_wcStep == 0 ? "DATE" : "TIME");
            var p = UiPanel.Create<UiPanel>("modal", title, w, h, _root); // a dialog, not a window (placement, layouts)
            var cr = p.Content;
            float y = cr.y;
            _wcSearch = null;
            if (search)
            {
                _wcSearch = p.Add(new UiTextField("", "", WebChoiceFilter, "search " + n + (kind == "select" ? " options" : " values")) { Rect = new Rect(cr.x, y, cr.width, bh) });
                _wcSearch.OnChanged = WebChoiceFilter; // as it is typed
                y += bh + t.Gap;
            }
            _wcList = p.Add(new UiList(WebChoiceRowH, (item, idx) => WebChoicePick(item)) { Rect = new Rect(cr.x, y, cr.width, rows * WebChoiceRowH) });
            y += rows * WebChoiceRowH + t.Gap;
            // DONE (a multiple select), TYPE (a field: any value, typed), CLEAR (a field that may be empty), CANCEL
            var buttons = new List<(string text, Action act, UiButtonStyle style)>();
            if (kind == "select" && c.Multiple) buttons.Add(("DONE", WebChoiceDone, UiButtonStyle.Primary));
            if (kind != "select") buttons.Add(("TYPE", WebChoiceType, UiButtonStyle.Normal));
            if (kind != "select" && !c.Required && c.Value.Length > 0) buttons.Add(("CLEAR", () => WebChoiceFinish(""), UiButtonStyle.Normal));
            buttons.Add(("CANCEL", () => WebChoiceEnd(true), UiButtonStyle.Normal));
            float bw = (cr.width - t.Gap * (buttons.Count - 1)) / buttons.Count;
            for (int i = 0; i < buttons.Count; i++)
                p.Add(new UiButton(buttons[i].text, buttons[i].act, buttons[i].style) { Rect = new Rect(cr.x + i * (bw + t.Gap), y, bw, bh) });
            p.Closed += _ =>
            {
                Router?.Unregister(p);
                if (ReferenceEquals(_wcPanel, p)) WebChoiceEnd(true); // its ✕: the page keeps what it had
            };
            _wcPanel = p;
            _wcFilter = "";
            WebChoiceRefresh(false);
            WebChoicePlace();
            p.SetHighlight(1f);
            Router.Register(p);
            p.Open();
        }

        /// The chooser under the element (its top at the element's bottom, its left at the element's left), above it when
        /// there is no room under it, inside the window; parallel to the window, a little in front of it, at its scale.
        private void WebChoicePlace()
        {
            var p = _wcPanel;
            var c = _wc;
            if (p == null || c == null || _web == null || _webPage == null) return;
            var area = _webPage.Rect;
            float ex = area.x + Mathf.Clamp01(c.Box.x) * area.width;
            float top = area.y + Mathf.Clamp01(c.Box.y) * area.height, bottom = area.y + Mathf.Clamp01(c.Box.yMax) * area.height;
            float left = Mathf.Clamp(ex, 0f, Mathf.Max(0f, _web.Width - p.Width));
            float y = bottom + 0.004f + p.Height <= _web.Height ? bottom + 0.004f
                : top - 0.004f - p.Height >= 0f ? top - 0.004f - p.Height
                : Mathf.Clamp(bottom, 0f, Mathf.Max(0f, _web.Height - p.Height));
            var wt = _web.transform;
            var centre = wt.TransformPoint(_web.PanelToLocal(new Vector2(left + p.Width * 0.5f, y + p.Height * 0.5f)));
            p.transform.SetPositionAndRotation(centre - wt.forward * WebChoiceGap, wt.rotation); // towards the user
            float rootScale = _root != null ? Mathf.Max(1e-4f, _root.lossyScale.x) : 1f;
            p.BaseScale = Vector3.one * (_web.RestLossyScale.x / rootScale);
            _wcWebPos = wt.position;
            _wcWebRot = wt.rotation;
            _wcWebScale = _web.BaseScale.x;
        }

        /// Each frame while it is open: the window moved or was scaled, the chooser goes with it (compares only).
        private void WebChoiceFollow()
        {
            if (_web == null) return;
            var wt = _web.transform;
            if (wt.position != _wcWebPos || wt.rotation != _wcWebRot || _web.BaseScale.x != _wcWebScale) WebChoicePlace();
        }

        private void WebChoiceFilter(string s)
        {
            string f = (s ?? "").Trim();
            if (f == _wcFilter) return;
            _wcFilter = f;
            WebChoiceRefresh(false);
        }

        private bool WebChoiceMatch(string a, string b) =>
            (a != null && a.IndexOf(_wcFilter, StringComparison.OrdinalIgnoreCase) >= 0) || (b != null && b.IndexOf(_wcFilter, StringComparison.OrdinalIgnoreCase) >= 0);

        /// The list again (opened, searched, the date picked for a datetime-local), its current entry lit and in view.
        private void WebChoiceRefresh(bool keepScroll)
        {
            if (_wcList == null || _wc == null) return;
            var items = new List<UiListItem>();
            int current = WebChoiceKindNow() == "select" ? WebChoiceSelectItems(items) : WebChoiceValueItems(items);
            _wcCurrentRow = current;
            _wcList.SelectedIndex = current;
            _wcList.SetItems(items, keepScroll);
            if (!keepScroll && current > 2) _wcList.ScrollBy(current - 2);
            _wcList.SetEmptyText(_wcFilter.Length > 0 ? "Nothing matches “" + _wcFilter + "”" : "Nothing to choose here");
        }

        /// A select's rows: optgroups as dim titles (tapping one does nothing), their options indented; hidden options
        /// are not listed, disabled ones dim. Returns the row of the selected option (-1: none, or a multiple select).
        private int WebChoiceSelectItems(List<UiListItem> items)
        {
            var t = UiTheme.Current;
            var c = _wc;
            int lastGroup = -1, current = -1;
            for (int i = 0; i < c.Options.Count; i++)
            {
                var o = c.Options[i];
                if (o.Hidden) continue;
                string group = o.Group >= 0 && o.Group < c.Groups.Count ? c.Groups[o.Group] : null;
                if (_wcFilter.Length > 0 && !WebChoiceMatch(o.Text, group)) continue;
                if (o.Group != lastGroup)
                {
                    lastGroup = o.Group;
                    if (group != null) items.Add(new UiListItem { Icon = "▾", IconColor = t.TextDim, Title = group.Length > 0 ? group : "—", TitleColor = t.TextDim });
                }
                bool on = c.Multiple ? _wcPicked.Contains(i) : o.Selected;
                if (on && !c.Multiple && current < 0) current = items.Count;
                items.Add(new UiListItem
                {
                    Icon = c.Multiple ? (on ? "☑" : "·") : on ? "◆" : "◇",
                    IconColor = on ? t.Ok : o.Disabled ? t.TextDim : (Color?)null,
                    Title = o.Text.Length > 0 ? o.Text : "—",
                    TitleColor = o.Disabled ? t.TextDim : (Color?)null,
                    Sub = o.Disabled ? "not available" : "",
                    Indent = group != null ? 1 : 0,
                    Tag = i,
                });
            }
            return current;
        }

        /// A field's rows (WebChoiceValues), the value it has lit. Returns that row (-1: not listed).
        private int WebChoiceValueItems(List<UiListItem> items)
        {
            var t = UiTheme.Current;
            string cur = WebChoiceCurrent();
            bool colour = WebChoiceKindNow() == "color";
            int current = -1;
            foreach (var (v, title, sub) in _wcValues)
            {
                if (_wcFilter.Length > 0 && !WebChoiceMatch(title, sub)) continue;
                bool on = string.Equals(v, cur, StringComparison.OrdinalIgnoreCase);
                if (on && current < 0) current = items.Count;
                Color? swatch = null;
                if (colour && ColorUtility.TryParseHtmlString(v, out var col)) swatch = col;
                items.Add(new UiListItem { Icon = colour ? "■" : on ? "◆" : "◇", IconColor = swatch ?? (on ? t.Ok : (Color?)null), Title = title, Sub = sub, Badge = on ? "✓" : "", BadgeColor = t.Ok, Tag = v });
            }
            return current;
        }

        /// The values a field offers, inside the page's min and max: days from two weeks before the current one (or
        /// today) to six weeks after; months a year before to two years after; weeks two months before to half a year
        /// after; the day's times at the page's step (15 minutes unless it asks for 15 minutes or more), with the
        /// current one; a palette of 20 colours, with the current one.
        private void WebChoiceValues(string kind)
        {
            _wcValues.Clear();
            var inv = CultureInfo.InvariantCulture;
            string cur = WebChoiceCurrent();
            WebChoiceRange(out string min, out string max);
            var today = DateTime.Today;
            DateTime Base(string fmt)
            {
                var d = DateTime.TryParseExact(cur, fmt, inv, DateTimeStyles.None, out var x) ? x : today;
                return d.Year < 1000 || d.Year > 9000 ? today : d;
            }
            void Add(string v, string title, string sub) { if (WebChoiceInRange(kind, v, min, max)) _wcValues.Add((v, title, sub)); }
            switch (kind)
            {
                case "date":
                {
                    var b = Base("yyyy-MM-dd");
                    for (int i = -14; i <= 45; i++)
                    {
                        var d = b.AddDays(i);
                        string v = d.ToString("yyyy-MM-dd", inv);
                        Add(v, v, d.ToString("ddd d MMM", inv) + (d == today ? " · today" : ""));
                    }
                    break;
                }
                case "month":
                {
                    var b = Base("yyyy-MM");
                    for (int i = -12; i <= 24; i++)
                    {
                        var d = new DateTime(b.Year, b.Month, 1).AddMonths(i);
                        string v = d.ToString("yyyy-MM", inv);
                        Add(v, v, d.ToString("MMMM yyyy", inv) + (d.Year == today.Year && d.Month == today.Month ? " · this month" : ""));
                    }
                    break;
                }
                case "week":
                {
                    var m = Regex.Match(cur, @"^(\d{4})-W(\d{2})$");
                    var b = m.Success ? IsoWeekMonday(int.Parse(m.Groups[1].Value, inv), int.Parse(m.Groups[2].Value, inv)) : today;
                    if (b.Year < 1000 || b.Year > 9000) b = today;
                    var (ty, tw) = IsoWeekOf(today);
                    for (int i = -8; i <= 26; i++)
                    {
                        var (y, w) = IsoWeekOf(b.AddDays(7 * i));
                        var mon = IsoWeekMonday(y, w);
                        string v = y.ToString("D4", inv) + "-W" + w.ToString("D2", inv);
                        Add(v, v, "from " + mon.ToString("ddd d MMM yyyy", inv) + (y == ty && w == tw ? " · this week" : ""));
                    }
                    break;
                }
                case "time":
                {
                    int stepMin = 15;
                    if (double.TryParse(_wc.Step, NumberStyles.Float, inv, out double st) && st >= 900 && st <= 43200 && Math.Abs(st / 60.0 - Math.Round(st / 60.0)) < 1e-9)
                        stepMin = (int)Math.Round(st / 60.0);
                    var times = new List<string>();
                    for (int mm = 0; mm < 1440; mm += stepMin) times.Add((mm / 60).ToString("D2", inv) + ":" + (mm % 60).ToString("D2", inv));
                    if (Regex.IsMatch(cur, @"^\d\d:\d\d(:\d\d(\.\d+)?)?$") && !times.Contains(cur)) { times.Add(cur); times.Sort(string.CompareOrdinal); }
                    foreach (var v in times)
                    {
                        string sub = DateTime.TryParseExact(v.Length > 5 ? v.Substring(0, 5) : v, "HH:mm", inv, DateTimeStyles.None, out var tm) ? tm.ToString("h:mm tt", inv).ToLowerInvariant() : "";
                        Add(v, v, sub);
                    }
                    break;
                }
                case "color":
                {
                    string c = cur.ToLowerInvariant();
                    if (Regex.IsMatch(c, "^#[0-9a-f]{6}$") && Array.FindIndex(WebColours, x => x.hex == c) < 0) _wcValues.Add((c, "this colour", c));
                    foreach (var (name, hex) in WebColours) _wcValues.Add((hex, name, hex));
                    break;
                }
            }
        }

        /// Inside the page's min and max (same-format values compare as text); a time range over midnight (min after
        /// max) allows either side.
        public static bool WebChoiceInRange(string kind, string v, string min, string max)
        {
            bool lo = string.IsNullOrEmpty(min) || string.CompareOrdinal(v, min) >= 0;
            bool hi = string.IsNullOrEmpty(max) || string.CompareOrdinal(v, max) <= 0;
            if (kind == "time" && !string.IsNullOrEmpty(min) && !string.IsNullOrEmpty(max) && string.CompareOrdinal(min, max) > 0) return lo || hi;
            return lo && hi;
        }

        /// ISO 8601 weeks: the week (and its year) a day is in, the Monday a week starts on.
        private static (int year, int week) IsoWeekOf(DateTime d)
        {
            int dow = ((int)d.DayOfWeek + 6) % 7; // Monday 0
            var thursday = d.AddDays(3 - dow);
            return (thursday.Year, (thursday.DayOfYear - 1) / 7 + 1);
        }

        private static DateTime IsoWeekMonday(int year, int week)
        {
            var jan4 = new DateTime(year, 1, 4);
            return jan4.AddDays(-(((int)jan4.DayOfWeek + 6) % 7) + (week - 1) * 7);
        }

        /// A typed value for a field (TYPE): its kind's format (a few spellings are taken: 2026-11-7, 9:05, 2026/11/07,
        /// #36f), then the page's min and max. `value` is what goes into the field (its canonical form); `why` says what
        /// is wrong. Empty clears a field that may be empty.
        public static bool WebChoiceValid(string kind, string typed, string min, string max, bool required, out string value, out string why)
        {
            var inv = CultureInfo.InvariantCulture;
            string s = (typed ?? "").Trim();
            value = "";
            why = "";
            if (s.Length == 0)
            {
                if (!required) return true;
                why = "The page needs a " + (WebChoiceNoun(kind) ?? "value") + " here";
                return false;
            }
            bool ok = false;
            switch (kind)
            {
                case "date":
                    ok = DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d" }, inv, DateTimeStyles.None, out var d);
                    if (ok) value = d.ToString("yyyy-MM-dd", inv);
                    break;
                case "time":
                    ok = DateTime.TryParseExact(s, new[] { "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss" }, inv, DateTimeStyles.None, out var tm);
                    if (ok) value = tm.ToString(tm.Second != 0 ? "HH:mm:ss" : "HH:mm", inv);
                    break;
                case "datetime-local":
                    ok = DateTime.TryParseExact(s, new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-M-d H:mm", "yyyy-M-dTH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                        inv, DateTimeStyles.None, out var dt);
                    if (ok) value = dt.ToString(dt.Second != 0 ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-ddTHH:mm", inv);
                    break;
                case "month":
                    ok = DateTime.TryParseExact(s, new[] { "yyyy-MM", "yyyy-M", "yyyy/MM", "yyyy/M" }, inv, DateTimeStyles.None, out var mo);
                    if (ok) value = mo.ToString("yyyy-MM", inv);
                    break;
                case "week":
                {
                    var m = Regex.Match(s, @"^(\d{4})-?[Ww](\d{1,2})$");
                    if (m.Success)
                    {
                        int y = int.Parse(m.Groups[1].Value, inv), w = int.Parse(m.Groups[2].Value, inv);
                        ok = y >= 1000 && w >= 1 && w <= IsoWeekOf(new DateTime(y, 12, 28)).week;
                        if (ok) value = y.ToString("D4", inv) + "-W" + w.ToString("D2", inv);
                    }
                    break;
                }
                case "color":
                {
                    var m = Regex.Match(s, "^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$");
                    if (m.Success)
                    {
                        string h = m.Groups[1].Value.ToLowerInvariant();
                        if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
                        value = "#" + h;
                        ok = true;
                    }
                    break;
                }
            }
            if (!ok) { value = ""; why = "Not a valid " + (WebChoiceNoun(kind) ?? "value") + ": type it as " + WebChoiceExample(kind); return false; }
            if (kind != "color" && !WebChoiceInRange(kind, value, min, max))
            {
                why = "The page takes a " + WebChoiceNoun(kind) + (string.IsNullOrEmpty(min) ? "" : " from " + min) + (string.IsNullOrEmpty(max) ? "" : " up to " + max);
                value = "";
                return false;
            }
            return true;
        }

        /// A row tapped: a select's option is chosen (a multiple one ticks or unticks it; a group's title or a disabled
        /// option does nothing), a field's value is chosen (datetime-local: its date, then the time).
        private void WebChoicePick(UiListItem item)
        {
            var c = _wc;
            if (c == null || item == null || _wcList == null) return;
            if (WebChoiceKindNow() == "select")
            {
                if (!(item.Tag is int i) || i < 0 || i >= c.Options.Count || c.Options[i].Disabled)
                {
                    _wcList.SelectedIndex = c.Multiple ? -1 : _wcCurrentRow; // the light goes back where it was
                    _wcList.ScrollBy(0f);
                    return;
                }
                if (c.Multiple)
                {
                    bool on = !_wcPicked.Remove(i);
                    if (on) _wcPicked.Add(i);
                    item.Icon = on ? "☑" : "·";
                    item.IconColor = on ? UiTheme.Current.Ok : (Color?)null;
                    _wcList.SelectedIndex = -1; // the ticks show what is chosen
                    _wcList.ScrollBy(0f);       // drawn again, in place
                    return;
                }
                WebChoiceFinish(i.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (!(item.Tag is string v)) return;
            if (c.Kind == "datetime-local" && _wcStep == 0) // the date: now its time, in the same place
            {
                _wcDate = v;
                _wcStep = 1;
                WebChoiceDropPanel();
                WebChoiceBuild();
                return;
            }
            WebChoiceFinish(c.Kind == "datetime-local" ? _wcDate + "T" + v : v);
        }

        /// DONE (a multiple select): the options ticked, in order.
        private void WebChoiceDone()
        {
            if (_wc == null) return;
            var sb = new StringBuilder();
            foreach (int i in _wcPicked.OrderBy(x => x)) { if (sb.Length > 0) sb.Append(','); sb.Append(i.ToString(CultureInfo.InvariantCulture)); }
            WebChoiceFinish(sb.ToString());
        }

        /// TYPE: any value for the field on the keyboard, checked (WebChoiceValid); Cancel or the same value keeps the
        /// chooser open, a wrong one says why in a toast.
        private void WebChoiceType()
        {
            var c = _wc;
            if (c == null) return;
            int id = c.Id;
            UiKeyboard.Edit(c.Value, false, null, typed =>
            {
                if (_wc == null || _wc.Id != id) return; // closed meanwhile
                string s = (typed ?? "").Trim();
                if (s == c.Value) return;
                if (!WebChoiceValid(c.Kind, s, c.Min, c.Max, c.Required, out string value, out string why)) { Toast(why, "warn"); return; }
                WebChoiceFinish(value);
            }, "Type a " + WebChoiceNoun(c.Kind) + ", as " + WebChoiceExample(c.Kind));
        }

        private void WebChoiceFinish(string answer)
        {
            var c = _wc;
            WebChoiceEnd(false);
            if (c != null) WebChoiceAnswer(c.Id, answer);
        }

        /// The chooser's panel goes (the request stays: datetime-local's second step is built after it).
        private void WebChoiceDropPanel()
        {
            var p = _wcPanel;
            _wcPanel = null;
            _wcList = null;
            _wcSearch = null;
            if (p != null) { Router?.Unregister(p); p.CloseAndDestroy(); }
        }

        /// The chooser closes; `cancel`: without a choice (the page keeps what it had).
        private void WebChoiceEnd(bool cancel)
        {
            var c = _wc;
            _wc = null;
            WebChoiceDropPanel();
            _wcValues.Clear();
            _wcPicked.Clear();
            _wcFilter = "";
            if (cancel && c != null) WebChoiceCancel(c.Id);
        }

        private void WebChoiceAnswer(int id, string answer)
        {
            _webLastChoice = (id, answer);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv == null) return;
            try { if (!_wv.Call<bool>("selectAnswer", id, answer)) Log("web: the page's chooser was gone (its page changed): nothing written"); }
            catch (Exception ex) { Log("web selectAnswer: " + ex.Message); }
#endif
        }

        private void WebChoiceCancel(int id)
        {
            _webLastChoice = (id, null);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv != null) try { _wv.Call("selectCancel", id); } catch (Exception ex) { Log("web selectCancel: " + ex.Message); }
#endif
        }

        /// Self-tests: a request as the WebView sends it (no WebView in the Editor), the chooser open now (its panel, list
        /// and SEARCH field), the last answer sent (answer null: cancelled), a search typed.
        public void WebChoiceForTest(string json) => WebChoiceOpen(json);
        public UiPanel WebChoicePanelForTest => _wcPanel;
        public UiList WebChoiceListForTest => _wcList;
        public UiTextField WebChoiceSearchFieldForTest => _wcSearch;
        public (int id, string answer) WebLastChoiceForTest => _webLastChoice;
        public void WebChoiceSearchForTest(string s) => WebChoiceFilter(s);

        /// Self-tests: the page the window has, the last touch sent to the page, a panel point in page pixels.
        public string WebUrlForTest => _webCurrent;
        public bool WebOnStartForTest => _webOnStart;
        public (int action, Vector2 page) WebLastTouchForTest => _webLastTouch;
        public Rect WebPageRectForTest => _webPage != null ? _webPage.Rect : default;
        public Texture2D WebTextureForTest => _webTex;
        /// Self-tests: the page's state as the WebView reports it (no WebView in the Editor), a frame of the window, what
        /// the window shows, the last answer to a page dialog.
        public void WebApplyStateForTest(string state) => WebApplyState(state);
        public void WebTickForTest() => TickWeb();
        public bool WebFullscreenForTest => _webFull;
        public bool WebErrorShownForTest => _webDark;
        public string WebNoteForTest => _webNoteShown ?? "";
        public string WebTitleForTest => _webTitleShown ?? "";
        public (int id, bool ok) WebLastDialogAnswerForTest => _webLastJsAnswer;
        /// Self-tests: the page's size, whether the page has the hardware keyboard, the last key sent, the last paste,
        /// a key into the page as the keyboard would send it.
        public Vector2Int WebPageSizeForTest => new Vector2Int(_webPageW, _webPageH);
        public bool WebHasKeysForTest => _webKeys;
        public (int code, int mods) WebLastKeyForTest => _webLastKey;
        public string WebLastPasteForTest => _webLastPaste;
        public void WebPasteForTest() => WebPaste();
        public void WebTakeKeysForTest(bool on) => WebTakeKeys(on);
    }
}
