using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The orb's quick list (owner 2026-11-11): under its billboard (TALK, SPEAK, PLACE, LOG), the five threads used last,
    /// one small card each, turned to the user with the billboard: PROJECT · thread title · state. Working: blue, it
    /// pulses; done: green; needs you or waiting: orange; error: red. A tap shows that thread's window, or hides it when it
    /// is the one shown. The rows keep their places while the menu is up (nothing moves under the finger); every text is
    /// cut to its column. docs/ui.md (the orb), QuickThreads (the pure part).
    public sealed partial class MetaCodexShell
    {
        public const float OrbCardH = 0.030f, OrbCardGap = 0.004f;
        private const float OrbCardPulseHz = 0.8f;

        private sealed class OrbCard
        {
            public UiPanel Panel;
            public UiButton Hit;
            public UiLabel Project, Title, Status;
            public int Dot;
            public QuickThread Q;
            public bool Open;
        }

        private readonly List<OrbCard> _orbCards = new List<OrbCard>();
        private readonly List<string> _orbOrder = new List<string>();
        // when the user last used each thread here (opened it, wrote to it), unix ms; kept in a small file of its own
        // (not in the settings: MQH's copy of those would bring an old list back)
        private readonly Dictionary<string, long> _threadUsedAt = new Dictionary<string, long>();
        // threads that answered while their window was not open: their card says DONE · NEW until opened
        private readonly HashSet<string> _threadsDoneUnseen = new HashSet<string>();
        private bool _threadUseLoaded;
        private float _orbCardsTextAt, _orbCardsPollAt;
        private bool _orbCardsPolled;

        private static string ThreadUsePath => Path.Combine(Application.persistentDataPath, "recent-threads.txt");

        private void EnsureOrbCards()
        {
            if (_orbCards.Count > 0) return;
            var t = UiTheme.Current;
            const float w = BillboardW, h = OrbCardH, pad = 0.008f;
            for (int k = 0; k < QuickThreads.Max; k++)
            {
                var p = UiPanel.Create<UiPanel>("orb-thread-" + k, "Orb thread card", w, h, _root, header: false);
                p.Closable = false;
                p.Draggable = false;
                p.LightTransition = true;
                Router.Register(p);
                int idx = k;
                var c = new OrbCard { Panel = p };
                // the whole card is the button; the texts lie on it (they take no touches)
                c.Hit = p.Add(new UiButton("", () => OrbCardTapped(idx), UiButtonStyle.Ghost) { Rect = new Rect(0.002f, 0.002f, w - 0.004f, h - 0.004f) });
                var dot = p.Shape(UiShapeKind.Dot, new Rect(pad, h * 0.5f - 0.0045f, 0.009f, 0.009f), t.Accent, 0f, 0f, 0.003f, 0.6f);
                dot.Center = p.PanelToLocal(new Vector2(pad + 0.0045f, h * 0.5f), -0.0004f);
                c.Dot = p.Shapes.Add(dot);
                // three columns, each cut with … at its edge: PROJECT | title | STATE
                c.Project = p.Add(new UiLabel("", 0.0084f) { Rect = new Rect(0.022f, 0f, 0.050f, h), UseDisplay = true, Color = t.TextDim });
                c.Title = p.Add(new UiLabel("", 0.0102f) { Rect = new Rect(0.075f, 0f, 0.098f, h), Color = t.Text });
                c.Status = p.Add(new UiLabel("", 0.0084f) { Rect = new Rect(0.176f, 0f, w - 0.176f - pad, h), UseDisplay = true, Align = TextAlignmentOptions.MidlineRight, Color = t.TextDim });
                p.gameObject.SetActive(false);
                _orbCards.Add(c);
            }
        }

        /// The billboard opened: the list as it is now (the order is kept until it opens again).
        private void OpenOrbThreads()
        {
            EnsureOrbCards();
            LoadThreadUse();
            _orbOrder.Clear();
            _orbOrder.AddRange(QuickThreads.Order(_threads, _threadUsedAt));
            _orbCardsPolled = false;
            _orbCardsPollAt = _orbClock; // ask MQH now: the rows may be old
            _orbCardsTextAt = _orbClock + 1f;
            RenderOrbCards();
        }

        private void CloseOrbThreads()
        {
            foreach (var c in _orbCards) { c.Q = null; if (c.Panel.IsOpen) c.Panel.Close(); }
        }

        private static Color QuickColor(QuickState s, UiTheme t) =>
            s == QuickState.Working ? t.Accent
            : s == QuickState.Done ? t.Ok
            : s == QuickState.NeedsYou || s == QuickState.Waiting ? t.Warn
            : s == QuickState.Error ? t.Danger
            : t.TextDim;

        /// How lit a card's frame is when it does not pulse: what needs the user stands out, an old answer is quiet;
        /// the card of the window that is shown is lit as selected.
        private static float OrbCardRest(QuickThread q, bool open)
        {
            float s = q.Attention ? 0.42f : q.State == QuickState.Error ? 0.36f : q.State == QuickState.Done ? (q.Fresh ? 0.5f : 0.08f) : 0f;
            return open ? Mathf.Max(s, 0.55f) : s;
        }

        /// The cards' texts and colours from the thread rows now (a second's timers; after a poll; after a tap).
        private void RenderOrbCards()
        {
            if (_orbCards.Count == 0) return;
            var t = UiTheme.Current;
            bool up = _orbMenu != null && _orbMenu.IsOpen;
            long now = NowMs;
            var rows = new List<JObject>();
            foreach (var id in _orbOrder)
            {
                var r = _threads.OfType<JObject>().FirstOrDefault(x => (string)x["id"] == id);
                if (r != null) rows.Add(r);
            }
            for (int k = 0; k < _orbCards.Count; k++)
            {
                var c = _orbCards[k];
                var row = up && k < rows.Count ? rows[k] : null;
                if (row == null) { c.Q = null; if (c.Panel.IsOpen) c.Panel.Close(); continue; }
                string id = (string)row["id"], pid = (string)row["projectId"];
                string proj = (string)_projects.OfType<JObject>().FirstOrDefault(x => (string)x["id"] == pid)?["name"];
                var q = QuickThreads.Describe(row, proj, PulseOf(id), now, _threadsDoneUnseen.Contains(id));
                bool open = id == _threadOpen && IsPanelOpen("thread");
                var col = QuickColor(q.State, t);
                c.Q = q;
                c.Open = open;
                c.Project.Set(q.Project.Length > 0 ? Esc(q.Project.ToUpperInvariant()) : "—");
                c.Title.Set(Esc(q.Title), open ? t.TextAccent : t.Text);
                c.Status.Set(q.Word, col);
                c.Panel.SetFrameTint(Color.Lerp(t.Frame, col, q.State == QuickState.Stopped ? 0f : q.State == QuickState.Done && !q.Fresh ? 0.45f : 0.8f));
                c.Panel.Shapes.SetTint(c.Dot, col);
                if (!q.Pulses) { c.Panel.SetHighlight(OrbCardRest(q, open)); c.Panel.Shapes.SetState(c.Dot, 0.6f); }
                if (!c.Panel.IsOpen) { PlaceOrbCard(k); c.Panel.Open(); }
            }
        }

        /// Every frame while the billboard is drawn: the cards stay under it; while it is up, the working ones pulse,
        /// the texts follow each second and MQH is asked every 4 s.
        private void TickOrbThreads()
        {
            if (_orbCards.Count == 0 || _orbMenu == null) return;
            for (int k = 0; k < _orbCards.Count; k++) if (_orbCards[k].Panel.gameObject.activeSelf) PlaceOrbCard(k);
            if (!_orbMenu.IsOpen) return;
            if (_orbClock >= _orbCardsTextAt) { _orbCardsTextAt = _orbClock + 1f; RenderOrbCards(); }
            if (_orbClock >= _orbCardsPollAt) { _orbCardsPollAt = _orbClock + 4f; PollOrbThreads(); }
            float wave = 0.5f - 0.5f * Mathf.Cos(_orbClock * Mathf.PI * 2f * OrbCardPulseHz);
            foreach (var c in _orbCards)
            {
                if (c.Q == null || !c.Q.Pulses || !c.Panel.IsOpen) continue;
                c.Panel.SetHighlight(Mathf.Lerp(c.Open ? 0.35f : 0.10f, 0.85f, wave));
                c.Panel.Shapes.SetState(c.Dot, Mathf.Lerp(0.25f, 1f, wave));
            }
        }

        /// Card k under the billboard, in its plane and scale (it turns to the user with it).
        private void PlaceOrbCard(int k)
        {
            var p = _orbCards[k].Panel;
            var tr = _orbMenu.transform;
            if (p.BaseScale != _orbMenu.BaseScale) p.BaseScale = _orbMenu.BaseScale;
            float y = BillboardH * 0.5f + OrbCardGap + OrbCardH * 0.5f + k * (OrbCardH + OrbCardGap);
            p.transform.SetPositionAndRotation(tr.TransformPoint(new Vector3(0f, -y, 0f)), tr.rotation);
        }

        /// MQH's thread rows now (the first ask when the menu opens, then while MQH answers): the states are fresh, an
        /// archived thread leaves, a new one joins at the end while there is room.
        private void PollOrbThreads()
        {
            if (!Application.isPlaying || Mqh == null || _orbCardsPolled && !Mqh.Online) return;
            _orbCardsPolled = true;
            Mqh.Threads(null, th =>
            {
                if (!(th is JArray rows)) return;
                _threads = rows;
                CtxThreadsUpdated();
                RebuildWorkspaceMap();
                bool unknownProject = rows.OfType<JObject>().Any(r => !_projects.OfType<JObject>().Any(p => (string)p["id"] == (string)r["projectId"]));
                if (unknownProject) Mqh.Projects(pr => { if (pr is JArray a) { _projects = a; RenderOrbCards(); } });
                SyncOrbOrder();
                RenderOrbCards();
            });
        }

        private void SyncOrbOrder()
        {
            var ids = new HashSet<string>(_threads.OfType<JObject>().Select(x => (string)x["id"]).Where(x => x != null));
            _orbOrder.RemoveAll(id => !ids.Contains(id));
            foreach (var id in QuickThreads.Order(_threads, _threadUsedAt))
                if (_orbOrder.Count < QuickThreads.Max && !_orbOrder.Contains(id)) _orbOrder.Add(id);
        }

        /// A tap: that thread's window shows (THREAD on it), or hides when it is the one shown. The menu stays up.
        private void OrbCardTapped(int k)
        {
            var q = k >= 0 && k < _orbCards.Count ? _orbCards[k].Q : null;
            if (q == null) return;
            if (q.Id == _threadOpen && IsPanelOpen("thread")) ClosePanel("thread");
            else OpenThread(q.Id);
            RenderOrbCards();
        }

        // ---- recently used

        private void LoadThreadUse()
        {
            if (_threadUseLoaded) return;
            _threadUseLoaded = true;
            if (!Application.isPlaying) return; // self-tests: in memory only
            try
            {
                if (!File.Exists(ThreadUsePath)) return;
                foreach (var line in File.ReadAllLines(ThreadUsePath))
                {
                    int sp = line.IndexOf(' ');
                    if (sp <= 0 || !long.TryParse(line.Substring(0, sp), out long at)) continue;
                    string id = line.Substring(sp + 1).Trim();
                    if (id.Length > 0 && (!_threadUsedAt.TryGetValue(id, out var had) || had < at)) _threadUsedAt[id] = at;
                }
            }
            catch (Exception ex) { Log("recent threads unreadable: " + ex.Message); }
        }

        /// The user used a thread here (opened it, wrote to it): it comes first in the orb's quick list; its answer
        /// has been seen.
        private void NoteThreadUsed(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            LoadThreadUse();
            _threadUsedAt[id] = NowMs;
            _threadsDoneUnseen.Remove(id);
            if (_threadUsedAt.Count > 24)
                foreach (var old in _threadUsedAt.OrderBy(kv => kv.Value).Take(_threadUsedAt.Count - 24).Select(kv => kv.Key).ToList()) _threadUsedAt.Remove(old);
            if (!Application.isPlaying) return;
            try { File.WriteAllLines(ThreadUsePath, _threadUsedAt.OrderByDescending(kv => kv.Value).Select(kv => kv.Value + " " + kv.Key)); }
            catch (Exception ex) { Log("recent threads not saved: " + ex.Message); }
        }

        /// A thread's pulse changed (ShellThreadView.NotifyThreadChange): an answer that came while its window was
        /// not shown is new until the user opens it; working again, it is not.
        private void NoteThreadDone(string id, ThreadPulse before, ThreadPulse now, bool open)
        {
            if (id == null || now == null) return;
            if (now.Working) _threadsDoneUnseen.Remove(id);
            else if (before != null && before.Working && now.State == "idle" && !open) _threadsDoneUnseen.Add(id);
            if (_orbMenu != null && _orbMenu.IsOpen) RenderOrbCards();
        }

        /// The thread a card shows (the session context: "this thread" while pointing at its card).
        private string OrbCardThread(UiPanel p) => _orbCards.FirstOrDefault(c => c.Panel == p)?.Q?.Id;

        // ---- self-tests

        public IReadOnlyList<UiPanel> OrbThreadCardsForTest => _orbCards.Select(c => c.Panel).ToList();
        public List<QuickThread> OrbThreadsForTest => _orbCards.Where(c => c.Q != null && c.Panel.IsOpen).Select(c => c.Q).ToList();
        public (string project, string title, string status) OrbCardTextsForTest(int k) =>
            (_orbCards[k].Project.Label.Text, _orbCards[k].Title.Label.Text, _orbCards[k].Status.Label.Text);
        public UiLabel[] OrbCardLabelsForTest(int k) => new[] { _orbCards[k].Project, _orbCards[k].Title, _orbCards[k].Status };
        public void TapOrbThreadForTest(int k) => _orbCards[k].Hit.Click();
        public void NoteThreadUsedForTest(string id, long at) { LoadThreadUse(); _threadUsedAt[id] = at; }
        public void ClearThreadUseForTest() { _threadUsedAt.Clear(); _threadsDoneUnseen.Clear(); _threadUseLoaded = true; }
        /// A background thread's pulse, as CtxPollPulses brings it.
        public void PulseThreadForTest(string id, ThreadPulse p) => CtxPulse(id, p);
        public string OrbCardThreadForTest(int k) => OrbCardThread(_orbCards[k].Panel);
        public string OpenThreadIdForTest => _threadOpen;
        /// A hand ray on a panel that is not one of the windows (the orb's cards).
        public void PointAtForTest(UiPanel p) { _ctxPointed = p; _ctxPointedAt = Time.unscaledTime; }
    }
}
