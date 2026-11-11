using System;
using System.Linq;
using System.Text;
using MetaCodex.Typing;
using MetaCodex.UI;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// WEB: the in-app browser window (owner 2026-11-10: "it's boring to have the system browser turn the app off";
    /// Horizon OS cannot show its browser beside an immersive app: even its Web Tasks send the app to the background).
    /// On the headset the page is Android's own WebView (MetaCodexWebView.java: laid out off screen, drawn into an
    /// ImageReader only when the page changes, copied into one buffer), uploaded here into a texture on a flat quad (the
    /// window never bends: ShellLayout.StaysFlat, so the page and the pointer agree). A ray's pinch or a fingertip is a
    /// touch, a drag scrolls, the pointer over the page hovers, ⌨ types into the page's focused field, ⏎ and ⌫ are
    /// the keys. Every page the app opens (GO, bookmarks, local servers, terminal links, voice, Lua browser.open) opens
    /// here; ⇗ sends the page to Horizon OS's browser (sign-in pages that refuse embedded browsers). Remote debugging is
    /// on: chrome://inspect on the PC (adb) shows the page's console. In the Editor there is no WebView: the window works,
    /// the page area says so.
    public sealed partial class MetaCodexShell
    {
        public const int WebW = 1280, WebH = 800;
        private UiPanel _web;
        private UiTextField _webUrl;
        private UiLabel _webTitle, _webNote;
        private UiButton _webBack, _webFwd;
        private WebPage _webPage;
        private Texture2D _webTex;
        private Mesh _webQuad;
        private string _webCurrent = "", _webError = "";
        private bool _webOnStart;
        private float _webNextState;
        private Vector2 _webLastHover = new Vector2(-1f, -1f);
        private (int action, Vector2 page) _webLastTouch = (-1, default);
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _wv;
        private Unity.Collections.NativeArray<byte> _wvPixels; // a view of the Java buffer (no copy, no unsafe code)
#endif

        /// The page area: a pointer on it, in page pixels (top-left origin), goes to the page.
        private sealed class WebPage : UiElement
        {
            private readonly MetaCodexShell _shell;
            public override bool Interactive => true;
            public WebPage(MetaCodexShell shell) { _shell = shell; }
            protected override void Build() { }
            public Vector2 ToPage(Vector2 p) => new Vector2(Mathf.Clamp01((p.x - Rect.x) / Rect.width) * WebW, Mathf.Clamp01((p.y - Rect.y) / Rect.height) * WebH);
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
            float bh = t.ButtonHeight, sq = 0.040f, x = c.x;
            UiButton B(string text, Action act, float w, UiButtonStyle style = UiButtonStyle.Normal)
            {
                var b = p.Add(new UiButton(text, act, style) { Rect = new Rect(x, c.y, w, bh) });
                x += w + t.Gap;
                return b;
            }
            _webBack = B("◀", () => WebCall("back"), sq);
            _webFwd = B("▶", () => WebCall("forward"), sq);
            B("⟳", () => WebCall("reload"), sq);
            float right = 0.056f + (sq + t.Gap) * 5f; // GO, then ⌨ ⏎ ⌫ ☰ ⇗
            _webUrl = p.Add(new UiTextField("", "", u => OpenBrowser(u), "a URL, or localhost:5173") { Rect = new Rect(x, c.y, c.xMax - right - x - t.Gap, bh) });
            x = c.xMax - right;
            B("GO", () => OpenBrowser(_webUrl.Text), 0.056f, UiButtonStyle.Primary);
            B("⌨", WebType, sq);
            B("⏎", () => WebKey(66), sq);  // KEYCODE_ENTER
            B("⌫", () => WebKey(67), sq);  // KEYCODE_DEL
            B("☰", () => OpenPanel("links"), sq); // bookmarks and local servers, beside this window
            B("⇗", () => OpenSystemBrowser(_webCurrent), sq);
            float titleY = c.y + bh + t.Gap * 0.5f, top = titleY + 0.016f;
            _webTitle = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, titleY, c.width, 0.016f), Color = t.TextDim });
            float pw = c.width, ph = pw * WebH / WebW;
            if (top + ph > c.yMax) { ph = c.yMax - top; pw = ph * WebW / WebH; }
            var area = new Rect(c.x + (c.width - pw) * 0.5f, top, pw, ph);
            _webPage = p.Add(new WebPage(this) { Rect = area });
            if (_webTex == null) // kept when the panels are rebuilt (accent colour)
            {
                _webTex = new Texture2D(WebW, WebH, TextureFormat.RGBA32, false, false) { name = "web page", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var fill = new Color32[WebW * WebH];
                for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(6, 16, 28, 255);
                _webTex.SetPixels32(fill);
                _webTex.Apply(false, false);
            }
            AddWebQuad(p, area);
            _webNote = p.Add(new UiLabel("", t.BodySize) { Rect = new Rect(area.x + 0.03f, area.y + area.height * 0.4f, area.width - 0.06f, 0.06f), Align = TextAlignmentOptions.Center, Color = t.TextDim, Wrap = true });
#if UNITY_EDITOR
            _webNote.Set("The page shows on the headset (Android's WebView). ⇗ opens it in your browser here.");
#endif
            p.Tick += _ => TickWeb();
            p.Opened += _ =>
            {
                WebEnsure();
                WebCallBool("setPaused", false);
                if (string.IsNullOrEmpty(_webCurrent)) { WebStartPage(); ProbeLocalServers(); }
            };
            p.Closed += _ => WebCallBool("setPaused", true); // no drawing, no copying while closed
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
            _webUrl?.Set("");
            WebCall("loadHtml", sb.ToString(), "about:blank");
        }

        private static string Escape(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// ⌨: the keyboard types straight into the page's focused field (tap the field first), key by key.
        private void WebType()
        {
            if (Keyboard == null) return;
            _webSink ??= new WebSink(this);
            if (Keyboard.Visible && Keyboard.Sink == _webSink) { Keyboard.Hide(); return; }
            Keyboard.Show(_webSink);
        }

        private WebSink _webSink;

        /// The keyboard's keys into the page (Android key codes for the special keys).
        private sealed class WebSink : IKeySink
        {
            private readonly MetaCodexShell _s;
            public WebSink(MetaCodexShell s) { _s = s; }
            public string Title => "WEB PAGE";
            public string Preview => null;
            public bool KeepsKeyboard => true;
            public KeyContext Context => KeyContext.Code;
            public void Type(string text, bool ctrl, bool alt) { if (!string.IsNullOrEmpty(text)) _s.WebCall("text", text); }
            public void Key(KbKind key, bool ctrl, bool alt, bool shift, int fn)
            {
                switch (key)
                {
                    case KbKind.Backspace: _s.WebKey(67); break;
                    case KbKind.Enter: _s.WebKey(66); break;
                    case KbKind.Tab: _s.WebKey(61); break;
                    case KbKind.Escape: _s.WebKey(111); break;
                    case KbKind.Left: _s.WebKey(21); break;
                    case KbKind.Right: _s.WebKey(22); break;
                    case KbKind.Up: _s.WebKey(19); break;
                    case KbKind.Down: _s.WebKey(20); break;
                    case KbKind.Delete: _s.WebKey(112); break;
                    case KbKind.Home: _s.WebKey(122); break;
                    case KbKind.End: _s.WebKey(123); break;
                    case KbKind.PageUp: _s.WebKey(92); break;
                    case KbKind.PageDown: _s.WebKey(93); break;
                    case KbKind.Function: if (fn >= 1 && fn <= 12) _s.WebKey(130 + fn); break;
                    case KbKind.Paste: _s.WebCall("text", GUIUtility.systemCopyBuffer ?? ""); break;
                }
            }
        }

        private void WebKey(int code) => WebCallInt("key", code);

        private void WebTouch(int action, Vector2 page)
        {
            _webLastTouch = (action, page);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv != null) try { _wv.Call("touch", action, page.x, page.y); } catch (Exception) { }
#endif
        }

        private void WebHover(Vector2 page)
        {
            if ((page - _webLastHover).sqrMagnitude < 4f) return; // only when it moved two pixels
            _webLastHover = page;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv != null) try { _wv.Call("hover", page.x, page.y); } catch (Exception) { }
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
                    _wv = new AndroidJavaObject("com.pnncl.metacodex.web.MetaCodexWebView", activity, WebW, WebH);
                _wv.Call("setExternal", string.Join("\n", SignInHosts), string.Join("\n", SignInPaths));
                using (var buf = _wv.Call<AndroidJavaObject>("pixels")) _wvPixels = AndroidJNI.GetDirectByteBuffer(buf.GetRawObject());
                return true;
            }
            catch (Exception ex) { Log("web: " + ex.Message); _webError = "webview: " + ex.Message; _wv = null; return false; }
#else
            return true; // the Editor: the window works without a page
#endif
        }

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

        /// While the window is open: a new frame of the page into the texture (only when the page changed), the
        /// toolbar from the page's state four times a second.
        private void TickWeb()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_wv == null || _webTex == null) return;
            bool fresh = false;
            try
            {
                fresh = _wv.Call<bool>("beginRead");
                if (fresh && _wvPixels.IsCreated && _wvPixels.Length >= WebW * WebH * 4) _webTex.LoadRawTextureData(_wvPixels);
            }
            catch (Exception ex) { fresh = false; Log("web frame: " + ex.Message); }
            finally { try { _wv.Call("endRead"); } catch (Exception) { } }
            if (fresh) _webTex.Apply(false, false);
            if (Time.unscaledTime < _webNextState) return;
            _webNextState = Time.unscaledTime + 0.25f;
            string[] st;
            try { st = (_wv.Call<string>("state") ?? "").Split('\t'); } catch (Exception) { return; }
            if (st.Length < 7) return;
            if (st.Length > 7 && st[7].Length > 0) // a link on the page to a sign-in page: not followed here
            {
                Toast("Sign-in pages open in the system browser: MetaCodex keeps no sign-in sessions", "info");
                OpenSystemBrowser(st[7]);
            }
            string url = st[0];
            if (url.Length > 0 && url != _webCurrent && !url.StartsWith("data:") && url != "about:blank") { _webCurrent = url; _webOnStart = false; _webUrl?.Set(url); }
            int.TryParse(st[2], out int progress);
            _webTitle?.Set((st[5] == "1" ? $"loading {progress} % · " : "") + (_webOnStart ? "start page" : st[1]));
            _webBack?.SetEnabled(st[3] == "1");
            _webFwd?.SetEnabled(st[4] == "1");
            if (!_webError.StartsWith("webview:")) _webError = st[6];
            _webNote?.Set(_webError.Length > 0 ? "⚠ " + _webError : "");
#endif
        }

        /// Self-tests: the page the window has, the last touch sent to the page, a panel point in page pixels.
        public string WebUrlForTest => _webCurrent;
        public bool WebOnStartForTest => _webOnStart;
        public (int action, Vector2 page) WebLastTouchForTest => _webLastTouch;
        public Rect WebPageRectForTest => _webPage != null ? _webPage.Rect : default;
        public Texture2D WebTextureForTest => _webTex;
    }
}
