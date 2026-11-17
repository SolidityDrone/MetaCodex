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
    /// WEB: the in-app browser windows (owner 2026-11-10: "it's boring to have the system browser turn the app off";
    /// Horizon OS cannot show its browser beside an immersive app: even its Web Tasks send the app to the background).
    /// On the headset each page is Android's own WebView (MetaCodexWebView.java): the content of a hardware-accelerated
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
    /// browser.open) opens in a web window; ⇗ sends the page to Horizon OS's browser (sign-in pages that refuse embedded browsers).
    /// A page's dropdown (<select>) and its date, time and colour pickers open as the app's chooser over the page (the
    /// engine's own ones are dialogs the headset never shows): see "a page's dropdowns and pickers" below.
    /// Remote debugging is on: chrome://inspect on the PC (adb) shows the page's console. In the Editor there is no
    /// WebView: the window works, the page area says so.
    ///
    /// SEVERAL WINDOWS (owner 2026-11-17: "if I open a video and then the code editor, they fight over the window"): up to
    /// WebMaxWindows web windows, each with its own page (its own WebView, texture, toolbar, dialogs, chooser, full
    /// screen). The first is "web" (layouts, tests, voice and the wrist tile name it) and stays when closed (its page
    /// paused, as before); ❐ NEW WINDOW opens "web-2" or "web-3" beside the window it was pressed on, on its start page;
    /// closing one of those destroys its WebView (its memory comes back). A page the app opens (OpenBrowser) goes to the
    /// open window touched last that does not show the code editor (none: the first window, or a new one beside the
    /// editor's); GO and the address field load in their own window. The code editor (ShellIde) always has a window of
    /// its own. The hardware keyboard types into the window touched last. A closed window does nothing per frame: no
    /// poll, no copy, its page paused with the WebView's own onPause (never the process-wide pauseTimers: that would
    /// freeze the other windows' pages). Tabs come later: a window shows one WebPage; a page could move to another window.
    public sealed partial class MetaCodexShell
    {
        public const int WebW = 1280, WebH = 800, WebLargeW = 1600, WebLargeH = 1000;
        public const float WebLargeScale = 1.25f;
        /// Web windows at once at most (each page has its own engine and buffers: memory).
        public const int WebMaxWindows = 3;
        /// The first web window's panel id; the others are "web-2", "web-3".
        public const string WebFirstId = "web";
        private static readonly Color32 WebDarkFill = new Color32(6, 16, 28, 255);

        /// A page: its engine (the WebView), its picture (the texture), its address and state, its dialog. A window shows
        /// one page; with tabs a window would hold several and a page could move to another window, so nothing here
        /// knows its window.
        private sealed class WebPage
        {
            public Texture2D Tex;
            public int W = WebW, H = WebH;      // the page's size now (WebW x WebH, or the large one)
            public bool Large;
            public string Current = "", Error = "", Mode = "";
            public bool OnStart, Loading, Full, Dark, ReadState, Swallow;
            public int JsShown, JsAnswered, JsDismissed;
            public UiPanel Dialog;               // the page's alert or confirm while it shows
            public float NextState;
            public Vector2 LastHover = new Vector2(-1f, -1f);
#if UNITY_ANDROID && !UNITY_EDITOR
            public AndroidJavaObject Wv;
            public Unity.Collections.NativeArray<byte> Pixels;  // a view of the Java buffer (no copy, no unsafe code)
            public readonly jvalue[] Args = new jvalue[3];      // the raw JNI calls' arguments: one array per page
#endif
        }

        /// A web window: its panel, toolbar and page area, and the page it shows.
        private sealed class WebWindow
        {
            public string Id, Title;
            public UiPanel Panel;
            public UiTextField Url;
            public UiLabel TitleLabel, Note;
            public UiButton Back, Fwd, Reload, Go, ZoomOut, ZoomIn, ExitFull;
            public UiProgress Progress;
            public WebPageArea Area;
            public string TitleShown, NoteShown;
            public int ProgressShown = -1;
            public float TouchedAt;              // its last press (or opening): pages the app opens go to the one touched last
            public readonly WebPage Page = new WebPage();
            public bool Extra => Id != WebFirstId;
        }

        private readonly List<WebWindow> _webWins = new List<WebWindow>(WebMaxWindows); // the first window first
        private WebWindow _webLast;              // the window touched last
        private UiPanel _webOpener;              // while a new window opens: the window it opens beside (ContextOpener)
        /// ❐ NEW WINDOW pressed again within this many seconds of the last press (on any web window) does nothing.
        public const float WebNewDebounce = 0.8f;
        private float _webNewAt = -10f;          // the last ❐ press that opened (or tried to open) a window
        private Mesh _webQuad;                   // the page's quad: one mesh for every window
        private (int action, Vector2 page) _webLastTouch = (-1, default);
        private (int id, bool ok) _webLastJsAnswer;
#if UNITY_ANDROID && !UNITY_EDITOR
        // the calls made every frame (poll, endRead) or per pointer move (touch, hover, keys): raw JNI with the method ids
        // looked up once (one Java class: every page shares them) and each page's own argument array, so they allocate
        // nothing (AndroidJavaObject.Call builds a signature string and boxes its arguments on every call)
        private static IntPtr WvPoll, WvEndRead, WvTouch, WvHover, WvKeyMeta;
        private static readonly jvalue[] WvNoArgs = new jvalue[0];
#endif

        /// A window's page area: a pointer on it, in page pixels (top-left origin), goes to the window's page.
        private sealed class WebPageArea : UiElement
        {
            private readonly MetaCodexShell _shell;
            private readonly WebWindow _win;
            public override bool Interactive => true;
            public WebPageArea(MetaCodexShell shell, WebWindow win) { _shell = shell; _win = win; }
            protected override void Build() { }
            public Vector2 ToPage(Vector2 p) => new Vector2(Mathf.Clamp01((p.x - Rect.x) / Rect.width) * _win.Page.W, Mathf.Clamp01((p.y - Rect.y) / Rect.height) * _win.Page.H);
            public override void OnPress(UiPointerEvent e) => _shell.WebTouch(_win, 0, ToPage(e.Point));
            public override void OnDrag(UiPointerEvent e) => _shell.WebTouch(_win, 2, ToPage(e.Point));
            public override void OnRelease(UiPointerEvent e, bool inside) => _shell.WebTouch(_win, 1, ToPage(e.Point));
            public override void OnHover(UiPointerEvent e) => _shell.WebHover(_win, ToPage(e.Point));
        }

        // ---- the windows

        /// A web window's panel id: "web", "web-2", "web-3" (StaysFlat, placement, voice).
        public static bool IsWebPanelId(string id) =>
            id == WebFirstId || id != null && id.Length == 5 && id.StartsWith("web-", StringComparison.Ordinal) && char.IsDigit(id[4]);

        private WebWindow WebFirst => _webWins.Count > 0 ? _webWins[0] : null;

        /// The windows exist: a page or a window asked for during the staged start completes it first (as OpenPanel does).
        private bool WebReady()
        {
            if (_webWins.Count == 0 && _staging) CompleteStartNow();
            return _webWins.Count > 0;
        }

        /// The window touched last (the first one before any): the self-tests' window.
        private WebWindow WebCur => _webLast ?? WebFirst;

        private static bool WebIsOpen(WebWindow w) => w != null && w.Panel != null && w.Panel.IsOpen;

        private WebWindow WebWindowOf(UiPanel p)
        {
            if (p == null) return null;
            foreach (var w in _webWins) if (w.Panel == p) return w;
            return null;
        }

        private WebWindow WebWindowById(string id)
        {
            foreach (var w in _webWins) if (w.Id == id) return w;
            return null;
        }

        /// The open window touched last (`noIde`: not the one that shows the code editor), or null. No allocation.
        private WebWindow WebLastOpen(bool noIde)
        {
            WebWindow best = null;
            foreach (var w in _webWins)
                if (WebIsOpen(w) && (!noIde || !WebShowsIde(w)) && (best == null || w.TouchedAt > best.TouchedAt)) best = w;
            return best;
        }

        private void WebMarkTouched(WebWindow w)
        {
            w.TouchedAt = Time.unscaledTime;
            _webLast = w;
        }

        /// UiInputRouter.Touched (a press or grab on any panel, before its element sees it): a thread window gets the
        /// focus (ShellThreadWindows); a web window becomes the one touched last, and when a page has the hardware
        /// keyboard, the keys go to this window's page now.
        private void OnAnyPanelTouched(UiPanel p)
        {
            OnPanelTouched(p);
            if (p == null || !IsWebPanelId(p.Id)) return;
            var w = WebWindowOf(p);
            if (w == null) return;
            WebMarkTouched(w);
            if (_webKeys) _webKeysWin = w;
        }

        /// The web windows' panels (BuildPanels; again on an accent change: the open extra windows too, with their pages).
        private void BuildWeb()
        {
            WebChoiceEnd(true); // built again (accent colour): an open chooser goes, the page keeps its value
            if (_webWins.Count == 0) _webWins.Add(new WebWindow { Id = WebFirstId, Title = "WEB" });
            foreach (var w in _webWins) BuildWebWindow(w);
            if (_webLast == null) _webLast = _webWins[0];
        }

        private void BuildWebWindow(WebWindow w)
        {
            var t = UiTheme.Current;
            var pg = w.Page;
            var p = Panel<UiPanel>(w.Id, w.Title, 0.80f, 0.61f);
            w.Panel = p;
            var c = p.Content;
            float bh = t.ButtonHeight, sq = 0.036f, x = c.x;
            UiButton B(string text, Action act, float bw, UiButtonStyle style = UiButtonStyle.Normal)
            {
                var b = p.Add(new UiButton(text, act, style) { Rect = new Rect(x, c.y, bw, bh) });
                x += bw + t.Gap;
                return b;
            }
            w.Back = B("◀", () => WebCall(w, "back"), sq);       // in full screen: leaves it
            w.Fwd = B("▶", () => WebCall(w, "forward"), sq);
            w.Reload = B("⟳", () => WebCall(w, w.Page.Loading ? "stop" : "reload"), sq); // ✕ while a page loads: stops it
            float right = 0.056f + (sq + t.Gap) * 9f; // GO, then ⌨ ⎘ ⏎ ⌫ - + ☰ ❐ ⇗
            float urlX = x;
            w.Url = p.Add(new UiTextField("", "", u => WebGo(w, u), "an address, or words to search") { Rect = new Rect(urlX, c.y, c.xMax - right - urlX - t.Gap, bh) });
            x = c.xMax - right;
            w.Go = B("GO", () => WebGo(w, w.Url.Text), 0.056f, UiButtonStyle.Primary);
            // a page in full screen: one button where the address and GO are
            w.ExitFull = p.Add(new UiButton("EXIT FULL SCREEN", () => WebCall(w, "exitFullscreen"), UiButtonStyle.Primary) { Rect = new Rect(urlX, c.y, c.xMax - right + 0.056f - urlX, bh) });
            w.ExitFull.SetVisible(false);
            B("⌨", () => WebType(w), sq);
            B("⎘", () => WebPaste(w), sq);          // the clipboard (terminals and CLIP fill it) into the page's focused field
            B("⏎", () => WebKey(w, 66), sq);  // KEYCODE_ENTER
            B("⌫", () => WebKey(w, 67), sq);  // KEYCODE_DEL
            w.ZoomOut = B("-", () => WebCallInt(w, "zoom", -1), sq);
            w.ZoomIn = B("+", () => WebCallInt(w, "zoom", 1), sq);
            B("☰", () => OpenPanel("links"), sq); // bookmarks and local servers, beside this window
            B("❐", () => WebNewWindow(w), sq);    // NEW WINDOW: another web window beside this one
            B("⇗", () => OpenSystemBrowser(w.Page.Current), sq);
            // how far the page has loaded: a line under the toolbar, only while it loads
            w.Progress = p.Add(new UiProgress(0f) { Rect = new Rect(c.x, c.y + bh + 0.0008f, c.width, 0.0022f) });
            w.Progress.SetVisible(false);
            w.ProgressShown = -1;
            float titleY = c.y + bh + t.Gap * 0.5f, top = titleY + 0.016f;
            w.TitleLabel = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, titleY, c.width, 0.016f), Color = t.TextDim });
            w.TitleShown = null;
            float pw = c.width, ph = pw * WebH / WebW;
            if (top + ph > c.yMax) { ph = c.yMax - top; pw = ph * WebW / WebH; }
            var area = new Rect(c.x + (c.width - pw) * 0.5f, top, pw, ph);
            w.Area = p.Add(new WebPageArea(this, w) { Rect = area });
            if (pg.Tex == null) // kept when the panels are rebuilt (accent colour)
            {
                pg.Tex = new Texture2D(pg.W, pg.H, TextureFormat.RGBA32, false, false) { name = "web page", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                WebFillDark(pg);
            }
            AddWebQuad(p, area, pg.Tex);
            // the Editor's hint, a fatal error, or the page that did not open (its text over the darkened page)
            w.Note = p.Add(new UiLabel("", t.BodySize) { Rect = new Rect(area.x + 0.03f, area.y + area.height * 0.25f, area.width - 0.06f, area.height * 0.5f), Align = TextAlignmentOptions.Center, Color = t.TextDim, Wrap = true });
            w.NoteShown = null;
            pg.Full = false;
            pg.Loading = false;
            pg.ReadState = true; // a rebuilt window (accent colour) takes the page's state at once
            WebRefreshNote(w);
            p.Tick += _ => TickWeb(w);
            p.Opened += _ => WebOpened(w);
            p.Closed += _ => WebClosed(w);
            WebApplyScale(w);
        }

        private void WebOpened(WebWindow w)
        {
            WebMarkTouched(w);
            WebEnsure(w);
            WebCallBool(w, "setPaused", false);
            if (string.IsNullOrEmpty(w.Page.Current)) { WebStartPage(w); ProbeLocalServers(); }
        }

        /// A window closed: its keyboard, chooser and dialog go; the first window's page pauses (media, drawing, copying;
        /// it stays for when the window opens again), another window goes for good with its page.
        private void WebClosed(WebWindow w)
        {
            if (_webKeys && _webKeysWin == w) WebTakeKeys(w, false);
            if (_wc != null && _wcWin == w) WebChoiceEnd(true);
            if (w.Extra) WebDestroy(w);
            else WebCallBool(w, "setPaused", true); // the page's media pause; no drawing, no copying while closed
            if (_webLast == w) _webLast = WebLastOpen(false) ?? WebFirst;
        }

        /// An extra window closed: its panel, its page's WebView (its display, buffers and memory) and its texture go.
        private void WebDestroy(WebWindow w)
        {
            var p = w.Panel;
            var pg = w.Page;
            _webWins.Remove(w);
            if (p != null)
            {
                if (_panels.TryGetValue(w.Id, out var q) && q == p) _panels.Remove(w.Id);
                _placed.Remove(w.Id);
                if (Floating) _slots.Remove(p.transform);
                Router?.Unregister(p);
                p.CloseAndDestroy();
            }
            WebFreeEngine(w);
            var tex = pg.Tex;
            pg.Tex = null;
            if (tex != null) After(1f, () => Destroy(tex)); // after the window's closing (its quad shows it until then)
            Log(w.Id + ": closed, its page and its memory freed (" + _webWins.Count + " web window" + (_webWins.Count == 1 ? "" : "s") + " left)");
        }

        /// A page's dialog and its WebView go (its display, buffers, history and memory); the next opening makes a new one.
        private void WebFreeEngine(WebWindow w)
        {
            var pg = w.Page;
            if (pg.Dialog != null) { var d = pg.Dialog; pg.Dialog = null; if (d.IsOpen) d.Close(); }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (pg.Wv != null)
            {
                try { pg.Wv.Call("close"); } catch (Exception ex) { Log(w.Id + " close: " + ex.Message); }
                pg.Wv.Dispose();
                pg.Wv = null;
                pg.Pixels = default;
            }
#endif
        }

        /// The closed first window reused by ❐ NEW WINDOW (owner 2026-11-17: it came back with its old page): a fresh
        /// window, as web-2 and web-3 are. Its WebView goes (a new one is made when it opens: no history, so ◀ never goes
        /// back to the old page), its page state is cleared and its size is the usual one; on opening it shows its start
        /// page (WebOpened). The WEB tile and voice still bring the first window back as it was (WebShow, not this).
        private void WebFreshWindow(WebWindow w)
        {
            var pg = w.Page;
            if (WebIsOpen(w) || w.Extra) return;
            bool had = pg.Current.Length > 0 || !pg.OnStart;
#if UNITY_ANDROID && !UNITY_EDITOR
            had |= pg.Wv != null;
#endif
            WebFreeEngine(w);
            if (pg.Large) WebSetLarge(w, false);
            if (w.Panel != null) w.Panel.BaseScale = Vector3.one * UiScaleNow; // the code editor's larger window: the usual size
            pg.Current = pg.Error = pg.Mode = "";
            pg.OnStart = false;
            pg.Loading = false;
            pg.JsShown = pg.JsAnswered = pg.JsDismissed = 0; // a new engine counts its dialogs from 1 again
            pg.LastHover = new Vector2(-1f, -1f);
            pg.NextState = 0f;
            pg.ReadState = true;
            WebFullscreen(w, false);
            w.Reload?.SetText("⟳");
            w.Progress?.SetVisible(false);
            w.ProgressShown = -1;
            w.Back?.SetEnabled(false);
            w.Fwd?.SetEnabled(false);
            w.Url?.Set("");
            w.TitleShown = null;
            w.TitleLabel?.Set("");
            pg.Dark = false;
            WebFillDark(pg); // not the old page's last frame while the new engine starts
            WebRefreshNote(w);
            if (had) Log(w.Id + ": NEW WINDOW reuses it as a fresh window (its old page and history gone)");
        }

        /// A new extra window ("web-2", "web-3"), made closed: the caller opens it.
        private WebWindow WebMakeWindow(int n)
        {
            var w = new WebWindow { Id = "web-" + n, Title = "WEB " + n };
            _webWins.Add(w);
            BuildWebWindow(w);
            w.Panel.BaseScale = Vector3.one * UiScaleNow;
            return w;
        }

        /// Another window for a page: a free extra slot (made now, still closed), else the first window when it is the
        /// closed one; null when WebMaxWindows are open (a toast says so).
        private WebWindow WebSpare()
        {
            for (int n = 2; n <= WebMaxWindows; n++)
            {
                var x = WebWindowById("web-" + n);
                if (x == null) return WebMakeWindow(n);
                if (!WebIsOpen(x)) return x; // made and not opened (never left so): it is the free one
            }
            var first = WebFirst;
            if (first != null && !WebIsOpen(first)) return first;
            Toast(WebMaxWindows + " web windows are open, the most there can be: close one first", "warn");
            Log("web: no new window (" + WebMaxWindows + " open)");
            return null;
        }

        /// Opens a window (beside `from` when it is placed anew) and makes it the one touched last.
        private void WebShow(WebWindow w, WebWindow from = null)
        {
            if (w?.Panel == null) return;
            _webOpener = from != null && from != w && WebIsOpen(from) ? from.Panel : null;
            try { OpenPanel(w.Id); }
            finally { _webOpener = null; }
            WebMarkTouched(w);
        }

        /// ❐ NEW WINDOW: another web window beside this one, always on its start page (the closed first window, when it is
        /// the spare one, comes as a fresh window: WebFreshWindow).
        private void WebNewWindow(WebWindow from)
        {
            // one press, one window (owner b16: one pinch gave three calls in 0.4 s: two windows and the limit toast)
            float now = Time.unscaledTime;
            if (now - _webNewAt < WebNewDebounce) return;
            _webNewAt = now;
            var w = WebSpare();
            if (w == null) return;
            if (!w.Extra) WebFreshWindow(w);
            WebShow(w, from);
            Log(w.Id + ": a new web window beside " + from.Id);
        }

        /// Where a page the app opens goes: the open window touched last that does not show the code editor; none: the
        /// first window when it does not show the editor (it opens again), else a new window (beside the editor's).
        private WebWindow WebForPage()
        {
            var w = WebLastOpen(true);
            if (w != null) return w;
            var first = WebFirst;
            if (first == null) return null; // before the shell is up
            return !WebShowsIde(first) ? first : WebSpare();
        }

        /// The WEB tile (wrist menu, dock): hides the open window touched last that does not show the code editor; with
        /// none open, shows the first window (a new one when the first shows the editor).
        public void ToggleWebWindow()
        {
            if (!WebReady()) return;
            var w = WebLastOpen(true);
            if (w != null) { ClosePanel(w.Id); return; }
            var first = WebFirst;
            if (first == null) return;
            WebShow(!WebShowsIde(first) ? first : WebSpare() ?? first);
        }

        /// The WEB tile is lit: a web window without the code editor is open (every frame while the menu shows: no allocation).
        public bool WebWindowShown => WebLastOpen(true) != null;

        /// Voice's open_panel and close_panel "web": the window the user means (open: as the WEB tile shows one; close:
        /// the open window touched last). Other panels are themselves.
        private string WebVoicePanel(string id, bool open)
        {
            if (id != WebFirstId) return id;
            var first = WebFirst;
            var w = open ? WebLastOpen(true) ?? (first != null && !WebShowsIde(first) ? first : null) : WebLastOpen(false);
            return w?.Id ?? WebFirstId;
        }

        /// The web window panels opened from WEB go beside (links, clipboard): the open one touched last.
        private UiPanel WebOpenPanel() => WebLastOpen(false)?.Panel;

        // ---- the code editor's window (ShellIde)

        private bool WebShowsIde(WebWindow w) => _ideUrl != null && w != null && w.Page.Current.StartsWith(_ideUrl, StringComparison.Ordinal);

        /// The window that shows the code editor (open or not), or null. No allocation (CODE's light, every frame).
        private WebWindow WebIdeWindow()
        {
            if (_ideUrl == null) return null;
            foreach (var w in _webWins) if (WebShowsIde(w)) return w;
            return null;
        }

        /// The code editor's window is open.
        private bool WebIdeShown => WebIsOpen(WebIdeWindow());

        /// The code editor's own window: the one that shows it, else a new one (a free extra slot first, so the first
        /// window stays the browser); null with a toast when WebMaxWindows are open.
        private UiPanel WebIdePanel() => WebReady() ? (WebIdeWindow() ?? WebSpare())?.Panel : null;

        /// Loads `url` in the window of the panel (opening it): the code editor's page in its window.
        private void WebOpenOn(UiPanel p, string url)
        {
            var w = WebWindowOf(p);
            if (w != null) WebOpenIn(w, WebAddress(url));
        }

        /// Closes the code editor's window when it is open (CODE, STOP). Its server keeps running.
        private void WebCloseIde()
        {
            var w = WebIdeWindow();
            if (WebIsOpen(w)) ClosePanel(w.Id);
        }

        // ---- the page

        /// The page's quad, 1 mm in front of the window, its rows top first (the WebView's order).
        private void AddWebQuad(UiPanel p, Rect area, Texture2D tex)
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
            mpb.SetTexture("_MainTex", tex);
            mr.SetPropertyBlock(mpb);
            p.TagRenderer(mr, Vector3.zero); // the window's slot: the page is cast with it
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        /// The page area in the window's dark colour (at start, and under a page that did not open). Writes into the
        /// texture's own memory: no array.
        private static void WebFillDark(WebPage pg)
        {
            if (pg.Tex == null) return;
            var px = pg.Tex.GetPixelData<Color32>(0);
            for (int i = 0; i < px.Length; i++) px[i] = WebDarkFill;
            pg.Tex.Apply(false, false);
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

        /// Sign-in pages never open in a web window: its WebView keeps cookies, so an account's web session would
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

        /// Opens a page in a web window (every page the app opens comes here; sign-in pages go to the system browser):
        /// the open window touched last that does not show the code editor (WebForPage).
        public void OpenBrowser(string url)
        {
            string u = WebAddress(url);
            if (IsSignInPage(u)) { OpenSystemBrowser(u); return; }
            var w = WebReady() ? WebForPage() : null;
            if (w == null) return;
            WebOpenIn(w, u, WebIsOpen(w) ? null : WebIdeWindow());
        }

        /// Opens a page in a web window at a size: large is 1.25 times the window with a 1600 x 1000 page (code editors:
        /// more lines and columns, the text as sharp); not large is the usual 1280 x 800. OpenBrowser(url) keeps the size.
        public void OpenBrowser(string url, bool large)
        {
            string u = WebAddress(url);
            if (IsSignInPage(u)) { OpenSystemBrowser(u); return; }
            var w = WebReady() ? WebForPage() : null;
            if (w == null) return;
            WebSetLarge(w, large);
            WebOpenIn(w, u, WebIsOpen(w) ? null : WebIdeWindow());
            WebApplyScale(w); // after opening: the window has its large scale at once
        }

        /// GO and the address field: the page in this window.
        private void WebGo(WebWindow w, string typed)
        {
            string u = WebAddress(typed);
            if (IsSignInPage(u)) { OpenSystemBrowser(u); return; }
            WebOpenIn(w, u);
        }

        /// A page in this window (opened beside `from` when it is placed anew; "" only opens it).
        private void WebOpenIn(WebWindow w, string u, WebWindow from = null)
        {
            var pg = w.Page;
            if (u.Length > 0) { pg.Current = u; pg.OnStart = false; } // before it opens: a new window shows no start page first
            WebShow(w, from);
            if (u.Length == 0) return;
            w.Url?.Set(u);
            if (!WebEnsure(w)) { OpenSystemBrowser(u); return; } // no WebView on this device: the system's browser after all
            WebCall(w, "load", u);
        }

        /// The size of the window touched last (large or not): see WebSetLarge(window, large).
        public void WebSetLarge(bool large) { var w = WebCur; if (w != null) WebSetLarge(w, large); }

        /// A window's size (large or not): the page's pixels follow (the WebView gets a display of that size, the
        /// texture is made again), the window's scale is the UI scale times 1.25 while large.
        private void WebSetLarge(WebWindow w, bool large)
        {
            var pg = w.Page;
            bool was = pg.Large;
            pg.Large = large;
            int pw = large ? WebLargeW : WebW, ph = large ? WebLargeH : WebH;
            if (pw != pg.W || ph != pg.H)
            {
                pg.W = pw;
                pg.H = ph;
                if (pg.Tex != null) { pg.Tex.Reinitialize(pw, ph); WebFillDark(pg); }
#if UNITY_ANDROID && !UNITY_EDITOR
                if (pg.Wv != null) try { pg.Wv.Call("resize", pw, ph); } catch (Exception ex) { Log(w.Id + " resize: " + ex.Message); }
#endif
            }
            if (w.Panel != null && was && !large) w.Panel.BaseScale = Vector3.one * UiScaleNow;
            WebApplyScale(w);
        }

        /// While large, the window keeps 1.25 times the UI scale (the shell sets every window back to the UI scale when
        /// it changes). Not large: the window's scale is left alone.
        private void WebApplyScale(WebWindow w)
        {
            if (!w.Page.Large || w.Panel == null) return;
            float s = UiScaleNow * WebLargeScale;
            if (Mathf.Abs(w.Panel.BaseScale.x - s) > 1e-4f) w.Panel.BaseScale = Vector3.one * s;
        }

        /// The page in Horizon OS's browser (the app goes to the background while it shows); no address: the page of the
        /// window touched last.
        public void OpenSystemBrowser(string url)
        {
            string u = WebAddress(string.IsNullOrEmpty(url) ? WebCur?.Page.Current : url);
            if (u.Length == 0) { Toast("No page open", "warn"); return; }
            bool ok = SystemBridge.OpenBrowser(u);
            Toast(ok ? "Opening " + u + " in the system browser" : SystemBridge.LastResult, ok ? "info" : "warn");
        }

        /// A window shows its start page (kept under the old field's name: ShellPanels refreshes the start pages when the
        /// local servers were probed).
        private bool _webOnStart { get { foreach (var w in _webWins) if (w.Page.OnStart) return true; return false; } }

        /// Every window on its start page lists the bookmarks and local servers again.
        private void WebStartPage() { foreach (var w in _webWins) if (w.Page.OnStart) WebStartPage(w); }

        /// The start page: bookmarks and the local servers found, as big links.
        private void WebStartPage(WebWindow w)
        {
            w.Page.OnStart = true;
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
            w.TitleLabel?.Set("start page");
            w.TitleShown = "start page";
            w.Url?.Set("");
            WebCall(w, "loadHtml", sb.ToString(), "about:blank");
        }

        private static string Escape(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// ⌨: what is typed on the system keyboard goes into the page's focused field (tap the field first).
        private void WebType(WebWindow w) => UiKeyboard.Edit("", false, null, text => { if (!string.IsNullOrEmpty(text)) WebCall(w, "text", text); }, "Type into the page");

        /// ⎘ (and Ctrl+V on a hardware keyboard): the clipboard into the page, as a paste the page can take (code
        /// editors) or typed into its focused field.
        private void WebPaste(WebWindow w)
        {
            string clip = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(clip)) { Toast("The clipboard is empty", "info"); return; }
            _webLastPaste = clip;
            WebCall(w, "paste", clip);
        }

        private void WebKey(WebWindow w, int code) => WebCallInt(w, "key", code);

        private void WebTouch(WebWindow w, int action, Vector2 page)
        {
            var pg = w.Page;
            // a press on the page while its chooser is open closes it (the page keeps its value), as a tap beside a
            // dropdown does; that touch goes nowhere, up to its release. A press on another window's page closes it too
            // and goes on to that page.
            if (action == 0)
            {
                pg.Swallow = _wcPanel != null && _wcWin == w;
                if (_wcPanel != null) { WebChoiceEnd(true); if (pg.Swallow) return; }
            }
            else if (pg.Swallow) { if (action == 1) pg.Swallow = false; return; }
            _webLastTouch = (action, page);
            if (action == 0) WebTakeKeys(w, true); // a hardware keyboard types into this page now
#if UNITY_ANDROID && !UNITY_EDITOR
            if (pg.Wv == null || WvTouch == IntPtr.Zero) return;
            pg.Args[0].i = action;
            pg.Args[1].f = page.x;
            pg.Args[2].f = page.y;
            AndroidJNI.CallVoidMethod(pg.Wv.GetRawObject(), WvTouch, pg.Args);
            WebJniOk();
#endif
        }

        private void WebHover(WebWindow w, Vector2 page)
        {
            var pg = w.Page;
            if ((page - pg.LastHover).sqrMagnitude < 4f) return; // only when it moved two pixels
            pg.LastHover = page;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (pg.Wv == null || WvHover == IntPtr.Zero) return;
            pg.Args[0].f = page.x;
            pg.Args[1].f = page.y;
            AndroidJNI.CallVoidMethod(pg.Wv.GetRawObject(), WvHover, pg.Args);
            WebJniOk();
#endif
        }

        /// The page's WebView on the headset, made the first time its window opens (false where there is none: a failure).
        private bool WebEnsure(WebWindow w)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var pg = w.Page;
            if (pg.Error.StartsWith("webview:")) return false; // it failed once: the system browser from now on
            if (pg.Wv != null) return true;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    pg.Wv = new AndroidJavaObject("com.pnncl.metacodex.web.MetaCodexWebView", activity, pg.W, pg.H);
                pg.Wv.Call("setExternal", string.Join("\n", SignInHosts), string.Join("\n", SignInPaths));
                using (var buf = pg.Wv.Call<AndroidJavaObject>("pixels")) pg.Pixels = AndroidJNI.GetDirectByteBuffer(buf.GetRawObject());
                if (WvPoll == IntPtr.Zero) // one class: looked up once, for every page
                {
                    WvPoll = WebMethod(pg.Wv, "poll", "()I");
                    WvEndRead = WebMethod(pg.Wv, "endRead", "()V");
                    WvTouch = WebMethod(pg.Wv, "touch", "(IFF)V");
                    WvHover = WebMethod(pg.Wv, "hover", "(FF)V");
                    WvKeyMeta = WebMethod(pg.Wv, "keyMeta", "(II)V");
                }
                Log(w.Id + ": a page engine made (" + _webWins.Count + " web window" + (_webWins.Count == 1 ? "" : "s") + ")");
                return true;
            }
            catch (Exception ex) { Log(w.Id + ": " + ex.Message); pg.Error = "webview: " + ex.Message; pg.Wv = null; WebRefreshNote(w); return false; }
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

        private IntPtr WebMethod(AndroidJavaObject wv, string name, string signature)
        {
            IntPtr id = AndroidJNI.GetMethodID(wv.GetRawClass(), name, signature);
            if (WebJniOk() && id != IntPtr.Zero) return id;
            Log("web: no method " + name + signature);
            return IntPtr.Zero;
        }
#endif

        private void WebCall(WebWindow w, string method, params object[] args)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var pg = w.Page;
            if (pg.Wv == null && !WebEnsure(w)) return;
            try { pg.Wv.Call(method, args); } catch (Exception ex) { Log(w.Id + " " + method + ": " + ex.Message); }
#endif
        }

        private void WebCallBool(WebWindow w, string method, bool v)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (w.Page.Wv != null) try { w.Page.Wv.Call(method, v); } catch (Exception) { }
#endif
        }

        private void WebCallInt(WebWindow w, string method, int v)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (w.Page.Wv != null) try { w.Page.Wv.Call(method, v); } catch (Exception) { }
#endif
        }

        /// While a window is open, each frame: a new frame of its page into the texture (only when there is one; never
        /// waits: while the copy thread writes, the frame comes next time; nothing allocated), the toolbar from the page's
        /// state when it changed (four times a second at most). A closed window's panel does not tick: nothing at all.
        private void TickWeb(WebWindow w)
        {
            var pg = w.Page;
            WebDialogTick(w);
            if (_webKeysWin == w) WebKeysTick(w);
            WebApplyScale(w);
            if (_wcPanel != null && _wcWin == w) WebChoiceFollow();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (pg.Wv == null || pg.Tex == null || WvPoll == IntPtr.Zero) return;
            IntPtr obj = pg.Wv.GetRawObject();
            int bits = AndroidJNI.CallIntMethod(obj, WvPoll, WvNoArgs);
            if (!WebJniOk()) return;
            if ((bits & 4) != 0) { WebTakeSize(w); return; } // a new page size: its buffer, the texture (once per resize)
            if ((bits & 1) != 0) // a new frame, the buffer locked until endRead
            {
                bool upload = !pg.Dark && pg.Pixels.IsCreated && pg.Pixels.Length >= pg.Tex.width * pg.Tex.height * 4;
                try { if (upload) pg.Tex.LoadRawTextureData(pg.Pixels); }
                catch (Exception ex) { upload = false; Log(w.Id + " frame: " + ex.Message); }
                finally { AndroidJNI.CallVoidMethod(obj, WvEndRead, WvNoArgs); WebJniOk(); }
                if (upload) pg.Tex.Apply(false, false);
            }
            if ((bits & 8) != 0) WebChoiceTake(w); // a page's dropdown or picker opened (or the page it was on is gone)
            if (((bits & 2) == 0 && !pg.ReadState) || Time.unscaledTime < pg.NextState) return;
            pg.NextState = Time.unscaledTime + 0.25f;
            pg.ReadState = false;
            string state;
            try { state = pg.Wv.Call<string>("state"); } catch (Exception) { return; }
            WebApplyState(w, state);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// The WebView has a new page size: a view of its new buffer, the texture at that size, then the old buffer goes.
        private void WebTakeSize(WebWindow w)
        {
            var pg = w.Page;
            try
            {
                int pw = pg.Wv.Call<int>("width"), ph = pg.Wv.Call<int>("height");
                using (var buf = pg.Wv.Call<AndroidJavaObject>("pixels")) pg.Pixels = AndroidJNI.GetDirectByteBuffer(buf.GetRawObject());
                if (pg.Tex.width != pw || pg.Tex.height != ph) { pg.Tex.Reinitialize(pw, ph); WebFillDark(pg); }
                pg.Wv.Call("sizeTaken");
                Log(w.Id + ": page " + pw + "x" + ph);
            }
            catch (Exception ex) { Log(w.Id + " size: " + ex.Message); }
        }
#endif

        private static string WebField(string[] st, int i) => i < st.Length ? st[i] : "";

        /// The page's state (MetaCodexWebView.state(): tab separated) onto its window's toolbar, note and dialogs. Only
        /// when it changed; labels only when their text changes.
        private void WebApplyState(WebWindow w, string state)
        {
            var pg = w.Page;
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
            if (url.Length > 0 && url != pg.Current && !url.StartsWith("data:") && url != "about:blank") { pg.Current = url; pg.OnStart = false; w.Url?.Set(url); }
            bool loading = st[5] == "1";
            if (loading != pg.Loading) { pg.Loading = loading; w.Reload?.SetText(loading ? "✕" : "⟳"); w.Progress?.SetVisible(loading); }
            int.TryParse(st[2], out int progress);
            if (progress != w.ProgressShown) { w.ProgressShown = progress; w.Progress?.Set(progress / 100f); }
            string title = (loading ? "loading · " : "") + (pg.OnStart ? "start page" : st[1]);
            if (title != w.TitleShown) { w.TitleShown = title; w.TitleLabel?.Set(title); }
            w.Back?.SetEnabled(st[3] == "1" || WebField(st, 9) == "1");
            w.Fwd?.SetEnabled(st[4] == "1");
            WebFullscreen(w, WebField(st, 9) == "1");
            string mode = WebField(st, 10);
            if (mode.Length > 0 && mode != pg.Mode)
            {
                pg.Mode = mode;
                Log(mode == "display" ? w.Id + ": capture display (the page renders on its own virtual display)"
                    : w.Id + ": capture " + mode + " (fallback: video may stop after a few frames; logcat tag MetaCodexWeb says why)");
            }
            if (!pg.Error.StartsWith("webview:")) pg.Error = st[6];
            WebRefreshNote(w);
            int.TryParse(WebField(st, 12), out int jsId);
            WebDialog(w, WebField(st, 11), jsId, WebField(st, 13));
        }

        /// A page in full screen (a video's ⛶): EXIT FULL SCREEN where the address and GO are; ◀ leaves it too.
        private void WebFullscreen(WebWindow w, bool on)
        {
            if (on == w.Page.Full) return;
            w.Page.Full = on;
            w.Url?.SetVisible(!on);
            w.Go?.SetVisible(!on);
            w.ExitFull?.SetVisible(on);
            w.ZoomOut?.SetEnabled(!on);
            w.ZoomIn?.SetEnabled(!on);
        }

        /// The note over the page: a page that did not open (why, its address, what to do; the page under it dark and
        /// not updated until the next page starts), a WebView that could not be made, or the Editor's hint.
        private void WebRefreshNote(WebWindow w)
        {
            var pg = w.Page;
            bool fatal = pg.Error.StartsWith("webview:");
            bool failed = !fatal && pg.Error.Length > 0;
            if (failed != pg.Dark) { pg.Dark = failed; if (failed) WebFillDark(pg); }
            string note = fatal ? "⚠ " + pg.Error
                : failed ? "⚠ This page did not open\n" + pg.Error + "\n\n" + pg.Current + "\n\n⟳ tries again · ◀ goes back"
                : Application.isEditor ? "The page shows on the headset (Android's WebView). ⇗ opens it in your browser here." : "";
            if (note == w.NoteShown) return;
            w.NoteShown = note;
            w.Note?.Set(note);
        }

        /// The page's alert or confirm as the app's dialog over its window; OK or CANCEL is the page's answer, a dialog
        /// closed with its ✕ answers CANCEL (the page waits for an answer).
        private void WebDialog(WebWindow w, string kind, int id, string message)
        {
            var pg = w.Page;
            if (kind.Length == 0 || id == 0 || id == pg.JsShown || Router == null) return;
            pg.JsShown = id;
            string text = message.Length > 400 ? message.Substring(0, 400) + "…" : message;
            var dlg = UiModal.Show(Router, Head, "THE PAGE SAYS", text, kind == "confirm" ? new[] { "OK", "CANCEL" } : new[] { "OK" },
                k => WebAnswer(w, id, k == 0), _root, w.Panel);
            // closed by any means (its buttons close it before they answer): no answer by the next frame is CANCEL
            if (dlg == null) return;
            pg.Dialog = dlg;
            dlg.Closed += _ => { pg.JsDismissed = id; if (pg.Dialog == dlg) pg.Dialog = null; };
        }

        private void WebAnswer(WebWindow w, int id, bool ok)
        {
            var pg = w.Page;
            if (pg.JsAnswered == id) return;
            pg.JsAnswered = id;
            _webLastJsAnswer = (id, ok);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (pg.Wv != null) try { pg.Wv.Call("jsAnswer", id, ok); } catch (Exception ex) { Log(w.Id + " jsAnswer: " + ex.Message); }
#endif
        }

        private void WebDialogTick(WebWindow w)
        {
            var pg = w.Page;
            if (pg.JsDismissed == 0) return;
            int id = pg.JsDismissed;
            pg.JsDismissed = 0;
            if (pg.JsAnswered != id) WebAnswer(w, id, false);
        }

        // ---- a hardware keyboard into the page (after a touch on the page, until a terminal or a text field takes the
        // keys); one window's page has it at a time: the window touched last (OnAnyPanelTouched moves it)

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
        private WebWindow _webKeysWin; // the window whose page has the keyboard
        private IsKeyboard _webKb;
        private readonly StringBuilder _webTyped = new StringBuilder(32);
        private IsKey _webRepeatKey;
        private int _webRepeatCode = -1;
        private float _webRepeatAt;
        private string _webLastPaste;
        private (int code, int mods) _webLastKey = (-1, 0);

        /// A page takes the hardware keyboard (a touch on it; already taken: it moves to this page) or gives it back (a
        /// terminal focused, a text field or the terminal's system keyboard opened, the window closed). While a page has
        /// it, the terminal's keyboard input is off, so a key never types in both.
        private void WebTakeKeys(WebWindow w, bool on)
        {
            if (on == _webKeys) { if (on && w != null) _webKeysWin = w; return; }
            var input = app != null ? app.Input : null;
            if (on)
            {
                if (UiKeyboard.Active || w == null) return;
                _webKeys = true;
                _webKeysWin = w;
                if (input != null) { _webTermWasOn = input.enabled; input.enabled = false; }
                if (!_webFocusHooked && app != null) { _webFocusHooked = true; app.FocusChanged += _ => WebTakeKeys(null, false); }
                WebHookKeyboard(IsKeyboard.current);
            }
            else
            {
                _webKeys = false;
                _webKeysWin = null;
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

        /// Each frame while this window's page has the keyboard: the text typed since the last frame (as key events),
        /// the keys pressed (with their modifiers), a held key repeating. No allocation unless something was typed.
        private void WebKeysTick(WebWindow w)
        {
            if (!_webKeys) return;
            var input = app != null ? app.Input : null;
            if (UiKeyboard.Active || (input != null && SystemKeyboard.IsOpenFor(input))) { WebTakeKeys(w, false); return; }
            var kb = IsKeyboard.current;
            WebHookKeyboard(kb);
            if (kb == null || SystemKeyboard.Busy) { _webTyped.Clear(); _webRepeatCode = -1; return; } // the system keyboard's own keys
            if (_webTyped.Length > 0) { WebCall(w, "typeText", _webTyped.ToString()); _webTyped.Clear(); }
            int mods = (kb.ctrlKey.isPressed ? 1 : 0) | (kb.shiftKey.isPressed ? 2 : 0) | (kb.altKey.isPressed ? 4 : 0) | (kb.leftMetaKey.isPressed || kb.rightMetaKey.isPressed ? 8 : 0);
            foreach (var (key, code) in WebNavKeys)
            {
                if (!kb[key].wasPressedThisFrame) continue;
                if (code == WebKeyInsert && mods == 2) { WebPaste(w); continue; } // Shift+Insert
                WebKeyMods(w, code, mods);
                _webRepeatKey = key;
                _webRepeatCode = code;
                _webRepeatAt = Time.unscaledTime + 0.45f;
            }
            if (_webRepeatCode >= 0)
            {
                if (!kb[_webRepeatKey].isPressed) _webRepeatCode = -1;
                else if (Time.unscaledTime >= _webRepeatAt) { WebKeyMods(w, _webRepeatCode, mods); _webRepeatAt = Time.unscaledTime + 0.04f; }
            }
            if ((mods & (1 | 4 | 8)) == 0) return;
            foreach (var (key, code) in WebChordKeys)
            {
                if (!kb[key].wasPressedThisFrame) continue;
                if (code == WebKeyV && (mods & 1) != 0 && (mods & (4 | 8)) == 0) { WebPaste(w); continue; } // Ctrl+V, Ctrl+Shift+V: the clipboard
                WebKeyMods(w, code, mods);
            }
        }

        /// A key with modifiers (bits 1 Ctrl, 2 Shift, 4 Alt, 8 Meta) into the page; raw JNI, nothing allocated.
        private void WebKeyMods(WebWindow w, int code, int mods)
        {
            if (code == WebKeyEscape && mods == 0 && _wcPanel != null && _wcWin == w) { WebChoiceEnd(true); return; } // Esc closes the chooser
            _webLastKey = (code, mods);
#if UNITY_ANDROID && !UNITY_EDITOR
            var pg = w.Page;
            if (pg.Wv == null || WvKeyMeta == IntPtr.Zero) return;
            pg.Args[0].i = code;
            pg.Args[1].i = mods;
            AndroidJNI.CallVoidMethod(pg.Wv.GetRawObject(), WvKeyMeta, pg.Args);
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
        private WebWindow _wcWin; // the window whose page the chooser is for
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
        private void WebChoiceTake(WebWindow w)
        {
            string json;
            try { json = w.Page.Wv.Call<string>("selectPayload"); } catch (Exception ex) { Log(w.Id + " selectPayload: " + ex.Message); return; }
            WebChoiceOpen(w, json);
        }
#endif

        /// A request from a window's page opens the chooser over that window; "" (the page it was on is gone) closes it.
        /// One chooser at a time: another window's open one is cancelled there.
        private void WebChoiceOpen(WebWindow w, string json)
        {
            if (string.IsNullOrEmpty(json)) { if (_wc != null && _wcWin == w) WebChoiceEnd(false); return; }
            var c = ParseWebChoice(json);
            if (c == null) { Log(w.Id + ": a chooser request from the page could not be read"); return; }
            if (_wc != null && _wcWin == w && _wc.Id == c.Id) return;
            WebChoiceEnd(_wcWin != w); // an older one of this page: the WebView keeps only the newest; another window's: cancelled
            if (!WebIsOpen(w) || w.Area == null || Router == null) { WebChoiceCancel(w, c.Id); return; }
            _wc = c;
            _wcWin = w;
            _wcStep = 0;
            _wcDate = "";
            _wcPicked.Clear();
            if (c.Multiple) for (int i = 0; i < c.Options.Count; i++) if (c.Options[i].Selected) _wcPicked.Add(i);
            Log($"{w.Id}: chooser {c.Kind}{(c.Multiple ? " (multiple)" : "")}{(c.Kind == "select" ? ", " + c.Options.Count + " options" : "")}");
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

        /// The chooser's panel, over its web window at the element (once per opening: it may allocate).
        private void WebChoiceBuild()
        {
            var t = UiTheme.Current;
            var c = _wc;
            var win = _wcWin;
            if (c == null || win?.Panel == null || win.Area == null || Router == null) return;
            string kind = WebChoiceKindNow();
            if (kind != "select") WebChoiceValues(kind);
            int n = kind == "select" ? c.Options.Count(o => !o.Hidden) + c.Groups.Count : _wcValues.Count;
            bool search = n > WebChoiceSearchOver;
            int rows = Mathf.Clamp(n, 1, WebChoiceRows);
            float bh = t.ButtonHeight, w = Mathf.Clamp(c.Box.width * win.Area.Rect.width + 0.02f, 0.30f, 0.42f);
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
            var web = _wcWin?.Panel;
            if (p == null || c == null || web == null || _wcWin.Area == null) return;
            var area = _wcWin.Area.Rect;
            float ex = area.x + Mathf.Clamp01(c.Box.x) * area.width;
            float top = area.y + Mathf.Clamp01(c.Box.y) * area.height, bottom = area.y + Mathf.Clamp01(c.Box.yMax) * area.height;
            float left = Mathf.Clamp(ex, 0f, Mathf.Max(0f, web.Width - p.Width));
            float y = bottom + 0.004f + p.Height <= web.Height ? bottom + 0.004f
                : top - 0.004f - p.Height >= 0f ? top - 0.004f - p.Height
                : Mathf.Clamp(bottom, 0f, Mathf.Max(0f, web.Height - p.Height));
            var wt = web.transform;
            var centre = wt.TransformPoint(web.PanelToLocal(new Vector2(left + p.Width * 0.5f, y + p.Height * 0.5f)));
            p.transform.SetPositionAndRotation(centre - wt.forward * WebChoiceGap, wt.rotation); // towards the user
            float rootScale = _root != null ? Mathf.Max(1e-4f, _root.lossyScale.x) : 1f;
            p.BaseScale = Vector3.one * (web.RestLossyScale.x / rootScale);
            _wcWebPos = wt.position;
            _wcWebRot = wt.rotation;
            _wcWebScale = web.BaseScale.x;
        }

        /// Each frame while it is open: its window moved or was scaled, the chooser goes with it (compares only).
        private void WebChoiceFollow()
        {
            var web = _wcWin?.Panel;
            if (web == null) return;
            var wt = web.transform;
            if (wt.position != _wcWebPos || wt.rotation != _wcWebRot || web.BaseScale.x != _wcWebScale) WebChoicePlace();
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
            var win = _wcWin;
            if (c == null) return;
            int id = c.Id;
            UiKeyboard.Edit(c.Value, false, null, typed =>
            {
                if (_wc == null || _wc.Id != id || _wcWin != win) return; // closed meanwhile
                string s = (typed ?? "").Trim();
                if (s == c.Value) return;
                if (!WebChoiceValid(c.Kind, s, c.Min, c.Max, c.Required, out string value, out string why)) { Toast(why, "warn"); return; }
                WebChoiceFinish(value);
            }, "Type a " + WebChoiceNoun(c.Kind) + ", as " + WebChoiceExample(c.Kind));
        }

        private void WebChoiceFinish(string answer)
        {
            var c = _wc;
            var win = _wcWin;
            WebChoiceEnd(false);
            if (c != null && win != null) WebChoiceAnswer(win, c.Id, answer);
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

        /// The chooser closes; `cancel`: without a choice (its page keeps what it had).
        private void WebChoiceEnd(bool cancel)
        {
            var c = _wc;
            var win = _wcWin;
            _wc = null;
            _wcWin = null;
            WebChoiceDropPanel();
            _wcValues.Clear();
            _wcPicked.Clear();
            _wcFilter = "";
            if (cancel && c != null && win != null) WebChoiceCancel(win, c.Id);
        }

        private void WebChoiceAnswer(WebWindow w, int id, string answer)
        {
            _webLastChoice = (id, answer);
#if UNITY_ANDROID && !UNITY_EDITOR
            var wv = w.Page.Wv;
            if (wv == null) return;
            try { if (!wv.Call<bool>("selectAnswer", id, answer)) Log(w.Id + ": the page's chooser was gone (its page changed): nothing written"); }
            catch (Exception ex) { Log(w.Id + " selectAnswer: " + ex.Message); }
#endif
        }

        private void WebChoiceCancel(WebWindow w, int id)
        {
            _webLastChoice = (id, null);
#if UNITY_ANDROID && !UNITY_EDITOR
            var wv = w.Page.Wv;
            if (wv != null) try { wv.Call("selectCancel", id); } catch (Exception ex) { Log(w.Id + " selectCancel: " + ex.Message); }
#endif
        }

        /// Self-tests: a request as the WebView sends it (no WebView in the Editor; for the window touched last), the
        /// chooser open now (its panel, list and SEARCH field), the last answer sent (answer null: cancelled), a search typed.
        public void WebChoiceForTest(string json) { var w = WebCur; if (w != null) WebChoiceOpen(w, json); }
        public UiPanel WebChoicePanelForTest => _wcPanel;
        public UiList WebChoiceListForTest => _wcList;
        public UiTextField WebChoiceSearchFieldForTest => _wcSearch;
        public (int id, string answer) WebLastChoiceForTest => _webLastChoice;
        public void WebChoiceSearchForTest(string s) => WebChoiceFilter(s);

        /// Self-tests (the window touched last, the first one before any): the page it has, the last touch sent to a
        /// page, a panel point in page pixels.
        public string WebUrlForTest => WebCur?.Page.Current ?? "";
        public bool WebOnStartForTest => WebCur != null && WebCur.Page.OnStart;
        public (int action, Vector2 page) WebLastTouchForTest => _webLastTouch;
        public Rect WebPageRectForTest => WebCur?.Area != null ? WebCur.Area.Rect : default;
        public Texture2D WebTextureForTest => WebCur?.Page.Tex;
        /// Self-tests: the page's state as the WebView reports it (no WebView in the Editor), a frame of the window, what
        /// the window shows, the last answer to a page dialog.
        public void WebApplyStateForTest(string state) { var w = WebCur; if (w != null) WebApplyState(w, state); }
        public void WebTickForTest() { var w = WebCur; if (w != null) TickWeb(w); }
        public bool WebFullscreenForTest => WebCur != null && WebCur.Page.Full;
        public bool WebErrorShownForTest => WebCur != null && WebCur.Page.Dark;
        public string WebNoteForTest => WebCur?.NoteShown ?? "";
        public string WebTitleForTest => WebCur?.TitleShown ?? "";
        public (int id, bool ok) WebLastDialogAnswerForTest => _webLastJsAnswer;
        /// Self-tests: the page's size, whether a page has the hardware keyboard, the last key sent, the last paste,
        /// a key into the page as the keyboard would send it.
        public Vector2Int WebPageSizeForTest => WebCur != null ? new Vector2Int(WebCur.Page.W, WebCur.Page.H) : default;
        public bool WebHasKeysForTest => _webKeys;
        public (int code, int mods) WebLastKeyForTest => _webLastKey;
        public string WebLastPasteForTest => _webLastPaste;
        public void WebPasteForTest() { var w = WebCur; if (w != null) WebPaste(w); }
        public void WebTakeKeysForTest(bool on) => WebTakeKeys(WebCur, on);
        /// Self-tests: the web windows there are (the first one first), the one touched last, the one whose page has the
        /// keyboard, ❐ NEW WINDOW pressed on the window touched last.
        public IEnumerable<string> WebWindowIdsForTest => _webWins.Select(w => w.Id);
        public string WebLastWindowForTest => WebCur?.Id;
        public string WebKeysWindowForTest => _webKeysWin?.Id;
        public void WebNewWindowForTest() { var w = WebCur; _webNewAt = -10f; if (w != null) WebNewWindow(w); } // as a separate press (no debounce)
    }
}
