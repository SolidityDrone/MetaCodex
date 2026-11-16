using System;
using System.Linq;
using System.Text;
using MetaCodex.UI;
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
            p.Closed += _ => { WebCallBool("setPaused", true); WebTakeKeys(false); }; // the page's media pause; no drawing, no copying while closed
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
        private const int WebKeyV = 50, WebKeyInsert = 124;
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
            _webLastKey = (code, mods);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv == null || _wvKeyMeta == IntPtr.Zero) return;
            _wvArgs[0].i = code;
            _wvArgs[1].i = mods;
            AndroidJNI.CallVoidMethod(_wv.GetRawObject(), _wvKeyMeta, _wvArgs);
            WebJniOk();
#endif
        }

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
