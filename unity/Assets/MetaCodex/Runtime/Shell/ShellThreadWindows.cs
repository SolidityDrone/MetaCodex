using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Several THREAD windows (owner 2026-11-14, docs/thread-view.md "Thread windows and groups"). Each window is a
    /// ThreadWindow: its panel ("thread", then "thread-2", "thread-3" ...) and all the state that was one set of fields
    /// before (the thread, its timeline, the composer, the approvals, the attach row ...). The old field names are
    /// properties of the CURRENT window `_tw`, so the thread code reads as before:
    /// - outside a scope `_tw` is the FOCUSED window (the last one the user touched or opened): voice, attachments, the
    ///   session context and the clipboard mean that one by "the open thread";
    /// - `using (In(w))` makes `w` current for a block (the per-frame tick of each window, the build, OpenThreadIn);
    /// - UI callbacks run in their window: a press on a window focuses it first (UiInputRouter.Touched), and the
    ///   composer's submit, SEND, STOP and the approvals are bound to their window at build time (Bind);
    /// - async answers (MQH) are bound to the window that asked (Bind): a window closed meanwhile drops them.
    public sealed partial class MetaCodexShell
    {
        public const int MaxThreadWindows = 6;
        public const float ThreadH = 0.66f;

        private sealed class ThreadWindow
        {
            public readonly string Id;
            public readonly int Number;
            public UiPanel Panel;
            public bool Dead;                 // closed for good (an extra window): late callbacks are dropped
            public ThreadGroup Group;         // the group it is in (ShellThreadGroups), or null
            public float FocusedAt;
            public bool Dragging;             // its title bar is held (drop onto a window or group: ShellThreadGroups)
            public bool Gliding;              // FIXED: eases to GlideTo (leaving a group by UNGROUP)
            public Vector3 GlidePos;
            public Quaternion GlideRot;
            public UiButton CloseButton;
            public ThreadWindow(string id, int number) { Id = id; Number = number; }

            public string threadOpen;
            public string threadOpenProject;
            public JObject threadInfo;
            public JArray interactions = new JArray();
            public long tlMaxSeq = -1;
            public float nextThreadPoll;
            public UiLabel thTitle;
            public UiLabel thMeta;
            public UiLabel thAsk;
            public UiTextField thComposer;
            public readonly List<UiButton> thAskButtons = new List<UiButton>();
            public UiButton thModelChip;
            public UiButton thPermChip;
            public UiButton thWsChip;
            public UiRing thContext;
            public UiButton tlUp;
            public UiButton tlDown;
            public Rect tlArea;
            public JObject thExec;
            public JToken thUsage;
            public JArray tlRaw = new JArray();
            public long tlOpenSeq = -1;
            public float resyncAt = -1f;
            public string viewsScanThread;
            public long viewsScanSeq = -1;
            public bool askShown;
            public ThreadDraft draft;
            public UiChatView thTimeline;
            public UiLabel thStatus;
            public UiLabel thLimits;
            public UiButton thNewBtn;
            public UiButton thMic;
            public ThreadTimeline tlModel = new ThreadTimeline();
            public ThreadPulse thPulse;
            public JObject tlFields = new JObject();
            public string tlTag;
            public bool tlInFlight;
            public float tlSentAt;
            public float nextThreadMeta;
            public float nextStatusTick;
            public float panelPulseUntil;
            public UiDragGrip thGrip;
            public float thComposerMax;
            public float thComposerWant;
            public float thComposerApplied;
            public float thChatApplied;
            public UiEdgeHandle thHandle;
            public readonly List<UiButton> attChips = new List<UiButton>();
            public UiButton attMore;
            public UiButton attMenuFiles;
            public UiButton attMenuCapture;
            public UiButton attMenuClose;
            public UiButton thPlus;
            public UiLabel attHint;
            public bool attMenuOpen;
            public bool amOpen;
            public string amMode;
            public string amDir = "/root";
            public readonly HashSet<string> amPicked = new HashSet<string>();
            public UiLabel amTitle;
            public UiLabel amInfo;
            public UiList amList;
            public UiButton amUp;
            public UiButton amAttach;
            public UiButton amCancel;
            public Rect amPreviewArea;
            public GameObject amPreview;
            public Texture2D amTex;
            public readonly List<UiElement> amElements = new List<UiElement>();
            public UiButton thSendNow;
            public Rect thStatusFull;
            public (string id, string reason) thWait;
        }

        // ---- the per-window state: properties of the current window (they were fields before)

        private string _threadOpen { get => _tw.threadOpen; set => _tw.threadOpen = value; }
        private string _threadOpenProject { get => _tw.threadOpenProject; set => _tw.threadOpenProject = value; }
        private JObject _threadInfo { get => _tw.threadInfo; set => _tw.threadInfo = value; }
        private JArray _interactions { get => _tw.interactions; set => _tw.interactions = value; }
        private long _tlMaxSeq { get => _tw.tlMaxSeq; set => _tw.tlMaxSeq = value; }
        private float _nextThreadPoll { get => _tw.nextThreadPoll; set => _tw.nextThreadPoll = value; }
        private UiLabel _thTitle { get => _tw.thTitle; set => _tw.thTitle = value; }
        private UiLabel _thMeta { get => _tw.thMeta; set => _tw.thMeta = value; }
        private UiLabel _thAsk { get => _tw.thAsk; set => _tw.thAsk = value; }
        private UiTextField _thComposer { get => _tw.thComposer; set => _tw.thComposer = value; }
        private List<UiButton> _thAskButtons => _tw.thAskButtons;
        private UiButton _thModelChip { get => _tw.thModelChip; set => _tw.thModelChip = value; }
        private UiButton _thPermChip { get => _tw.thPermChip; set => _tw.thPermChip = value; }
        private UiButton _thWsChip { get => _tw.thWsChip; set => _tw.thWsChip = value; }
        private UiRing _thContext { get => _tw.thContext; set => _tw.thContext = value; }
        private UiButton _tlUp { get => _tw.tlUp; set => _tw.tlUp = value; }
        private UiButton _tlDown { get => _tw.tlDown; set => _tw.tlDown = value; }
        private Rect _tlArea { get => _tw.tlArea; set => _tw.tlArea = value; }
        private JObject _thExec { get => _tw.thExec; set => _tw.thExec = value; }
        private JToken _thUsage { get => _tw.thUsage; set => _tw.thUsage = value; }
        private JArray _tlRaw { get => _tw.tlRaw; set => _tw.tlRaw = value; }
        private long _tlOpenSeq { get => _tw.tlOpenSeq; set => _tw.tlOpenSeq = value; }
        private float _resyncAt { get => _tw.resyncAt; set => _tw.resyncAt = value; }
        private string _viewsScanThread { get => _tw.viewsScanThread; set => _tw.viewsScanThread = value; }
        private long _viewsScanSeq { get => _tw.viewsScanSeq; set => _tw.viewsScanSeq = value; }
        private bool _askShown { get => _tw.askShown; set => _tw.askShown = value; }
        private ThreadDraft _draft { get => _tw.draft; set => _tw.draft = value; }
        private UiChatView _thTimeline { get => _tw.thTimeline; set => _tw.thTimeline = value; }
        private UiLabel _thStatus { get => _tw.thStatus; set => _tw.thStatus = value; }
        private UiLabel _thLimits { get => _tw.thLimits; set => _tw.thLimits = value; }
        private UiButton _thNewBtn { get => _tw.thNewBtn; set => _tw.thNewBtn = value; }
        private UiButton _thMic { get => _tw.thMic; set => _tw.thMic = value; }
        private ThreadTimeline _tlModel { get => _tw.tlModel; set => _tw.tlModel = value; }
        private ThreadPulse _thPulse { get => _tw.thPulse; set => _tw.thPulse = value; }
        private JObject _tlFields { get => _tw.tlFields; set => _tw.tlFields = value; }
        private string _tlTag { get => _tw.tlTag; set => _tw.tlTag = value; }
        private bool _tlInFlight { get => _tw.tlInFlight; set => _tw.tlInFlight = value; }
        private float _tlSentAt { get => _tw.tlSentAt; set => _tw.tlSentAt = value; }
        private float _nextThreadMeta { get => _tw.nextThreadMeta; set => _tw.nextThreadMeta = value; }
        private float _nextStatusTick { get => _tw.nextStatusTick; set => _tw.nextStatusTick = value; }
        private float _panelPulseUntil { get => _tw.panelPulseUntil; set => _tw.panelPulseUntil = value; }
        private UiDragGrip _thGrip { get => _tw.thGrip; set => _tw.thGrip = value; }
        private float _thComposerMax { get => _tw.thComposerMax; set => _tw.thComposerMax = value; }
        private float _thComposerWant { get => _tw.thComposerWant; set => _tw.thComposerWant = value; }
        private float _thComposerApplied { get => _tw.thComposerApplied; set => _tw.thComposerApplied = value; }
        private float _thChatApplied { get => _tw.thChatApplied; set => _tw.thChatApplied = value; }
        private UiEdgeHandle _thHandle { get => _tw.thHandle; set => _tw.thHandle = value; }
        private List<UiButton> _attChips => _tw.attChips;
        private UiButton _attMore { get => _tw.attMore; set => _tw.attMore = value; }
        private UiButton _attMenuFiles { get => _tw.attMenuFiles; set => _tw.attMenuFiles = value; }
        private UiButton _attMenuCapture { get => _tw.attMenuCapture; set => _tw.attMenuCapture = value; }
        private UiButton _attMenuClose { get => _tw.attMenuClose; set => _tw.attMenuClose = value; }
        private UiButton _thPlus { get => _tw.thPlus; set => _tw.thPlus = value; }
        private UiLabel _attHint { get => _tw.attHint; set => _tw.attHint = value; }
        private bool _attMenuOpen { get => _tw.attMenuOpen; set => _tw.attMenuOpen = value; }
        private bool _amOpen { get => _tw.amOpen; set => _tw.amOpen = value; }
        private string _amMode { get => _tw.amMode; set => _tw.amMode = value; }
        private string _amDir { get => _tw.amDir; set => _tw.amDir = value; }
        private HashSet<string> _amPicked => _tw.amPicked;
        private UiLabel _amTitle { get => _tw.amTitle; set => _tw.amTitle = value; }
        private UiLabel _amInfo { get => _tw.amInfo; set => _tw.amInfo = value; }
        private UiList _amList { get => _tw.amList; set => _tw.amList = value; }
        private UiButton _amUp { get => _tw.amUp; set => _tw.amUp = value; }
        private UiButton _amAttach { get => _tw.amAttach; set => _tw.amAttach = value; }
        private UiButton _amCancel { get => _tw.amCancel; set => _tw.amCancel = value; }
        private Rect _amPreviewArea { get => _tw.amPreviewArea; set => _tw.amPreviewArea = value; }
        private GameObject _amPreview { get => _tw.amPreview; set => _tw.amPreview = value; }
        private Texture2D _amTex { get => _tw.amTex; set => _tw.amTex = value; }
        private List<UiElement> _amElements => _tw.amElements;
        private UiButton _thSendNow { get => _tw.thSendNow; set => _tw.thSendNow = value; }
        private Rect _thStatusFull { get => _tw.thStatusFull; set => _tw.thStatusFull = value; }
        private (string id, string reason) _thWait { get => _tw.thWait; set => _tw.thWait = value; }

        // ---- the windows, the current one, the focused one

        private readonly ThreadWindow _twFirst = new ThreadWindow("thread", 1);
        private ThreadWindow _twCur, _twFocused;
        private int _twDepth;
        private List<ThreadWindow> _twList;

        /// The current window (the focused one outside a scope).
        private ThreadWindow _tw { get => _twCur ?? _twFocused ?? _twFirst; set => _twCur = value; }
        private ThreadWindow _twFocus => _twFocused ?? _twFirst;
        /// Every window that exists ("thread" always; extra ones while open).
        private List<ThreadWindow> Tws => _twList ?? (_twList = new List<ThreadWindow> { _twFirst });

        private struct TwScope : IDisposable
        {
            private readonly MetaCodexShell _s;
            private readonly ThreadWindow _prev;
            public TwScope(MetaCodexShell s, ThreadWindow prev) { _s = s; _prev = prev; }
            public void Dispose()
            {
                if (_s == null) return;
                _s._twDepth--;
                _s._twCur = _s._twDepth > 0 ? _prev : null; // back to the focused window outside every scope
            }
        }

        /// `using (In(w)) { ... }`: w is the current window in the block (null: the current one stays).
        private TwScope In(ThreadWindow w)
        {
            var prev = _tw;
            _twDepth++;
            _twCur = w ?? prev;
            return new TwScope(this, prev);
        }

        /// The action runs in the window current now (a callback for later: a button, an MQH answer); dropped when
        /// that window was closed for good meanwhile.
        private Action Bind(Action a)
        {
            var w = _tw;
            return () => { if (w.Dead) return; using (In(w)) a(); };
        }

        private Action<T> Bind<T>(Action<T> a)
        {
            var w = _tw;
            return x => { if (w.Dead) return; using (In(w)) a(x); };
        }

        /// The thread window a panel belongs to: its own panel, or the sidebar (its owner's); null for others.
        private ThreadWindow WindowOfPanel(UiPanel p)
        {
            if (p == null) return null;
            if (p == _sb) return _sbOwner;
            foreach (var w in Tws) if (w.Panel == p) return w;
            return null;
        }

        private static bool WinOpen(ThreadWindow w) => w != null && !w.Dead && w.Panel != null && w.Panel.IsOpen;

        /// The open windows, the focused one first.
        private IEnumerable<ThreadWindow> OpenWindows => Tws.Where(WinOpen).OrderByDescending(w => w == _twFocus).ThenByDescending(w => w.FocusedAt);

        /// The open window that shows this thread (null: none).
        private ThreadWindow OpenWindowShowing(string threadId)
        {
            if (threadId == null) return null;
            foreach (var w in Tws) if (WinOpen(w) && w.threadOpen == threadId) return w;
            return null;
        }

        /// A thread panel id: "thread", "thread-2" ... (not "thread-new" or the sidebar "thread-side").
        public static bool IsThreadWindowId(string id) => id == "thread" || id != null && id.Length > 7 && id.StartsWith("thread-", StringComparison.Ordinal) && char.IsDigit(id[7]);

        /// The current window's panel, and whether it is open.
        private UiPanel ThreadPanel => _tw.Panel;
        private bool ThreadWinOpen => WinOpen(_tw);

        /// The focused window becomes `w` (a press on it, a thread opened in it). Outside a scope it is the current one too.
        private void FocusWindow(ThreadWindow w)
        {
            if (w == null || w.Dead) return;
            var was = _twFocus;
            _twFocused = w;
            w.FocusedAt = Time.unscaledTime;
            if (_twDepth == 0) _twCur = null;
            if (was != w) { RefreshWindowTitles(); UpdateSidebarVoice(); UpdateThreadWindowVoice(); }
        }

        /// Window titles: THREAD, THREAD 2 ...; with more than one open, the focused one is marked (voice and the
        /// session context mean that one).
        private void RefreshWindowTitles()
        {
            int open = Tws.Count(WinOpen);
            foreach (var w in Tws)
            {
                if (w.Panel == null || w.Dead) continue;
                string name = w.Number == 1 ? "Thread" : "Thread " + w.Number;
                w.Panel.SetTitle(open > 1 && w == _twFocus ? "● " + name : name);
            }
        }

        public int ThreadWindowCount => Tws.Count(WinOpen);
        public string FocusedThreadWindowId => _twFocus.Id;
        /// Self-tests: the thread shown in each open window, by window id.
        public Dictionary<string, string> ThreadWindowsForTest => Tws.Where(WinOpen).ToDictionary(w => w.Id, w => w.threadOpen ?? (w.draft != null ? "draft" : ""));
        public void FocusThreadWindowForTest(string id) { var w = Tws.FirstOrDefault(x => x.Id == id); if (w != null) FocusWindow(w); }
        public UiPanel ThreadWindowPanelForTest(string id) => Tws.FirstOrDefault(x => x.Id == id)?.Panel;

        // ---- which window a thread opens in

        /// A thread or draft about to open, not shown yet: the focused window when it is closed or empty, THREAD when it
        /// is closed, else a new window (at most MaxThreadWindows; then the focused one).
        private ThreadWindow PickWindowForOpen()
        {
            var f = _twFocus;
            if (!WinOpen(f) && !f.Dead) return f;
            if (f.threadOpen == null && f.draft == null) return f;
            if (!WinOpen(_twFirst)) return _twFirst;
            if (Tws.Count < MaxThreadWindows) return NewThreadWindow();
            Toast($"{MaxThreadWindows} thread windows are open: it opens in the focused one", "info");
            return f;
        }

        private ThreadWindow NewThreadWindow()
        {
            int n = 2;
            while (Tws.Any(x => x.Number == n)) n++;
            var w = new ThreadWindow("thread-" + n, n);
            Tws.Add(w);
            BuildThreadWindow(w);
            if (w.Panel != null && Settings != null) w.Panel.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
            return w;
        }

        /// The window a new window opens beside (the focused one, while it is open).
        private UiPanel _twOpener;

        /// Opens (or shows again) the current window's panel: where it was, or beside the window it came from.
        private void ShowThreadWindow()
        {
            var w = _tw;
            if (w.Panel == null) return;
            if (w.Group != null) { if (!w.Group.Panel.IsOpen) w.Group.Panel.Open(); w.Panel.Ping(); return; }
            _threadAliasOff = true; // its own id ("thread" is also the focused window's alias in OpenPanel)
            try { OpenPanel(w.Id); } finally { _threadAliasOff = false; }
            RefreshWindowTitles();
        }

        private bool _threadAliasOff;

        /// The focused thread window (or its group) when it is open: what opens "from the thread" opens beside it.
        private UiPanel FocusedThreadAnchor() => WinOpen(_twFocus) ? (_twFocus.Group?.Panel ?? _twFocus.Panel) : null;

        /// After a resume: every open window's thread as MQH has it now.
        public void ResyncAllThreads()
        {
            bool any = false;
            foreach (var w in Tws.Where(WinOpen).ToList()) using (In(w)) { ResyncThread(); any = true; }
            if (!any) ResyncThread();
        }

        /// A window closed (its ✕, CLOSE ALL, a layout): the draft goes, the sidebar folds when it was its; an extra
        /// window goes for good (the thread stays in MQH). The focus moves to the window used last.
        private void OnThreadWindowClosed(ThreadWindow w)
        {
            using (In(w))
            {
                if (_sbOwner == w && _sb != null && _sb.IsOpen) _sb.Close();
                CloseAttachModal();
                SetAttachMenu(false);
                DiscardDraft();
            }
            if (w.Group != null) RemoveFromGroup(w, false);
            _pulledOut.Remove(w.threadOpen ?? "");
            if (w != _twFirst)
            {
                w.Dead = true;
                Tws.Remove(w);
                if (_panels.TryGetValue(w.Id, out var p) && p == w.Panel) _panels.Remove(w.Id);
                _placed.Remove(w.Id);
                if (Floating && w.Panel != null) _slots.Remove(w.Panel.transform);
                if (w.Panel != null) { Router?.Unregister(w.Panel); w.Panel.CloseAndDestroy(); }
                if (_sbOwner == w) _sbOwner = _twFirst;
            }
            if (_twFocus == w || w.Dead)
            {
                var next = Tws.Where(x => x != w && WinOpen(x)).OrderByDescending(x => x.FocusedAt).FirstOrDefault() ?? _twFirst;
                _twFocused = next;
                if (_twDepth == 0) _twCur = null;
            }
            RefreshWindowTitles();
            UpdateSidebarVoice();
            UpdateThreadWindowVoice();
            UpdateSidebarHandle();
        }

        /// Every frame, before the layout: each window's drag (drop onto a window or group), the groups' grids, eased
        /// scales and glides (ShellThreadGroups).
        private void TickThreadWindows(float dt)
        {
            if (Router == null) return;
            if (Router.Touched == null) Router.Touched = OnPanelTouched;
            TickThreadGroups(dt);
        }

        /// A press or grab on a panel (UiInputRouter.Touched, before the element sees it): a thread window, or the
        /// sidebar (its window), becomes the focused one, so what the press does is for that window.
        private void OnPanelTouched(UiPanel p)
        {
            var w = WindowOfPanel(p);
            if (w != null) FocusWindow(w);
        }
    }
}
