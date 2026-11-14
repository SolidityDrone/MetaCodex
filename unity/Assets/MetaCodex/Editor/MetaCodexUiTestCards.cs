using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace MetaCodex.EditorTools
{
    /// Self-test section `cards` (owner 2026-11-14, docs/thread-view.md "Thread cards"): two threads pinned as cards
    /// (THREAD's ⊞ CARD and a THREADS row's ⊞), their live status, a drop of one group on the other (they merge: one
    /// panel, two cards, it moves as one), a card pulled out by its grip (a group of one again), OPEN loads the THREAD
    /// window, REPLY sends through the system keyboard, the voice commands, and the groups through a restart.
    public static partial class MetaCodexUiTest
    {
        private static void CardsSection(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            foreach (var g in shell.CardGroupIdsForTest.ToList()) foreach (var t in shell.CardThreadsForTest(g).ToList()) shell.RemoveCard(t);
            const long now = 1_800_000_000_000;
            shell.SetClockForTest(now);
            JObject T(string id, string title, string st, bool ask = false) => new JObject
            {
                ["id"] = id, ["projectId"] = "prj_cards", ["providerId"] = "claude-code", ["title"] = title, ["status"] = st,
                ["runtime"] = new JObject { ["displayStatus"] = st }, ["environmentPath"] = "/root/work/cards", ["updatedAt"] = now - 60000, ["hasPendingInteraction"] = ask,
            };
            var rowA = T("thr_cA", "Fix the login form", "active");
            var rowB = T("thr_cB", "Write the release notes", "idle");
            var projects = new JArray { new JObject { ["id"] = "prj_cards", ["name"] = "cards", ["sources"] = new JArray() } };
            shell.LoadThreadForTest(rowA, new JArray { Row("conversation", 1, new JObject { ["role"] = "user", ["text"] = "Fix the login form" }) }, null, null, projects, false);

            // A from THREAD's ⊞ CARD, B from its THREADS row's ⊞
            var cardBtn = shell.PanelsById["thread"].Elements.OfType<UiButton>().FirstOrDefault(b => b.Text == "⊞ CARD");
            cardBtn?.Click();
            shell.OpenPanel("threads");
            shell.SetThreadsForTest(projects, new JArray { rowA, rowB });
            var list = shell.ThreadListForTest;
            int ib = list.Items.ToList().FindIndex(i => i.Tag as string == "t:thr_cB");
            bool rowMarks = ib >= 0 && list.Items[ib].Action == "⊞" && list.Items.Any(i => i.Tag as string == "t:thr_cA" && i.Action == "▣");
            list.ActionIndex(ib);
            shell.ClosePanel("threads");
            shell.SetCardRowsForTest(new JArray { rowA, rowB });
            string gA = shell.CardGroupOfForTest("thr_cA"), gB = shell.CardGroupOfForTest("thr_cB");
            Check(cardBtn != null && rowMarks && gA != null && gB != null && gA != gB && shell.CardGroupIdsForTest.Count == 2 && shell.IsPanelOpen(gA) && shell.IsPanelOpen(gB),
                $"pin: THREAD's ⊞ CARD and a THREADS row's ⊞ make two cards, each its own open window ({gA}, {gB}; row marks ⊞ / ▣ {rowMarks})");

            shell.ClosePanel("thread"); // the open thread's card shows THREAD's own pulse: closed, the cards show these
            // live data: working with its clock, waiting for an approval (ALLOW / DENY), answered with how long ago
            shell.SetCardPulseForTest("thr_cA", new ThreadPulse { State = "working", Doing = "running `npm test`", LastUpdate = "3 tests left", TurnStart = now - 75000, Title = "Fix the login form" });
            shell.SetCardPulseForTest("thr_cB", new ThreadPulse { State = "idle", AnsweredAt = now - 180000, AnswerGist = "The notes are in CHANGELOG.md" });
            string ta = shell.CardTextForTest("thr_cA"), tb = shell.CardTextForTest("thr_cB");
            Check(ta.StartsWith("Fix the login form | cards") && ta.Contains("WORKING · 1:15") && ta.Contains("Running `npm test`") && !ta.Contains("+ALLOW")
                  && tb.Contains("ANSWERED · 3 min ago") && tb.Contains("CHANGELOG.md"),
                $"a card shows title, project, status with how long and the last line (“{ta}” / “{tb}”)");
            shell.SetCardPulseForTest("thr_cA", new ThreadPulse { State = "waiting", Doing = "waiting for your approval: rm -rf build", PhaseStart = now - 12000 });
            ta = shell.CardTextForTest("thr_cA");
            Check(ta.Contains("WAITING FOR YOU · 0:12") && ta.EndsWith("+ALLOW") && shell.CardElementForTest("thr_cA", "deny").Visible,
                $"an approval: WAITING FOR YOU and ALLOW / DENY on the card (“{ta}”)");
            shell.SetCardPulseForTest("thr_cA", new ThreadPulse { State = "working", Doing = "editing login.ts", TurnStart = now - 5000 });

            // merge: B's window moved by its header and let go on A's: A keeps its place, B's card is appended
            var pa = shell.PanelsById[gA]; var pb = shell.PanelsById[gB];
            pa.transform.SetPositionAndRotation(head.position + new Vector3(-0.22f, -0.05f, 0.9f), Quaternion.identity);
            pb.transform.SetPositionAndRotation(head.position + new Vector3(0.22f, -0.05f, 0.9f), Quaternion.identity);
            var posA = pa.transform.position;
            var dir = Vector3.forward;
            var hp = pb.transform.TransformPoint(pb.PanelToLocal(new Vector2(0.08f, UiTheme.Current.HeaderHeight * 0.5f)));
            var ptr = new UiPointer { Id = 52, Kind = PointerKind.Test, Hand = "test", Active = true, Ray = new Ray(hp - dir * 0.4f, dir) };
            shell.Router.Inject(ptr);
            ptr.Down = true; shell.Router.Inject(ptr);
            bool dragging = shell.Router.IsDragging(pb);
            var onA = pa.transform.TransformPoint(pa.PanelToLocal(new Vector2(pa.Width * 0.5f, pa.Height * 0.6f)));
            ptr.Ray = new Ray(onA - dir * 0.4f, dir); shell.Router.Inject(ptr);
            shell.Router.Inject(ptr);
            string lit = shell.CardDropTargetForTest;
            ptr.Down = false; shell.Router.Inject(ptr);
            var merged = shell.CardThreadsForTest(gA);
            Check(dragging && lit == gA && merged.SequenceEqual(new[] { "thr_cA", "thr_cB" }) && shell.CardGroupIdsForTest.Count == 1 && !shell.PanelsById.ContainsKey(gB)
                  && (shell.PanelsById[gA].transform.position - posA).magnitude < 0.001f && shell.CardDropTargetForTest == null,
                $"drop a group on another: lit while over it ({lit}), they merge into {gA} ({string.Join(", ", merged)}), the target kept its place");

            // one panel, two cards side by side, moving as one
            pa = shell.PanelsById[gA];
            var oa = shell.CardElementForTest("thr_cA", "open"); var ob = shell.CardElementForTest("thr_cB", "open");
            Vector3 W(UiElement e) => e.Panel.SurfacePoint(e.Rect.center);
            var wa0 = W(oa); var wb0 = W(ob);
            pa.transform.position += new Vector3(0.1f, 0.05f, -0.1f);
            var wa1 = W(oa); var wb1 = W(ob);
            bool oneWindow = oa.Panel == pa && ob.Panel == pa && pa.Width > 2f * MetaCodexShell.CardW && pa.Width < 3f * MetaCodexShell.CardW
                             && shell.CardElementForTest("thr_cB", "grip") != null && (wa1 - wa0 - new Vector3(0.1f, 0.05f, -0.1f)).magnitude < 0.001f && (wb1 - wb0 - (wa1 - wa0)).magnitude < 0.001f;
            Check(oneWindow, $"the group is one window {pa.Width:F2} x {pa.Height:F2} m with both cards (each with a grip) and moves as one");
            pa.ForceRefresh();
            RenderPanel(pa, "ui-cards.png");

            // pull B out by its grip past the right edge: a group of one there; A's window narrows and stays
            var grip = shell.CardElementForTest("thr_cB", "grip");
            var posA2 = pa.transform.position;
            Click(shell.Router, pa, grip.Rect.center, new Vector2(pa.Width + 0.10f, grip.Rect.center.y));
            string gB2 = shell.CardGroupOfForTest("thr_cB");
            var pb2 = gB2 != null && shell.PanelsById.TryGetValue(gB2, out var q) ? q : null;
            pa = shell.PanelsById[gA];
            float side = pb2 != null ? pa.transform.InverseTransformPoint(pb2.transform.position).x : 0f;
            Check(gB2 != null && gB2 != gA && shell.CardThreadsForTest(gA).SequenceEqual(new[] { "thr_cA" }) && pb2 != null && pb2.IsOpen
                  && side > pa.Width * 0.5f && (pa.transform.position - posA2).magnitude < 0.001f && pa.Width < 1.5f * MetaCodexShell.CardW,
                $"pull a card out by its grip: a group of one ({gB2}) right of the old one ({side:F2} m), the old one keeps its place with one card");

            // OPEN: the card's thread in the THREAD window; REPLY: the system keyboard sends to that thread
            (shell.CardElementForTest("thr_cB", "open") as UiButton)?.Click();
            Check(shell.OpenThreadIdForTest == "thr_cB" && shell.IsPanelOpen("thread") && shell.CardGroupOfForTest("thr_cB") != null,
                $"OPEN loads the card's thread in the THREAD window (open: {shell.OpenThreadIdForTest}); the card stays");
            var keepProvider = UiKeyboard.EditorProvider;
            UiKeyboard.EditorProvider = null;
            SystemKeyboard.FakeForTest = true;
            try
            {
                SystemKeyboard.FakeStatus = TouchScreenKeyboard.Status.Visible;
                (shell.CardElementForTest("thr_cA", "reply") as UiButton)?.Click();
                bool kb = SystemKeyboard.IsOpen;
                SystemKeyboard.FakeText = "run the tests again"; UiKeyboard.Pump();
                SystemKeyboard.FakeStatus = TouchScreenKeyboard.Status.Done; UiKeyboard.Pump();
                var sent = shell.LastCardSendForTest;
                Check(kb && sent.thread == "thr_cA" && sent.text == "run the tests again", $"REPLY opens the system keyboard; Done sends to that thread ({sent.thread}: “{sent.text}”)");
            }
            finally
            {
                SystemKeyboard.Close();
                SystemKeyboard.FakeForTest = false;
                SystemKeyboard.FakeStatus = TouchScreenKeyboard.Status.Visible;
                UiKeyboard.EditorProvider = keepProvider;
            }

            // voice: complete commands, the fast path, and one run
            var names = new[] { "pin_thread_card", "remove_thread_card", "open_thread_card", "show_thread_cards", "ungroup_thread_cards", "rename_card_group", "card_approval", "reply_to_card" };
            var lint = names.SelectMany(n => shell.Voice.Find(n) == null ? new[] { n + ": missing" } : VoiceCommands.Lint(shell.Voice.Find(n)).Select(w => n + ": " + w)).ToList();
            Check(lint.Count == 0, lint.Count == 0 ? "registry: the 8 card commands are complete (lint clean)" : "registry: " + string.Join("; ", lint));
            string M(string said) { var m = shell.Voice.FastMatch(said); return m == null ? "none" : (m.Command.Name + " " + string.Join(" ", m.Args.Properties().OrderBy(p => p.Name).Select(p => p.Value.ToString()))).Trim(); }
            var says = new (string text, string want)[]
            {
                ("pin this thread as a card", "pin_thread_card"), ("remove the card for release notes", "remove_thread_card release notes"),
                ("open the card login form", "open_thread_card login form"), ("hide the thread cards", "show_thread_cards hide"),
            };
            var wrong = says.Where(s => M(s.text) != s.want).Select(s => $"“{s.text}” -> {M(s.text)}").ToList();
            Check(wrong.Count == 0, wrong.Count == 0 ? "voice fast path: pin / remove / open / hide the cards" : "voice: " + string.Join("; ", wrong));
            var om = shell.Voice.FastMatch("open the card login form");
            string said1 = om?.Command.Run?.Invoke(om.Args);
            Check(shell.OpenThreadIdForTest == "thr_cA", $"“open the card login form” opens it in THREAD (“{said1}”, open: {shell.OpenThreadIdForTest})");

            // persistence: names, members in order and open state through the settings file
            foreach (var id in shell.PanelIds.Where(x => !x.StartsWith(MetaCodexShell.CardGroupPrefix)).ToList()) shell.ClosePanel(id);
            gA = shell.CardGroupOfForTest("thr_cA"); gB2 = shell.CardGroupOfForTest("thr_cB");
            pa = shell.PanelsById[gA]; pb2 = shell.PanelsById[gB2];
            hp = pa.transform.TransformPoint(pa.PanelToLocal(new Vector2(0.08f, UiTheme.Current.HeaderHeight * 0.5f)));
            dir = pa.transform.forward;
            ptr = new UiPointer { Id = 53, Kind = PointerKind.Test, Hand = "test", Active = true, Ray = new Ray(hp - dir * 0.4f, dir) };
            shell.Router.Inject(ptr); ptr.Down = true; shell.Router.Inject(ptr);
            var onB = pb2.transform.TransformPoint(pb2.PanelToLocal(new Vector2(pb2.Width * 0.5f, pb2.Height * 0.6f)));
            ptr.Ray = new Ray(onB - pb2.transform.forward * 0.4f, pb2.transform.forward); shell.Router.Inject(ptr);
            ptr.Down = false; shell.Router.Inject(ptr);
            shell.RenameGroupForTest(gB2, "Release");
            var before = shell.CardThreadsForTest(gB2).ToList();
            shell.SaveCardsForTest();
            shell.ReloadCardsForTest(true);
            var ids = shell.CardGroupIdsForTest;
            var back = ids.Count == 1 ? shell.CardThreadsForTest(ids[0]).ToList() : null;
            string title = ids.Count == 1 ? shell.PanelsById[ids[0]].Title : "";
            Check(before.SequenceEqual(new[] { "thr_cB", "thr_cA" }) && back != null && back.SequenceEqual(before) && title.ToUpperInvariant().Contains("RELEASE") && shell.IsPanelOpen(ids[0]),
                $"through a restart: the group, its name and its threads in order (“{title}”: {string.Join(", ", back ?? before)}), open again");
            shell.PanelsById[ids[0]].Close();
            shell.ReloadCardsForTest(true);
            Check(shell.CardGroupIdsForTest.Count == 1 && !shell.IsPanelOpen(shell.CardGroupIdsForTest[0]), "a closed group stays closed through a restart (the cards stay)");

            // ✕ on each card: the group goes with its last card, and the card commands are withdrawn
            shell.OpenPanel(shell.CardGroupIdsForTest[0]);
            (shell.CardElementForTest("thr_cA", "remove") as UiButton)?.Click();
            (shell.CardElementForTest("thr_cB", "remove") as UiButton)?.Click();
            Check(shell.CardGroupIdsForTest.Count == 0 && shell.Settings.threadCards.Count == 0 && !shell.Voice.Find("remove_thread_card").Enabled && shell.Voice.Find("pin_thread_card").Enabled,
                "✕ removes a card; no card left: no group, nothing saved, the card commands withdrawn (pin stays)");
            shell.SetClockForTest(0);
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }
    }
}
