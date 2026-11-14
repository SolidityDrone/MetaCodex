using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Archive and delete for chats (owner 2026-11-14, docs/thread-view.md "Held turns and archive"):
    /// - a THREAD window's ARCHIVE… / DELETE… act on that window's own thread (not the focused one), ask first, and close
    ///   the window. ARCHIVE takes the child threads along (MQH archive-all); DELETE says how many child threads go too;
    /// - THREADS > ▤ ARCHIVED lists the archived threads (one request when switched on); a tap on one: RESTORE or DELETE;
    /// - a window whose thread is archived or deleted elsewhere (another client, an agent) closes on its next poll;
    /// - voice: archive_thread, restore_thread (no delete by voice).
    /// Archived threads are not in MQH's normal list, so they never count as working and are never in the session
    /// context's open windows (their windows close and let the thread go).
    public sealed partial class MetaCodexShell
    {
        private bool _threadsArchived;
        private JArray _archivedThreads = new JArray();
        private UiButton _threadsArchBtn;

        /// ARCHIVE… / DELETE… show only while the current window shows a thread (not a draft, not empty).
        private void UpdateThreadActions()
        {
            bool on = _threadOpen != null;
            _tw.thArchive?.SetVisible(on);
            _tw.thDelete?.SetVisible(on);
        }

        private string TitleOf(string id, JToken row = null)
        {
            row = row ?? _threads.FirstOrDefault(x => (string)x["id"] == id) ?? _archivedThreads.FirstOrDefault(x => (string)x["id"] == id) ?? OpenWindowShowing(id)?.threadInfo;
            return (string)row?["title"] ?? (string)row?["titleFallback"] ?? "this thread";
        }

        // ---- the window's buttons (bound to their window at build time: _threadOpen is that window's thread)

        private void ArchiveThreadPrompt()
        {
            string id = _threadOpen;
            if (id == null) return;
            Modal("Archive this chat?", $"“{Cut(TitleOf(id), 60)}” leaves the list; RESTORE brings it back (THREADS > ARCHIVED). Its child threads are archived with it.",
                new[] { "ARCHIVE", "CANCEL" }, i => { if (i == 0) ArchiveThread(id, null); });
        }

        private void DeleteThreadPrompt()
        {
            string id = _threadOpen;
            if (id == null) return;
            DeleteThreadAsk(id, TitleOf(id));
        }

        /// Archives the thread and its children in MQH, then every window showing one of them lets it go and closes.
        private void ArchiveThread(string id, Action<string> reply)
        {
            string title = TitleOf(id);
            Mqh.ArchiveThread(id, r =>
            {
                if (r == null) { Toast("Not archived: " + Mqh.LastError, "error"); reply?.Invoke("error: not archived (" + Mqh.LastError + ")"); return; }
                var ids = (r["archivedThreadIds"] as JArray)?.Select(x => (string)x).Where(x => x != null).ToList() ?? new List<string>();
                if (!ids.Contains(id)) ids.Add(id);
                ThreadsGone(ids);
                string msg = $"Archived “{Cut(title, 50)}”" + (ids.Count > 1 ? $" and {ThreadPulse.Plural(ids.Count - 1, "child thread")}" : "") + ": RESTORE in THREADS > ARCHIVED";
                Toast(msg, "ok");
                reply?.Invoke(msg);
            });
        }

        /// DELETE: MQH's child count first, then the question with that count; the delete confirms the children only when
        /// the user saw them in the question.
        private void DeleteThreadAsk(string id, string title)
        {
            Mqh.ThreadChildSummary(id, s =>
            {
                if (s == null) { Toast("Not deleted: " + Mqh.LastError, "error"); return; }
                int kids = s["nonDeletedChildCount"]?.Type == JTokenType.Integer ? (int)s["nonDeletedChildCount"] : 0;
                string text = $"“{Cut(title, 60)}” and its messages go; RESTORE cannot bring it back." + (kids > 0 ? $" Its {ThreadPulse.Plural(kids, "child thread")} {(kids == 1 ? "goes" : "go")} too." : " It has no child threads.");
                Modal("Delete this chat for good?", text, new[] { "DELETE", "CANCEL" }, i =>
                {
                    if (i != 0) return;
                    Mqh.DeleteThread(id, kids > 0, r =>
                    {
                        if (r == null) { Toast("Not deleted: " + Mqh.LastError, "error"); return; }
                        _archivedThreads = new JArray(_archivedThreads.Where(x => (string)x["id"] != id));
                        ThreadsGone(new List<string> { id }); // its children's windows close on their next poll (MQH: 404)
                        Toast($"Deleted “{Cut(title, 50)}”" + (kids > 0 ? $" and {ThreadPulse.Plural(kids, "child thread")}" : ""), "ok");
                    });
                });
            });
        }

        /// Threads archived or deleted: out of the list now (they never count as working), their windows closed; then
        /// MQH's list again.
        private void ThreadsGone(ICollection<string> ids)
        {
            foreach (var w in Tws.ToList()) if (!w.Dead && w.threadOpen != null && ids.Contains(w.threadOpen)) DropThreadWindow(w);
            for (int k = _threads.Count - 1; k >= 0; k--) if (ids.Contains((string)_threads[k]["id"])) _threads.RemoveAt(k);
            RebuildWorkspaceMap();
            CtxThreadsUpdated();
            RenderThreadGroups();
            RefreshThreads(); // one request (and the archived list when it is shown)
        }

        /// The window lets its thread go (the chat, the held turn, the state) and closes; the thread stays in MQH.
        private void DropThreadWindow(ThreadWindow w)
        {
            if (w == null || w.Dead) return;
            using (In(w))
            {
                if (_threadOpen != null) _pulledOut.Remove(_threadOpen);
                _threadOpen = null;
                _threadInfo = null;
                _thExec = null;
                _thUsage = null;
                _tlRaw = new JArray();
                _thWait = default;
                SetSendNowVisible(false);
                ResetThreadChat();
                _thTitle?.Set("");
                _thMeta?.Set("");
                UpdateThreadActions();
            }
            if (WinOpen(w)) w.Panel.Close(); // OnThreadWindowClosed: the group, the sidebar, the focus
        }

        /// The current window's thread was not in MQH's list: archived or deleted elsewhere? Asked at most every 10 s
        /// (a hidden thread is not listed either and must not close).
        private void CheckThreadGone(string id)
        {
            if (Time.unscaledTime < _tw.goneCheckAt) return;
            _tw.goneCheckAt = Time.unscaledTime + 10f;
            Mqh.Thread(id, Bind<JToken>(r =>
            {
                if (id != _threadOpen) return;
                string err = Mqh.LastError ?? "";
                bool deleted = r == null && err.Contains("thread_not_found") || r is JObject d && d["deletedAt"] != null && d["deletedAt"].Type != JTokenType.Null;
                bool archived = r is JObject a && a["archivedAt"] != null && a["archivedAt"].Type != JTokenType.Null;
                if (!deleted && !archived) return;
                string title = TitleOf(id, r);
                ThreadsGone(new List<string> { id });
                Toast($"“{Cut(title, 50)}” was {(archived ? "archived" : "deleted")}: its window closed", "info");
            }));
        }

        // ---- THREADS > ARCHIVED

        private void ToggleArchivedThreads()
        {
            _threadsArchived = !_threadsArchived;
            _threadsArchBtn?.SetText(_threadsArchived ? "◂ ACTIVE" : "▤ ARCHIVED");
            _threadsArchBtn?.SetSelected(_threadsArchived);
            if (_threadsArchived) RefreshArchivedThreads();
            else RenderThreadGroups();
        }

        /// One request: the archived threads (when ARCHIVED is switched on, after a RESTORE / DELETE, and on ⟳).
        private void RefreshArchivedThreads()
        {
            if (_threadList == null) return;
            _threadsStatus?.Set("asking MQH for the archived threads …", UiTheme.Current.TextDim);
            Mqh.ThreadsArchived(th =>
            {
                if (th == null) { _threadsStatus?.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable"), UiTheme.Current.Danger); return; }
                _archivedThreads = th as JArray ?? new JArray();
                RenderThreadGroups();
            });
        }

        private void RenderArchivedThreads()
        {
            var t = UiTheme.Current;
            var items = new List<UiListItem>();
            foreach (var x in _archivedThreads.OrderByDescending(x => x["archivedAt"]?.Type == JTokenType.Integer || x["archivedAt"]?.Type == JTokenType.Float ? (long)(double)x["archivedAt"] : 0))
            {
                string pid = (string)x["projectId"];
                string proj = (string)_projects.FirstOrDefault(p => (string)p["id"] == pid)?["name"];
                long at = x["archivedAt"]?.Type == JTokenType.Integer || x["archivedAt"]?.Type == JTokenType.Float ? (long)(double)x["archivedAt"] : 0;
                items.Add(new UiListItem
                {
                    Icon = "▤",
                    IconColor = t.TextDim,
                    Title = (string)x["title"] ?? (string)x["titleFallback"] ?? (string)x["id"],
                    Sub = $"{(proj != null ? proj + " · " : "")}{x["providerId"]}" + (at > 0 ? " · archived " + Ago(at) : ""),
                    Badge = "RESTORE ▸",
                    BadgeColor = t.Accent,
                    Tag = "a:" + (string)x["id"],
                });
            }
            if (items.Count == 0) items.Add(new UiListItem { Icon = "·", IconColor = t.TextDim, Title = "no archived threads", TitleColor = t.TextDim, Tag = "none" });
            _threadsStatus?.Set($"{ThreadPulse.Plural(_archivedThreads.Count, "archived thread")} · tap one: RESTORE or DELETE · ◂ ACTIVE goes back", t.TextDim);
            _threadList.SetItems(items, keepScroll: true);
        }

        private void ArchivedRowPicked(string id)
        {
            var row = _archivedThreads.FirstOrDefault(x => (string)x["id"] == id);
            if (row == null) return;
            string title = TitleOf(id, row);
            Modal(Cut(title, 50), "Archived. RESTORE puts it back in THREADS (its child threads stay archived until restored too). DELETE removes it for good.",
                new[] { "RESTORE", "DELETE…", "CANCEL" }, i =>
                {
                    if (i == 0) RestoreThread(id, null);
                    else if (i == 1) DeleteThreadAsk(id, title);
                });
        }

        private void RestoreThread(string id, Action<string> reply)
        {
            string title = TitleOf(id);
            Mqh.UnarchiveThread(id, r =>
            {
                if (r == null) { Toast("Not restored: " + Mqh.LastError, "error"); reply?.Invoke("error: not restored (" + Mqh.LastError + ")"); return; }
                _archivedThreads = new JArray(_archivedThreads.Where(x => (string)x["id"] != id));
                string msg = $"Restored “{Cut(title, 50)}”";
                Toast(msg, "ok");
                reply?.Invoke(msg);
                RefreshThreads(); // back in the list (and the archived list again when it is shown)
            });
        }

        // ---- voice (the command registry): archive_thread, restore_thread; no delete by voice (owner 2026-11-14)

        private static readonly HashSet<string> ThisThreadWords = new HashSet<string> { "this", "current", "open", "focused", "that", "my", "the" };

        private void RegisterArchiveVoiceCommands()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "archive_thread",
                Description = "Archive an agent thread (chat) and its child threads: it leaves the THREADS list and its window closes; restore_thread brings it back. Without a query: the thread in the focused thread window",
                Parameters = VoiceCommands.Schema(("query", "string", null, "words from the thread's title; empty: the thread in the focused window", false)),
                Phrases = new[]
                {
                    "(archive|shelve|file away) the (thread|chat|conversation) (about|on|called|named|where) {query}",
                    "(archive|shelve|file away) the {query} (thread|chat|conversation)",
                    "(archive|shelve|file away) [this|the|the current|the open|the focused|that] (thread|chat|conversation)",
                    "(put|move) [this|the|the current|that] (thread|chat|conversation) (in|into|to) the archive",
                },
                Samples = new[]
                {
                    "archive this chat", "please archive the thread about the login page", "move this conversation into the archive",
                    "archive the docker thread", "file away the conversation on payments",
                },
                Examples = new List<(string, string)> { ("archive this conversation", "archive_thread"), ("archive the chat about the menu", "archive_thread menu") },
                Vocabulary = new[] { "archive" },
                RunAsync = (a, reply) => VoiceArchiveThread((string)a["query"], reply),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "restore_thread",
                Description = "Restore (unarchive) an archived agent thread by words from its title; it comes back in THREADS. Without a query: THREADS opens on its ARCHIVED list",
                Parameters = VoiceCommands.Schema(("query", "string", null, "words from the archived thread's title; empty: show the archived threads", false)),
                Phrases = new[]
                {
                    "(restore|unarchive|bring back|recover) the (thread|chat|conversation) (about|on|called|named|where) {query}",
                    "(restore|unarchive|bring back|recover) the {query} (thread|chat|conversation)",
                    "(restore|unarchive|bring back|recover) [a|an|the] archived (thread|chat|conversation)",
                    "(show|open|list) [me] [the|my] archived (threads|chats|conversations)",
                },
                Samples = new[]
                {
                    "bring back the chat about the login page", "unarchive the docker conversation", "restore the thread on payments",
                    "show me my archived chats", "recover the archived conversation about the menu",
                },
                Examples = new List<(string, string)> { ("get back the thread about the database", "restore_thread database"), ("which chats did I archive", "restore_thread") },
                Vocabulary = new[] { "unarchive", "archived" },
                RunAsync = (a, reply) => VoiceRestoreThread((string)a["query"], reply),
            });
        }

        private void VoiceArchiveThread(string query, Action<string> reply)
        {
            string q = (query ?? "").Trim();
            if (q.Length == 0 || ThisThreadWords.Contains(VoiceCommands.Simplify(q)))
            {
                var f = WinOpen(_twFocus) ? _twFocus : OpenWindows.FirstOrDefault();
                if (f?.threadOpen == null) { reply("error: no thread is open; say which one to archive"); return; }
                ArchiveThread(f.threadOpen, reply);
                return;
            }
            Mqh.Threads(null, th =>
            {
                if (!(th is JArray arr)) { reply("error: MQH is not reachable (" + (Mqh.LastError ?? "no answer") + ")"); return; }
                var best = arr.Select(x => new { x, score = ThreadScore(((string)x["title"] ?? (string)x["titleFallback"] ?? "") + " " + (string)x["environmentBranchName"], q) })
                    .OrderByDescending(p => p.score).ThenByDescending(p => p.x["updatedAt"]?.Value<long>() ?? 0).FirstOrDefault();
                if (best == null || best.score < 0.34f) { reply($"error: no thread about “{q}” ({arr.Count} threads)"); return; }
                ArchiveThread((string)best.x["id"], reply);
            });
        }

        private void VoiceRestoreThread(string query, Action<string> reply)
        {
            string q = (query ?? "").Trim();
            if (q.Length == 0)
            {
                OpenPanel("threads");
                if (!_threadsArchived) ToggleArchivedThreads(); else RefreshArchivedThreads();
                reply("THREADS shows the archived threads: tap one to restore it");
                return;
            }
            Mqh.ThreadsArchived(th =>
            {
                if (!(th is JArray arr)) { reply("error: MQH is not reachable (" + (Mqh.LastError ?? "no answer") + ")"); return; }
                _archivedThreads = arr;
                var best = arr.Select(x => new { x, score = ThreadScore(((string)x["title"] ?? (string)x["titleFallback"] ?? "") + " " + (string)x["environmentBranchName"], q) })
                    .OrderByDescending(p => p.score).ThenByDescending(p => p.x["archivedAt"]?.Value<long?>() ?? 0).FirstOrDefault();
                if (best == null || best.score < 0.34f) { reply($"error: no archived thread about “{q}” ({arr.Count} archived)"); return; }
                RestoreThread((string)best.x["id"], reply);
            });
        }

        // ---- self-tests

        public void SetArchivedThreadsForTest(JArray rows) { _archivedThreads = rows ?? new JArray(); if (!_threadsArchived) ToggleArchivedThreadsForTest(); else RenderThreadGroups(); }
        /// ARCHIVED on / off without asking MQH (the rows set by SetArchivedThreadsForTest stay).
        public void ToggleArchivedThreadsForTest()
        {
            _threadsArchived = !_threadsArchived;
            _threadsArchBtn?.SetText(_threadsArchived ? "◂ ACTIVE" : "▤ ARCHIVED");
            _threadsArchBtn?.SetSelected(_threadsArchived);
            RenderThreadGroups();
        }
        public bool ArchivedShownForTest => _threadsArchived;
        /// As if MQH answered an archive of these threads: their windows close, they leave the list.
        public void ThreadsGoneForTest(params string[] ids) => ThreadsGone(ids);
        /// The ARCHIVE… / DELETE… buttons of a window (by id): visible?
        public bool ThreadActionsVisibleForTest(string windowId)
        {
            var w = Tws.FirstOrDefault(x => x.Id == windowId);
            return w?.thArchive != null && w.thArchive.Visible && w.thDelete != null && w.thDelete.Visible;
        }
    }
}
