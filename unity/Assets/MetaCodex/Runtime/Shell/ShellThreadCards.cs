using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// THREAD CARDS (owner 2026-11-14, docs/thread-view.md): a card is a small live view of one thread (title, project,
    /// a status pulse with how long, its last reply or what it does now; OPEN, REPLY, ALLOW / DENY, ✕). Cards live in
    /// groups, and a group is ONE window (`tgroup-<n>`): its cards side by side (two rows from 5 cards), a slim header
    /// (name, "N threads", ✎ rename, ⇲ ungroup, ✕). Because a group is one panel, moving it moves all its threads, and
    /// placement, RECENTER, the float ring and bending treat it as one window; a wide group bends round the user.
    /// Drag a group's header onto another group: they merge (the target keeps its place). Drag a card's ✥ grip out past
    /// its group's edge: it becomes a group of one there (or joins the group it is let go on). The groups (names, threads
    /// in order, open or not) are kept in the settings; their places are not (an open group is placed again at start).
    /// The cards' data: the thread rows and pulses CtxPollPulses reads (every 4 s while a card is shown, parsed off the
    /// main thread); a card is redrawn when its pulse changes, its clock once a second while it works.
    public sealed partial class MetaCodexShell
    {
        public const string CardGroupPrefix = "tgroup-";
        public const float CardW = 0.30f, CardH = 0.20f, CardPollSeconds = 4f;
        public const int MaxCardsPerGroup = 8;

        private sealed class CardView
        {
            public string Thread;
            public Rect Rect;
            public int Bg = -1, Dot = -1;
            public UiLabel Title, Meta, Status, Body;
            public UiButton Open, Reply, Allow, Deny, Remove;
            public CardGrip Grip;
            public string State = ""; // what the status shows now: "working" and "waiting" pulse their dot
        }

        private sealed class CardGroup
        {
            public int N;
            public string Name = "";
            public bool Open = true;
            public readonly List<string> Threads = new List<string>();
            public UiPanel Panel;
            public readonly List<CardView> Cards = new List<CardView>();
            public bool Dirty, AnyLive;
            public float NextText, NextPulse;
            public string Id => CardGroupPrefix + N;
        }

        private readonly List<CardGroup> _groups = new List<CardGroup>();
        private readonly Dictionary<string, long> _cardSeen = new Dictionary<string, long>(); // thread -> updatedAt last read
        private int _groupSeq;
        private bool _cardsLoaded, _cardsHooked, _cardsVoiceOn = true;
        private CardGroup _dropTarget;
        private UiPanel _dropSource;  // the group window being moved onto _dropTarget (null: a card's grip)
        private CardGrip _gripActive; // a card's grip being pulled
        private static readonly JArray NoRows = new JArray();

        // ------------------------------------------------------------------ the groups (memory, settings)

        private CardGroup CardOf(string thread)
        {
            if (string.IsNullOrEmpty(thread)) return null;
            foreach (var g in _groups) if (g.Threads.Contains(thread)) return g;
            return null;
        }

        private CardGroup GroupOfPanel(UiPanel p)
        {
            if (p == null) return null;
            foreach (var g in _groups) if (g.Panel == p) return g;
            return null;
        }

        /// Settings -> groups (once, at start; self-tests again). A thread is in one group only.
        private void CardsFromSettings()
        {
            _cardsLoaded = true;
            _groups.Clear();
            var seen = new HashSet<string>();
            foreach (var s in Settings.threadCards ?? new List<ThreadCardGroup>())
            {
                if (s == null || s.threads == null) continue;
                var g = new CardGroup { N = ++_groupSeq, Name = (s.name ?? "").Trim(), Open = s.open };
                foreach (var id in s.threads) if (!string.IsNullOrEmpty(id) && seen.Add(id) && g.Threads.Count < MaxCardsPerGroup) g.Threads.Add(id);
                if (g.Threads.Count > 0) _groups.Add(g);
            }
        }

        private void CardsToSettings()
        {
            if (!_cardsLoaded || Settings == null) return;
            Settings.threadCards = _groups.Select(g => new ThreadCardGroup { name = g.Name, open = g.Open, threads = new List<string>(g.Threads) }).ToList();
        }

        /// The groups changed: into the settings file now, to MQH's copy when things are quiet.
        private void SaveCards()
        {
            CardsToSettings();
            Settings.Save(_settingsPath);
            if (Settings.mqhSync) _settingsPushDue = _clock.Elapsed.TotalSeconds + 1.5;
        }

        /// BuildPanels (and RebuildPanels): every group's window, closed; the first time the groups come from the settings
        /// and the ones that were open open again after the splash, each at the best free place (PlaceSmart).
        private void BuildCardGroups()
        {
            if (!_cardsHooked)
            {
                _cardsHooked = true;
                Router.PanelDragging += OnCardPanelDragging;
                Router.PanelDropped += OnCardPanelDropped;
            }
            bool first = !_cardsLoaded;
            if (first) CardsFromSettings();
            foreach (var g in _groups) { g.Panel = null; g.Cards.Clear(); BuildGroupPanel(g, null); }
            _dropTarget = null; _dropSource = null; _gripActive = null;
            if (first && Application.isPlaying && _groups.Any(g => g.Open)) After(4f, OpenSavedCardGroups);
            UpdateCardVoice();
        }

        private void OpenSavedCardGroups()
        {
            foreach (var g in _groups.ToList()) if (g.Open && g.Panel != null && !g.Panel.IsOpen) OpenPanel(g.Id);
            _ctxNextPulses = 0f; // their data now
        }

        // ------------------------------------------------------------------ the group window

        private static int CardCols(int n) => n <= 4 ? Mathf.Max(1, n) : (n + 1) / 2;

        private string GroupTitle(CardGroup g) =>
            "✥ " + (g.Name.Length > 0 ? g.Name + " · " : "") + ThreadPulse.Plural(g.Threads.Count, "thread");

        /// Builds (or builds again, after its membership changed) a group's window: one rebuild per change. Built again,
        /// it keeps its centre, turn, bend and open state (it is the same window, wider or narrower).
        private void BuildGroupPanel(CardGroup g, UiPanel old)
        {
            var t = UiTheme.Current;
            bool had = old != null;
            Vector3 pos = default; Quaternion rot = default; bool wasOpen = false; float bend = 0f;
            if (had) { pos = old.transform.position; rot = old.transform.rotation; wasOpen = old.IsOpen; bend = old.BendRadius; DestroyGroupPanel(g, false); }
            int n = g.Threads.Count, cols = CardCols(n), rows = n <= 4 ? 1 : 2;
            float pad = t.Padding, gap = t.Gap;
            float w = 2f * pad + cols * CardW + (cols - 1) * gap;
            float h = t.HeaderHeight + 2f * pad + rows * CardH + (rows - 1) * gap;
            var p = Panel<UiPanel>(g.Id, GroupTitle(g), w, h);
            p.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
            g.Panel = p;
            g.Cards.Clear();
            // header: ✎ rename, ⇲ UNGROUP (a group of 2+), the panel's own ✕ (closes the window; the cards stay)
            float hy = 0.006f, hh = t.HeaderHeight - 0.010f;
            float renameX = w - 0.044f - 0.040f;
            p.Add(new UiButton("✎", () => RenameGroupPrompt(g), UiButtonStyle.Ghost) { Rect = new Rect(renameX, hy, 0.036f, hh) });
            float titleEnd = renameX;
            if (n > 1)
            {
                float ugX = renameX - 0.004f - 0.088f;
                p.Add(new UiButton("⇲ UNGROUP", () => UngroupCards(g), UiButtonStyle.Ghost) { Rect = new Rect(ugX, hy, 0.088f, hh), TextSize = t.SmallSize });
                titleEnd = ugX;
            }
            p.SetTitleWidth(titleEnd - (t.Padding + 0.006f) - 0.004f);
            for (int i = 0; i < n; i++)
            {
                int col = i % cols, row = i / cols;
                var r = new Rect(pad + col * (CardW + gap), t.HeaderHeight + pad + row * (CardH + gap), CardW, CardH);
                g.Cards.Add(BuildCard(p, g, g.Threads[i], r));
            }
            p.Tick += _ => TickGroup(g);
            p.Opened += _ => { if (GroupOfPanel(p) == g && !g.Open) { g.Open = true; SaveCards(); } g.Dirty = true; _ctxNextPulses = Mathf.Min(_ctxNextPulses, Time.unscaledTime + 0.5f); };
            p.Closed += _ => { if (GroupOfPanel(p) == g && g.Open) { g.Open = false; SaveCards(); } if (_dropTarget == g) ClearDropTarget(); };
            RefreshGroup(g);
            if (had)
            {
                p.transform.SetPositionAndRotation(pos, rot);
                p.SetBend(bend);
                p.SnapBend();
                _placed.Add(g.Id);
                if (wasOpen) { p.gameObject.SetActive(true); p.ForceRefresh(); }
            }
        }

        /// Takes a group's window away (`animate`: it closes with its transition first, the group is gone).
        private void DestroyGroupPanel(CardGroup g, bool animate)
        {
            var p = g.Panel;
            g.Panel = null;
            g.Cards.Clear();
            if (_dropTarget == g) { _dropTarget = null; _dropSource = null; }
            if (p == null) return;
            Router.Unregister(p);
            if (_panels.TryGetValue(g.Id, out var q) && q == p) _panels.Remove(g.Id);
            _slots.Remove(p.transform);
            if (animate && p.IsOpen) p.CloseAndDestroy();
            else UiToasts.Kill(p.gameObject);
        }

        private CardView BuildCard(UiPanel p, CardGroup g, string id, Rect r)
        {
            var t = UiTheme.Current;
            var c = new CardView { Thread = id, Rect = r };
            c.Bg = p.Shapes.Add(p.Shape(UiShapeKind.Button, r, t.AccentSoft, 0.008f, 0f, 0.004f, 0.10f)); // a quiet surface: lit while pulled out
            float x0 = r.x + 0.010f, x1 = r.xMax - 0.010f, y = r.y + 0.008f;
            const float rowH = 0.030f, gripW = 0.030f;
            bool grip = g.Threads.Count > 1; // a group of one moves by its header
            if (grip) c.Grip = p.Add(new CardGrip(this, g, id) { Rect = new Rect(x0, y, gripW, rowH), TextSize = t.BodySize });
            c.Remove = p.Add(new UiButton("✕", () => RemoveCard(id), UiButtonStyle.Ghost) { Rect = new Rect(x1 - rowH, y, rowH, rowH), TextSize = t.SmallSize * 1.1f });
            float tx = grip ? x0 + gripW + 0.004f : x0;
            c.Title = p.Add(new UiLabel("", t.BodySize * 1.05f) { Rect = new Rect(tx, y, x1 - rowH - 0.004f - tx, rowH) });
            y += rowH + 0.002f;
            c.Meta = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(x0, y, x1 - x0, 0.018f), Color = t.TextDim });
            y += 0.020f;
            c.Dot = p.Shapes.Add(p.Shape(UiShapeKind.Dot, new Rect(x0 + 0.002f, y + 0.005f, 0.012f, 0.012f), t.TextDim, 0f, 0f, 0.004f, 0.6f));
            c.Status = p.Add(new UiLabel("", t.SmallSize * 1.05f) { Rect = new Rect(x0 + 0.020f, y, x1 - x0 - 0.020f, 0.022f), UseDisplay = true });
            y += 0.026f;
            float by = r.yMax - 0.010f - t.ButtonHeight;
            c.Body = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(x0, y, x1 - x0, by - y - 0.004f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            float bw = (x1 - x0 - t.Gap * 3) / 4f, ts = t.SmallSize * 1.05f;
            Rect B(int k) => new Rect(x0 + k * (bw + t.Gap), by, bw, t.ButtonHeight);
            c.Open = p.Add(new UiButton("OPEN", () => OpenCard(id), UiButtonStyle.Primary) { Rect = B(0), TextSize = ts });
            c.Reply = p.Add(new UiButton("REPLY", () => ReplyCard(id)) { Rect = B(1), TextSize = ts });
            c.Allow = p.Add(new UiButton("ALLOW", () => CardResolve(id, "allow_once")) { Rect = B(2), TextSize = ts });
            c.Deny = p.Add(new UiButton("DENY", () => CardResolve(id, "deny"), UiButtonStyle.Danger) { Rect = B(3), TextSize = ts });
            c.Allow.SetVisible(false);
            c.Deny.SetVisible(false);
            return c;
        }

        /// Every frame while the group is open: nothing but two comparisons unless a card changed (Dirty), its clock runs
        /// (once a second while one works or waits; every 30 s for "answered 5 min ago"), or its dot pulses (15 times a
        /// second, live cards only).
        private void TickGroup(CardGroup g)
        {
            if (_dropTarget == g && _dropSource != null && (Router == null || !Router.IsDragging(_dropSource))) ClearDropTarget();
            if (_gripActive != null && _gripActive.Group == g && !_gripActive.Pressed) EndGripLook(g);
            float now = Time.unscaledTime;
            if (g.Dirty || now >= g.NextText) RefreshGroup(g);
            if (g.AnyLive && now >= g.NextPulse && g.Panel != null)
            {
                g.NextPulse = now + 1f / 15f;
                foreach (var c in g.Cards)
                {
                    if (c.State != "working" && c.State != "waiting") continue;
                    float speed = c.State == "waiting" ? 2.2f : 0.9f;
                    g.Panel.Shapes.SetState(c.Dot, 0.35f + 0.65f * (0.5f + 0.5f * Mathf.Sin(now * speed * Mathf.PI * 2f)));
                }
            }
        }

        private void RefreshGroup(CardGroup g)
        {
            g.Dirty = false;
            bool live = false;
            foreach (var c in g.Cards) { RefreshCard(g, c); live |= c.State == "working" || c.State == "waiting"; }
            g.AnyLive = live;
            g.NextText = Time.unscaledTime + (live ? 1f : 30f);
        }

        // ------------------------------------------------------------------ a card's data

        /// The thread's row: the open thread's own, else the newest list read.
        private JObject CardRow(string id)
        {
            if (id == null) return null;
            if (id == _threadOpen && _threadInfo != null && (string)_threadInfo["id"] == id) return _threadInfo;
            foreach (var x in _ctxThreadRows ?? NoRows) if ((string)x["id"] == id) return x as JObject;
            foreach (var x in _threads) if ((string)x["id"] == id) return x as JObject;
            return null;
        }

        private ThreadPulse CardPulse(string id)
        {
            if (id == null) return null;
            if (id == _threadOpen && _thPulse != null && IsPanelOpen("thread")) return _thPulse;
            return _pulses.TryGetValue(id, out var p) ? p : null;
        }

        private string CardTitle(string id)
        {
            var row = CardRow(id);
            return (string)row?["title"] ?? (string)row?["titleFallback"] ?? CardPulse(id)?.Title ?? "thread";
        }

        private string CardProject(string id)
        {
            var row = CardRow(id);
            string proj = ThreadProjectName(id);
            if (proj == null)
            {
                string path = ((string)row?["environmentPath"] ?? "").TrimEnd('/');
                int k = path.LastIndexOf('/');
                proj = k >= 0 ? path.Substring(k + 1) : path;
            }
            return proj;
        }

        /// "3 min ago" against the shell's clock (self-tests fix it).
        private string AgoMs(long ms)
        {
            if (ms <= 0) return "";
            long secs = Math.Max(0, (NowMs - ms) / 1000);
            return secs < 60 ? "just now" : secs < 3600 ? (secs / 60) + " min ago" : secs < 86400 ? (secs / 3600) + " h ago" : (secs / 86400) + " d ago";
        }

        private int SecsSince(long ms, int fallback) => ms > 0 && NowMs > ms ? (int)((NowMs - ms) / 1000) : fallback;

        /// The status pulse: its words, its colour, and the state it stands for.
        private (string text, Color color, string state) CardStatus(ThreadPulse p, JObject row)
        {
            var t = UiTheme.Current;
            if (p == null)
            {
                string st = (string)row?["runtime"]?["displayStatus"] ?? (string)row?["status"] ?? "";
                if (row?["hasPendingInteraction"]?.Type == JTokenType.Boolean && (bool)row["hasPendingInteraction"]) return ("WAITING FOR YOU", t.Warn, "waiting");
                if (ThreadTimeline.IsWorking(st)) return ("WORKING", t.Ok, "working");
                return (row == null ? "◌ READING THE THREAD…" : st == "error" ? "ERROR" : "IDLE", row == null ? t.TextDim : st == "error" ? t.Danger : t.TextDim, row == null ? "" : st == "error" ? "error" : "idle");
            }
            switch (p.State)
            {
                case "working":
                    return ("WORKING · " + ThreadPulse.Clock(SecsSince(p.TurnStart, p.TurnSecs)) + (p.MayBeStuck ? " · MAY BE STUCK" : ""), p.MayBeStuck ? t.Warn : t.Ok, "working");
                case "waiting": return ("WAITING FOR YOU · " + ThreadPulse.Clock(SecsSince(p.PhaseStart, p.PhaseSecs)), t.Warn, "waiting");
                case "held": return ("WAITING ITS TURN", t.Warn, "held");
                case "error": return ("ERROR" + (p.LastActivity > 0 ? " · " + AgoMs(p.LastActivity) : ""), t.Danger, "error");
                case "stopped": return ("STOPPED" + (p.LastActivity > 0 ? " · " + AgoMs(p.LastActivity) : ""), t.TextDim, "stopped");
                default:
                    return p.AnsweredAt > 0 ? ("ANSWERED · " + AgoMs(p.AnsweredAt), t.TextAccent, "answered")
                        : ("IDLE" + (p.LastActivity > 0 ? " · " + AgoMs(p.LastActivity) : ""), t.TextDim, "idle");
            }
        }

        /// The card's text: what it does now while it works, what it asks while it waits, else its last answer.
        private static string CardBody(ThreadPulse p)
        {
            if (p == null) return "";
            switch (p.State)
            {
                case "working":
                {
                    string more = p.LastUpdate.Length > 0 ? p.LastUpdate : p.LastThought;
                    return Cap(p.Doing) + (more.Length > 0 ? "\n" + more : "");
                }
                case "waiting": case "held": return Cap(p.Doing);
                case "error": return p.ErrorText.Length > 0 ? p.ErrorText : "the turn failed";
                case "stopped": return "Stopped before it finished" + (p.Prompt.Length > 0 ? ": " + p.Prompt : "");
                default: return p.AnswerGist.Length > 0 ? p.AnswerGist : p.Prompt.Length > 0 ? "asked: " + p.Prompt : "";
            }
        }

        private static bool AsksApproval(ThreadPulse p, JObject row) =>
            p != null ? p.State == "waiting" && !p.Doing.StartsWith("waiting for your answer", StringComparison.Ordinal)
                : row?["hasPendingInteraction"]?.Type == JTokenType.Boolean && (bool)row["hasPendingInteraction"];

        private void RefreshCard(CardGroup g, CardView c)
        {
            var p = g.Panel;
            if (p == null) return;
            var row = CardRow(c.Thread);
            var pulse = CardPulse(c.Thread);
            c.Title.Set("<b>" + Esc(ThreadPulse.Clip(CardTitle(c.Thread), 60)) + "</b>");
            string proj = CardProject(c.Thread), prov = (string)row?["providerId"] ?? pulse?.Provider ?? "";
            c.Meta.Set(Esc(proj.Length > 0 && prov.Length > 0 ? proj + " · " + prov : proj + prov));
            var (text, color, state) = CardStatus(pulse, row);
            c.Status.Set(text, color);
            p.Shapes.SetTint(c.Dot, color);
            if (state != "working" && state != "waiting") p.Shapes.SetState(c.Dot, 0.6f);
            c.State = state;
            c.Body.Set(Esc(ThreadPulse.Clip(CardBody(pulse), 150)), state == "error" ? UiTheme.Current.Danger : UiTheme.Current.Text);
            bool ask = AsksApproval(pulse, row);
            c.Allow.SetVisible(ask);
            c.Deny.SetVisible(ask);
        }

        /// A thread's pulse changed (CtxPulse): its card is drawn again at its group's next frame.
        private void MarkCardDirty(string thread)
        {
            foreach (var g in _groups) if (g.Threads.Contains(thread)) g.Dirty = true;
        }

        /// Threads whose card is shown now (an open group): CtxPollPulses reads them every few seconds.
        private HashSet<string> ShownCardThreads()
        {
            HashSet<string> set = null;
            foreach (var g in _groups)
                if (g.Panel != null && g.Panel.IsOpen) (set ??= new HashSet<string>()).UnionWith(g.Threads);
            return set;
        }

        /// A shown card's thread needs its timeline read: it has no pulse yet, or its row moved since the last read.
        private bool CardNeedsRead(string id, JObject row)
        {
            long up = row?["updatedAt"]?.Type == JTokenType.Integer ? (long)row["updatedAt"] : 0;
            bool need = !_pulses.ContainsKey(id) || !_cardSeen.TryGetValue(id, out var was) || was != up;
            _cardSeen[id] = up;
            return need;
        }

        // ------------------------------------------------------------------ actions

        /// A thread as a card: a new group of one beside the window it was asked from (THREADS' ⊞, THREAD's ⊞ CARD,
        /// voice). Already a card: its group opens (and flashes).
        public bool PinCard(string thread)
        {
            if (string.IsNullOrEmpty(thread)) return false;
            var had = CardOf(thread);
            if (had != null)
            {
                if (had.Panel != null) OpenPanel(had.Id);
                Toast("“" + ThreadPulse.Clip(CardTitle(thread), 40) + "” is a card already", "info");
                return true;
            }
            var g = new CardGroup { N = ++_groupSeq, Open = true };
            g.Threads.Add(thread);
            _groups.Add(g);
            BuildGroupPanel(g, null);
            OpenPanel(g.Id);
            SaveCards();
            CardsChanged();
            return true;
        }

        /// ✕ on a card: the card goes; a group left empty goes with it.
        public bool RemoveCard(string thread)
        {
            var g = CardOf(thread);
            if (g == null) return false;
            g.Threads.Remove(thread);
            if (g.Threads.Count == 0)
            {
                DestroyGroupPanel(g, true);
                _groups.Remove(g);
                _placed.Remove(g.Id);
            }
            else BuildGroupPanel(g, g.Panel);
            SaveCards();
            CardsChanged();
            return true;
        }

        /// OPEN: the thread in the THREAD window (with its sidebar), beside the group; the card stays.
        public void OpenCard(string thread)
        {
            if (string.IsNullOrEmpty(thread)) return;
            OpenThread(thread);
        }

        /// The last message a card sent (self-tests: MQH is not there).
        public (string thread, string text) LastCardSendForTest { get; private set; }

        /// REPLY: the system keyboard; Done sends what was typed to that thread (the composer's send, with that thread's
        /// model and access).
        private void ReplyCard(string thread)
        {
            string title = ThreadPulse.Clip(CardTitle(thread), 40);
            UiKeyboard.Edit("", false, null, text => { if (!string.IsNullOrWhiteSpace(text)) CardSend(thread, text); }, "REPLY · " + title, true);
        }

        private void CardSend(string thread, string text)
        {
            string msg = (text ?? "").Trim();
            if (msg.Length == 0 || thread == null) return;
            LastCardSendForTest = (thread, msg);
            NoteThreadUsed(thread);
            Mqh.SendMessage(thread, msg, SendExtra(thread), null, r =>
            {
                if (r == null) { Toast("Not sent: " + Mqh.LastError, "error"); return; }
                Toast(((string)r["delivery"] == "queued" ? "Queued for “" : "Sent to “") + ThreadPulse.Clip(CardTitle(thread), 40) + "”", "ok");
                if (thread == _threadOpen) { _nextThreadMeta = 0f; PollThread(); }
                _ctxNextPulses = 0f;
            });
        }

        /// ALLOW / DENY on a card: the thread's pending approval answered (THREAD's ALLOW ONCE / DENY calls).
        private void CardResolve(string thread, string decision)
        {
            Mqh.Interactions(thread, ia =>
            {
                var pending = (ia as JArray)?.FirstOrDefault(x => (string)x["status"] == "pending" || x["status"] == null);
                if (pending == null) { Toast("Nothing waits for an answer there now", "info"); _ctxNextPulses = 0f; return; }
                if ((string)pending["payload"]?["kind"] != "approval") { Toast("It asks a question: OPEN the thread to answer it", "warn"); return; }
                Mqh.ResolveApproval(thread, (string)pending["id"], decision, r =>
                {
                    Toast(r != null ? "Answered: " + decision.Replace('_', ' ') : "MQH: " + Mqh.LastError, r != null ? "ok" : "error");
                    if (thread == _threadOpen) { SetAskVisible(false); PollThread(); }
                    _ctxNextPulses = 0f;
                });
            });
        }

        /// ⇲ UNGROUP: every card a group of one; the first keeps the group's place, the others take the best free places
        /// beside it.
        private void UngroupCards(CardGroup g)
        {
            if (g == null || g.Threads.Count < 2) return;
            var rest = g.Threads.Skip(1).ToList();
            g.Threads.RemoveRange(1, g.Threads.Count - 1);
            bool open = g.Panel != null && g.Panel.IsOpen;
            BuildGroupPanel(g, g.Panel);
            var placed = new List<UiPanel>();
            if (g.Panel != null) placed.Add(g.Panel);
            foreach (var id in rest)
            {
                var ng = new CardGroup { N = ++_groupSeq, Open = open };
                ng.Threads.Add(id);
                _groups.Add(ng);
                BuildGroupPanel(ng, null);
                if (!open) continue;
                PlaceSmart(ng.Id, ng.Panel, g.Panel);
                ng.Panel.Open();
                _placed.Add(ng.Id);
                OnOpenedFloating(ng.Panel);
            }
            SaveCards();
            CardsChanged();
        }

        private void RenameGroupPrompt(CardGroup g)
        {
            UiKeyboard.Edit(g.Name, false, null, name => RenameGroup(g, name), "GROUP NAME (empty: none)");
        }

        private void RenameGroup(CardGroup g, string name)
        {
            if (g == null) return;
            g.Name = ThreadPulse.Clip(name ?? "", 40);
            g.Panel?.SetTitle(GroupTitle(g));
            SaveCards();
        }

        /// After a membership change: voice commands, THREADS' ⊞ / ▣, and the cards' data soon.
        private void CardsChanged()
        {
            UpdateCardVoice();
            if (IsPanelOpen("threads")) RenderThreadGroups();
            _ctxNextPulses = Mathf.Min(_ctxNextPulses, Time.unscaledTime + 0.5f);
        }

        // ------------------------------------------------------------------ grouping by drag

        private CardGroup GroupUnder(Ray ray, CardGroup except)
        {
            CardGroup best = null;
            float bestD = float.MaxValue;
            foreach (var g in _groups)
            {
                if (g == except || g.Panel == null || !g.Panel.IsOpen) continue;
                if (g.Panel.Raycast(ray, out _, out float d) && d < bestD) { best = g; bestD = d; }
            }
            return best;
        }

        private void SetDropTarget(CardGroup g, UiPanel source)
        {
            if (_dropTarget == g) { _dropSource = source; return; }
            ClearDropTarget();
            if (g?.Panel == null) return;
            _dropTarget = g;
            _dropSource = source;
            g.Panel.SetHighlight(1f);
            g.Panel.SetTitle("▼ LET GO TO GROUP · " + ThreadPulse.Plural(g.Threads.Count, "thread"));
        }

        private void ClearDropTarget()
        {
            var g = _dropTarget;
            _dropTarget = null;
            _dropSource = null;
            if (g?.Panel == null) return;
            g.Panel.SetHighlight(0f);
            g.Panel.SetTitle(GroupTitle(g));
        }

        /// A group's window being moved: the group under the hand's ray lights up (it would take these cards).
        private void OnCardPanelDragging(UiPanel p, Ray ray)
        {
            if (p == null || p.Id == null || !p.Id.StartsWith(CardGroupPrefix, StringComparison.Ordinal)) return;
            var src = GroupOfPanel(p);
            if (src == null) return;
            var target = GroupUnder(ray, src);
            if (target == null) { if (_dropTarget != null) ClearDropTarget(); return; }
            SetDropTarget(target, p);
        }

        /// Let go over another group: the dragged cards join it (appended; the target keeps its place).
        private void OnCardPanelDropped(UiPanel p, Ray ray)
        {
            if (p == null || p.Id == null || !p.Id.StartsWith(CardGroupPrefix, StringComparison.Ordinal)) return;
            var src = GroupOfPanel(p);
            ClearDropTarget();
            if (src == null) return;
            var target = GroupUnder(ray, src);
            if (target != null) MergeGroups(src, target);
        }

        private bool MergeGroups(CardGroup src, CardGroup dst)
        {
            if (src == null || dst == null || src == dst) return false;
            if (dst.Threads.Count + src.Threads.Count > MaxCardsPerGroup) { Toast($"A group holds up to {MaxCardsPerGroup} threads", "warn"); return false; }
            dst.Threads.AddRange(src.Threads);
            if (dst.Name.Length == 0) dst.Name = src.Name;
            DestroyGroupPanel(src, false);
            _groups.Remove(src);
            _placed.Remove(src.Id);
            BuildGroupPanel(dst, dst.Panel);
            dst.Panel?.Ping();
            SaveCards();
            CardsChanged();
            Toast("Grouped: " + ThreadPulse.Plural(dst.Threads.Count, "thread"), "ok");
            return true;
        }

        /// How far past its group's edge a card's grip must be let go to come out (metres, panel coordinates).
        private const float PullOutMargin = 0.02f;

        private static bool PastEdge(UiPanel p, Vector2 pt) =>
            pt.x < -PullOutMargin || pt.y < -PullOutMargin || pt.x > p.Width + PullOutMargin || pt.y > p.Height + PullOutMargin;

        private static Ray? PointerRay(UiPointer ptr, Transform head)
        {
            if (ptr == null) return null;
            if (ptr.Kind != PointerKind.Poke) return ptr.Ray;
            var eye = head != null ? head.position : ptr.Tip - Vector3.forward * 0.4f;
            var d = ptr.Tip - eye;
            return d.sqrMagnitude > 1e-6f ? new Ray(eye, d.normalized) : (Ray?)null;
        }

        /// A card's grip moves: past the edge its card lights up and so does a group under the hand.
        private void CardGripDrag(CardGroup g, string thread, UiPointerEvent e)
        {
            var p = g.Panel;
            if (p == null) return;
            var c = g.Cards.FirstOrDefault(x => x.Thread == thread);
            if (c == null) return;
            bool out_ = PastEdge(p, e.Point);
            p.Shapes.SetTint(c.Bg, out_ ? UiTheme.Current.Warn : UiTheme.Current.AccentSoft);
            p.Shapes.SetState(c.Bg, out_ ? 0.7f : 0.35f);
            var ray = PointerRay(e.Pointer, Head);
            var target = ray.HasValue ? GroupUnder(ray.Value, g) : null;
            if (target != null) SetDropTarget(target, null);
            else if (_dropTarget != null) ClearDropTarget();
        }

        // CardGrip's calls (it cannot name the private CardGroup)
        internal void GripPressed(CardGrip grip) => _gripActive = grip;
        internal void GripDragged(CardGrip grip, object group, string thread, UiPointerEvent e) { _gripActive = grip; if (group is CardGroup g) CardGripDrag(g, thread, e); }
        internal void GripReleased(object group, string thread, UiPointerEvent e) { if (group is CardGroup g) CardGripReleased(g, thread, e); }

        private void EndGripLook(CardGroup g)
        {
            _gripActive = null;
            if (g.Panel != null) foreach (var c in g.Cards) { g.Panel.Shapes.SetTint(c.Bg, UiTheme.Current.AccentSoft); g.Panel.Shapes.SetState(c.Bg, 0.10f); }
            if (_dropTarget != null && _dropSource == null) ClearDropTarget();
        }

        /// A card's grip let go: on another group it joins that one; past its group's edge it becomes a group of one there
        /// (just clear of the group); else nothing changes.
        private void CardGripReleased(CardGroup g, string thread, UiPointerEvent e)
        {
            var p = g.Panel;
            EndGripLook(g);
            if (p == null || !g.Threads.Contains(thread) || g.Threads.Count < 2) return;
            var ray = PointerRay(e.Pointer, Head);
            var target = ray.HasValue ? GroupUnder(ray.Value, g) : null;
            if (target != null && target.Threads.Count >= MaxCardsPerGroup) { Toast($"A group holds up to {MaxCardsPerGroup} threads", "warn"); return; }
            if (target == null && !PastEdge(p, e.Point)) return;
            PullCardOut(g, thread, e.Point, target);
        }

        /// Takes a card out of its group: into `target`, or a new group of one centred just past the edge it was pulled
        /// over at `point` (the source's panel coordinates).
        private void PullCardOut(CardGroup g, string thread, Vector2 point, CardGroup target)
        {
            var p = g.Panel;
            if (p == null) return;
            // where the new group goes: the release point, pushed out until the new window clears the old one's edge
            var t = UiTheme.Current;
            float nw = 2f * t.Padding + CardW, nh = t.HeaderHeight + 2f * t.Padding + CardH, clear = 0.03f;
            var at = point;
            if (point.x > p.Width) at.x = Mathf.Max(point.x, p.Width + clear + nw * 0.5f);
            else if (point.x < 0f) at.x = Mathf.Min(point.x, -clear - nw * 0.5f);
            else if (point.y > p.Height) at.y = Mathf.Max(point.y, p.Height + clear + nh * 0.5f);
            else if (point.y < 0f) at.y = Mathf.Min(point.y, -clear - nh * 0.5f);
            Vector3 world = p.SurfacePoint(at);
            bool open = p.IsOpen;
            g.Threads.Remove(thread);
            if (target != null)
            {
                target.Threads.Add(thread);
                BuildGroupPanel(target, target.Panel);
                target.Panel?.Ping();
            }
            else
            {
                var ng = new CardGroup { N = ++_groupSeq, Open = open };
                ng.Threads.Add(thread);
                _groups.Add(ng);
                BuildGroupPanel(ng, null);
                var head = Head;
                var face = head != null ? world - head.position : p.transform.forward;
                face.y = 0f;
                ng.Panel.transform.SetPositionAndRotation(world, Quaternion.LookRotation(face.sqrMagnitude > 1e-4f ? face.normalized : p.transform.forward, Vector3.up));
                if (open)
                {
                    ng.Panel.Open();
                    _placed.Add(ng.Id);
                    _slots.Remove(ng.Panel.transform); // FLOAT: slotted where it was let go (the nearest free place there), not by the gaze
                }
            }
            BuildGroupPanel(g, p);
            SaveCards();
            CardsChanged();
        }

        // ------------------------------------------------------------------ voice (the command registry)

        /// The card a spoken name means: the best title (or project) match; none said: the card pointed at, the open
        /// thread's, or the only one.
        private string CardByQuery(string query)
        {
            var all = _groups.SelectMany(g => g.Threads).ToList();
            if (all.Count == 0) return null;
            string q = (query ?? "").Trim();
            if (q.Length == 0 || q == "this" || q == "it" || q == "this one" || q == "that one" || q == "this thread")
            {
                var fg = GroupOfPanel(FocusedPanel());
                if (fg != null && fg.Threads.Count == 1) return fg.Threads[0];
                if (_threadOpen != null && all.Contains(_threadOpen)) return _threadOpen;
                return all.Count == 1 ? all[0] : null;
            }
            var best = all.Select(id => (id, s: Mathf.Max(ThreadScore(CardTitle(id), q), ThreadScore(CardProject(id), q) * 0.9f))).OrderByDescending(x => x.s).First();
            return best.s >= 0.34f ? best.id : null;
        }

        private CardGroup GroupInFocus()
        {
            var fg = GroupOfPanel(FocusedPanel());
            if (fg != null) return fg;
            var shown = _groups.Where(g => g.Panel != null && g.Panel.IsOpen).ToList();
            return shown.Count == 1 ? shown[0] : _groups.Count == 1 ? _groups[0] : null;
        }

        /// Commands that need a card exist only while there is one (AGENTS.md rule 2).
        private void UpdateCardVoice()
        {
            if (Voice == null) return;
            bool any = _groups.Count > 0;
            if (any == _cardsVoiceOn) return;
            _cardsVoiceOn = any;
            foreach (var n in new[] { "remove_thread_card", "open_thread_card", "show_thread_cards", "ungroup_thread_cards", "rename_card_group", "card_approval", "reply_to_card" })
                Voice.SetEnabled(n, any);
        }

        private void RegisterCardVoiceCommands()
        {
            var threadParam = ("thread", "string", (string[])null, "words of the thread's title (or its project); none: this thread", false);
            Voice.Register(new VoiceCommand
            {
                Name = "pin_thread_card",
                Description = "Pin a thread as a live card (a small window: its status, how long, its last reply; open, reply, allow or deny from it); without a name, the open thread",
                Parameters = VoiceCommands.Schema(threadParam),
                Phrases = new[]
                {
                    "(pin|keep|make|turn|put) (this|the|that) (thread|conversation|chat) [as|into|to] [a] card",
                    "(pin|keep|make) [a] card (of|for) (this|the|that) (thread|conversation|chat)",
                    "(pin|card) (this|it) [as a card]",
                    "(pin|keep) [the] (thread|conversation|chat) [called|named|about|on] {thread} as [a] card",
                    "(make|add) [a] card (for|of) [the] (thread|conversation|chat) [called|named|about|on] {thread}",
                },
                Samples = new[]
                {
                    "keep this conversation as a little card", "i want a card for this thread", "turn the login thread into a card", "pin the docs thread as a card", "make a live card of this chat",
                },
                Examples = new List<(string, string)> { ("pin this thread as a card", "pin_thread_card"), ("make a card for the thread about payments", "pin_thread_card payments") },
                Vocabulary = new[] { "card", "pin" },
                RunAsync = (a, reply) => VoicePinCard((string)a["thread"], reply),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "remove_thread_card",
                Description = "Remove a thread's card (the thread itself stays); without a name, the card pointed at or the open thread's",
                Parameters = VoiceCommands.Schema(threadParam),
                Phrases = new[]
                {
                    "(remove|delete|drop|unpin|close) [the] card (for|of) [the] {thread}",
                    "(remove|delete|drop|unpin) [the] {thread} card",
                    "(remove|delete|drop|unpin) (this|that|the) card",
                    "unpin (this|the|that) (thread|conversation|chat)",
                },
                Samples = new[]
                {
                    "take away the card of the login thread", "i don't need this card any more", "unpin the docs thread", "drop the card for payments",
                },
                Run = a =>
                {
                    string id = CardByQuery((string)a["thread"]);
                    if (id == null) return "error: no card " + ((string)a["thread"] is string q && q.Length > 0 ? "for “" + q + "”" : "here: say which one");
                    string title = CardTitle(id);
                    RemoveCard(id);
                    return "Removed the card of “" + Short(title, 50) + "”";
                },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "open_thread_card",
                Description = "Open a card's thread in the THREAD window (with its sidebar); the card stays",
                Parameters = VoiceCommands.Schema(threadParam),
                Phrases = new[]
                {
                    "(open|expand|show) [the] card (for|of) [the] {thread}",
                    "(open|expand) [the] card {thread}",
                    "(open|expand) (this|that|the) card",
                    "(open|show) [the] {thread} card",
                },
                Samples = new[]
                {
                    "open the card of the login thread in full", "expand this card", "show me the whole thread of that card", "open the payments card",
                },
                Run = a =>
                {
                    string id = CardByQuery((string)a["thread"]);
                    if (id == null) return "error: no card " + ((string)a["thread"] is string q && q.Length > 0 ? "for “" + q + "”" : "here: say which one");
                    OpenCard(id);
                    return "Opened “" + Short(CardTitle(id), 50) + "”";
                },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "show_thread_cards",
                Description = "Show (open) or hide (close) every group of thread cards; the cards stay",
                Parameters = VoiceCommands.Schema(("action", "string", new[] { "show", "hide" }, "show or hide the card groups", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["action"] = new Dictionary<string, string> { ["open"] = "show", ["bring back"] = "show", ["display"] = "show", ["close"] = "hide", ["put away"] = "hide", ["dismiss"] = "hide" },
                },
                Phrases = new[]
                {
                    "{action} [all] [the|my] [thread] cards",
                    "{action} [all] [the|my] card groups",
                    "{action} [all] [the|my] groups of cards",
                },
                Samples = new[]
                {
                    "bring back my thread cards", "put the cards away", "where are my cards", "close all the card groups", "show the cards again",
                },
                Run = a =>
                {
                    bool show = (string)a["action"] != "hide";
                    int k = 0;
                    foreach (var g in _groups.ToList())
                    {
                        if (g.Panel == null) continue;
                        if (show && !g.Panel.IsOpen) { OpenPanel(g.Id); k++; }
                        else if (!show && g.Panel.IsOpen) { g.Panel.Close(); k++; }
                    }
                    return show ? (k == 0 ? "The cards are shown" : $"Showed {ThreadPulse.Plural(k, "card group")}") : (k == 0 ? "No card is shown" : $"Hid {ThreadPulse.Plural(k, "card group")}");
                },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "ungroup_thread_cards",
                Description = "Split a group of thread cards: each card its own window (the group pointed at, or the only one)",
                Phrases = new[]
                {
                    "(ungroup|split|separate|break up) [the|this|that|these] [card] (group|cards|group of cards)",
                    "(ungroup|split up|separate) (them|these|this)",
                },
                Samples = new[]
                {
                    "split this group of cards", "take the cards apart", "ungroup these threads", "each card on its own again",
                },
                Run = a =>
                {
                    var g = GroupInFocus();
                    if (g == null) return "error: point at the group to split";
                    if (g.Threads.Count < 2) return "That group holds one card";
                    int n = g.Threads.Count;
                    UngroupCards(g);
                    return $"Split into {n} cards";
                },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "rename_card_group",
                Description = "Name a group of thread cards (the group pointed at, or the only one); an empty name removes it",
                Parameters = VoiceCommands.Schema(("name", "string", null, "the group's new name", true)),
                Phrases = new[]
                {
                    "(rename|name|call) (this|the|that) [card] group [to|as] {name}",
                    "(rename|name|call) (these|the) cards [to|as] {name}",
                },
                Samples = new[]
                {
                    "call this group backend work", "rename the card group to release", "name these cards morning tasks", "give this group the name bugs",
                },
                Run = a =>
                {
                    var g = GroupInFocus();
                    if (g == null) return "error: point at the group to name";
                    RenameGroup(g, (string)a["name"]);
                    return "Group named “" + g.Name + "”";
                },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "card_approval",
                Description = "Allow (once) or deny what a card's thread waits for your approval of; without a name, the card that waits",
                Parameters = VoiceCommands.Schema(("decision", "string", new[] { "allow", "deny" }, "allow it once, or deny it", true), threadParam),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["decision"] = new Dictionary<string, string> { ["approve"] = "allow", ["accept"] = "allow", ["permit"] = "allow", ["reject"] = "deny", ["refuse"] = "deny", ["decline"] = "deny", ["block"] = "deny" },
                },
                Phrases = new[]
                {
                    "{decision} [the] card [of|for] [the] {thread}",
                    "{decision} [it|that] (on|in|from) [the] card [of|for] [the] {thread}",
                    "{decision} [it|that] (on|in|from) (the|this|that) card",
                    "{decision} the card",
                },
                Samples = new[]
                {
                    "approve what the login card is asking", "let the docs card run that command", "say no to the card that waits", "refuse the request on that card",
                },
                Run = a =>
                {
                    string q = (string)a["thread"];
                    string id = !string.IsNullOrWhiteSpace(q) ? CardByQuery(q)
                        : _groups.SelectMany(g => g.Threads).FirstOrDefault(x => AsksApproval(CardPulse(x), CardRow(x))) ?? CardByQuery(null);
                    if (id == null) return "error: no card " + (!string.IsNullOrWhiteSpace(q) ? "for “" + q + "”" : "waits for you");
                    if (!AsksApproval(CardPulse(id), CardRow(id))) return "“" + Short(CardTitle(id), 40) + "” waits for nothing";
                    bool allow = (string)a["decision"] != "deny";
                    CardResolve(id, allow ? "allow_once" : "deny");
                    return (allow ? "Allowing" : "Denying") + " for “" + Short(CardTitle(id), 40) + "”";
                },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "reply_to_card",
                Description = "Send a message to a card's thread (with the words said), or open the keyboard to write it",
                Parameters = VoiceCommands.Schema(threadParam, ("message", "string", null, "what to send; none: the keyboard opens", false)),
                Llm = true,
                Phrases = new[]
                {
                    "(tell|say to|write to|message) [the] card [of|for] [the] {thread} (that|to) {message}",
                    "(reply|answer|respond|write) to [the] card [of|for] [the] {thread}",
                    "(reply|answer|respond|write) to (this|that|the) card",
                },
                Samples = new[]
                {
                    "tell the login card to run the tests again", "answer the docs card", "write to this card", "message the payments card that the keys are in the vault",
                },
                Run = a =>
                {
                    string id = CardByQuery((string)a["thread"]);
                    if (id == null) return "error: no card " + ((string)a["thread"] is string q && q.Length > 0 ? "for “" + q + "”" : "here: say which one");
                    string msg = (string)a["message"];
                    if (string.IsNullOrWhiteSpace(msg)) { ReplyCard(id); return "Write it on the keyboard"; }
                    CardSend(id, msg);
                    return "Sending to “" + Short(CardTitle(id), 40) + "”";
                },
            });
            _cardsVoiceOn = true;
            UpdateCardVoice();
        }

        private void VoicePinCard(string query, Action<string> reply)
        {
            string q = (query ?? "").Trim();
            if (q.Length == 0 || q == "this" || q == "it" || q == "this one")
            {
                if (_threadOpen == null) { reply("error: open a thread first, or say which thread"); return; }
                PinCard(_threadOpen);
                reply("Pinned “" + Short(CardTitle(_threadOpen), 50) + "” as a card");
                return;
            }
            Mqh.Threads(null, th =>
            {
                if (!(th is JArray arr)) { reply("error: MQH is not reachable (" + (Mqh.LastError ?? "no answer") + ")"); return; }
                _ctxThreadRows = arr;
                var best = arr.Select(x => new { x, score = ThreadScore(((string)x["title"] ?? (string)x["titleFallback"] ?? "") + " " + (string)x["environmentBranchName"], q) })
                    .OrderByDescending(p => p.score).ThenByDescending(p => p.x["updatedAt"]?.Value<long>() ?? 0).FirstOrDefault();
                if (best == null || best.score < 0.34f) { reply($"error: no thread about “{q}”"); return; }
                string id = (string)best.x["id"];
                PinCard(id);
                reply("Pinned “" + Short(CardTitle(id), 50) + "” as a card");
            });
        }

        // ------------------------------------------------------------------ self-tests

        public IReadOnlyList<string> CardGroupIdsForTest => _groups.Select(g => g.Id).ToList();
        public string OpenThreadIdForTest => _threadOpen;
        public IReadOnlyList<string> CardThreadsForTest(string groupId) => _groups.FirstOrDefault(g => g.Id == groupId)?.Threads.ToList() ?? new List<string>();
        public string CardGroupOfForTest(string thread) => CardOf(thread)?.Id;
        public string CardDropTargetForTest => _dropTarget?.Id;
        public void SetCardPulseForTest(string thread, ThreadPulse p) => CtxPulse(thread, p);
        public void SetCardRowsForTest(JArray rows) { _ctxThreadRows = rows; foreach (var g in _groups) g.Dirty = true; }
        /// A card's texts now: title | meta | status | body (and " +ALLOW" while ALLOW / DENY show).
        public string CardTextForTest(string thread)
        {
            var g = CardOf(thread);
            var c = g?.Cards.FirstOrDefault(x => x.Thread == thread);
            if (c == null) return "";
            if (g.Dirty) RefreshGroup(g);
            return ChatText.Strip(c.Title.Text) + " | " + ChatText.Strip(c.Meta.Text) + " | " + c.Status.Text + " | " + ChatText.Strip(c.Body.Text) + (c.Allow.Visible ? " +ALLOW" : "");
        }
        /// A card's elements (its grip, OPEN, REPLY, ALLOW, DENY, ✕) for clicks.
        public UiElement CardElementForTest(string thread, string which)
        {
            var c = CardOf(thread)?.Cards.FirstOrDefault(x => x.Thread == thread);
            if (c == null) return null;
            switch (which)
            {
                case "grip": return c.Grip;
                case "open": return c.Open;
                case "reply": return c.Reply;
                case "allow": return c.Allow;
                case "deny": return c.Deny;
                case "remove": return c.Remove;
                case "title": return c.Title;
                default: return null;
            }
        }
        public void UngroupForTest(string groupId) => UngroupCards(_groups.FirstOrDefault(g => g.Id == groupId));
        public void RenameGroupForTest(string groupId, string name) => RenameGroup(_groups.FirstOrDefault(g => g.Id == groupId), name);
        /// The groups as the settings keep them, and read back from them (`fromDisk`: from the settings file, as at a
        /// restart: windows made again, the open ones placed again).
        public void SaveCardsForTest() => SaveCards();
        public void ReloadCardsForTest(bool fromDisk)
        {
            if (fromDisk) Settings.threadCards = ShellSettings.Load(_settingsPath).threadCards ?? new List<ThreadCardGroup>();
            foreach (var g in _groups.ToList()) DestroyGroupPanel(g, false);
            _groups.Clear();
            _cardsLoaded = false;
            BuildCardGroups();
            foreach (var g in _groups) if (g.Open) OpenPanel(g.Id);
        }
    }

    /// A card's ✥ grip: pinch it and pull the card out of its group (let go past the group's edge, or on another group).
    public sealed class CardGrip : UiButton
    {
        private readonly MetaCodexShell _shell;
        internal readonly object Group;
        private readonly string _thread;

        internal CardGrip(MetaCodexShell shell, object group, string thread) : base("✥", null, UiButtonStyle.Ghost)
        {
            _shell = shell; Group = group; _thread = thread;
        }

        private Vector2 _last;
        private bool _moved;

        public override void OnPress(UiPointerEvent e) { _last = e.Point; _moved = false; _shell.GripPressed(this); }
        public override void OnDrag(UiPointerEvent e) { _last = e.Point; _moved = true; _shell.GripDragged(this, Group, _thread, e); }
        /// Let go where it was last moved to (the release frame's ray may miss the panel's plane: its point is then empty).
        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            if (_moved) e.Point = _last;
            _moved = false;
            _shell.GripReleased(Group, _thread, e);
        }
    }
}
