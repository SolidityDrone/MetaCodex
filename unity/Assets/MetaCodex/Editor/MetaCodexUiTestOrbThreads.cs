using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `orbthreads` (owner 2026-11-11, docs/ui.md "The orb"): the orb's billboard opens with the five
    /// threads used last under it, one card each turned to the user: PROJECT · title · state, coloured by state (working
    /// blue and pulsing, done green, needs you orange, error red), every text cut to its column. A tap shows that
    /// thread's window or hides it; the rows keep their places while the menu is up; the next time the thread used last
    /// comes first. Canned MQH rows, no MQH.
    public static partial class MetaCodexUiTest
    {
        private static void OrbThreadsSection(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            var keepPos = head.position; var keepRot = head.rotation;
            head.SetPositionAndRotation(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            bool ws0 = shell.Settings.threadWorkspace, orb0 = shell.Settings.orb;
            shell.Settings.threadWorkspace = false; // THREAD alone (no workspace module beside it)
            var t = UiTheme.Current;
            void Step(int n, float dt = 0.05f) { for (int i = 0; i < n; i++) shell.TickOrbForTest(dt); }
            try
            {
                if (!shell.Settings.orb) shell.SetOrb(true);
                Step(30); // lit and in its place; a touch counts again
                foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
                shell.Toasts.Tick(float.MaxValue);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var projects = new JArray
                {
                    new JObject { ["id"] = "p1", ["name"] = "skillset" },
                    new JObject { ["id"] = "p2", ["name"] = "metacodex-with-a-much-too-long-name" },
                };
                JObject T(string id, string pid, string title, string st, long ago, bool asks = false) => new JObject
                {
                    ["id"] = id, ["projectId"] = pid, ["title"] = title, ["providerId"] = "codex", ["status"] = st, ["runtime"] = new JObject { ["displayStatus"] = st },
                    ["updatedAt"] = now - ago, ["queuedWork"] = "none", ["hasPendingInteraction"] = asks,
                };
                var threads = new JArray
                {
                    T("q1", "p1", "Fix the build", "active", 5000),
                    T("q2", "p2", "A thread title that is far too long to fit in the column of its card", "idle", 240000),
                    T("q3", "p1", "Ask before deleting", "active", 8000, asks: true),
                    T("q4", "p1", "Broken deploy", "error", 60000),
                    T("q6", "p1", "Old one", "idle", 86400000L + 60000),
                    T("q7", "p2", "Older one", "idle", 2 * 86400000L),
                };
                shell.ClearThreadUseForTest();
                shell.SetWorkspacesForTest(projects, threads, new JArray());
                shell.NoteThreadUsedForTest("q6", now - 1000); // used here a second ago: it comes first
                // q1 works (its turn started 3:12 ago); q2 answered in the background, its window not open
                shell.PulseThreadForTest("q1", new ThreadPulse { Title = "Fix the build", State = "working", TurnStart = now - 192000 });
                shell.PulseThreadForTest("q2", new ThreadPulse { Title = "A thread title", State = "working", TurnStart = now - 300000 });
                shell.PulseThreadForTest("q2", new ThreadPulse { Title = "A thread title", State = "idle" });
                foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy(); // its "answered" callout
                shell.Toasts.Tick(float.MaxValue);

                // a touch on the orb: the billboard and, under it, five cards
                if (shell.OrbMenu != null && shell.OrbMenu.IsOpen) { shell.ToggleOrbMenu(); Step(12); }
                shell.ToggleOrbMenu();
                Step(4);
                var menu = shell.OrbMenu;
                var cards = shell.OrbThreadCardsForTest;
                var qs = shell.OrbThreadsForTest;
                var ids = qs.Select(q => q.Id).ToList();
                Check(menu != null && menu.IsOpen && ids.SequenceEqual(new[] { "q6", "q1", "q3", "q4", "q2" }) && cards.Count(c => c.IsOpen) == 5,
                    "the orb's billboard opens with five cards under it: the thread used here last (q6), then the latest (q1, q3, q4, q2); the sixth is not listed: " + string.Join(" ", ids));
                var byId = qs.ToDictionary(q => q.Id);
                Check(byId["q1"].State == QuickState.Working && byId["q1"].Word.StartsWith("WORKING 3:1") && byId["q3"].State == QuickState.NeedsYou && byId["q3"].Word == "NEEDS YOU"
                      && byId["q4"].State == QuickState.Error && byId["q2"].State == QuickState.Done && byId["q2"].Word == "DONE · NEW" && byId["q6"].Word == "DONE · 1 D",
                    $"states: {string.Join(" | ", qs.Select(q => q.Id + " " + q.Word))}");
                bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.02f;
                var colours = new[] { t.Ok, t.Accent, t.Warn, t.Danger, t.Ok };
                bool coloured = Enumerable.Range(0, 5).All(k => Near(shell.OrbCardLabelsForTest(k)[2].Label.Color, colours[k]));
                bool frames = Near(cards[1].FrameTintNow, Color.Lerp(t.Frame, t.Accent, 0.8f)) && Near(cards[3].FrameTintNow, Color.Lerp(t.Frame, t.Danger, 0.8f)) && Near(cards[2].FrameTintNow, Color.Lerp(t.Frame, t.Warn, 0.8f));
                Check(coloured && frames, $"colours: done green, working blue, needs you orange, error red (status and frame: {coloured}, {frames})");
                var texts = shell.OrbCardTextsForTest(4);
                Check(texts.project.Contains("METACODEX") && texts.title.Contains("A thread title") && texts.status == "DONE · NEW", $"a card: PROJECT · title · state ({texts.project} | {texts.title} | {texts.status})");

                // working pulses (blue), the others keep their light
                var lit = new List<float>(); var still = new List<float>();
                for (int i = 0; i < 16; i++) { Step(1); lit.Add(cards[1].HighlightNow); still.Add(cards[3].HighlightNow); }
                Check(lit.Max() - lit.Min() > 0.3f && still.Max() - still.Min() < 0.01f, $"the working card pulses ({lit.Min():F2}..{lit.Max():F2}); the error card does not ({still.Min():F2}..{still.Max():F2})");

                // under the billboard, in its plane, turned to the user, in order
                var up = menu.transform.up;
                bool stacked = true, facing = true;
                for (int k = 0; k < 5; k++)
                {
                    var c = cards[k];
                    float below = Vector3.Dot(menu.transform.position - c.transform.position, up);
                    float expect = (MetaCodexShell.BillboardH * 0.5f + MetaCodexShell.OrbCardGap + MetaCodexShell.OrbCardH * 0.5f + k * (MetaCodexShell.OrbCardH + MetaCodexShell.OrbCardGap)) * menu.transform.lossyScale.y;
                    stacked &= Mathf.Abs(below - expect) < 0.002f && Mathf.Abs(Vector3.Dot(c.transform.position - menu.transform.position, menu.transform.right)) < 0.002f && Quaternion.Angle(c.transform.rotation, menu.transform.rotation) < 0.5f;
                    facing &= Vector3.Angle(c.transform.forward, c.transform.position - head.position) < 25f; // one plane with the billboard: the lowest card is seen ~15 deg off
                }
                Check(stacked && facing, $"the cards hang under the billboard in its plane, one under the other ({stacked}), turned to the user ({facing})");

                // nothing leaves its column: PROJECT | title | state, each cut with … at its edge
                bool inside = true, cut = false;
                string worst = "";
                for (int k = 0; k < 5; k++)
                {
                    var ls = shell.OrbCardLabelsForTest(k);
                    for (int j = 0; j < 3; j++)
                    {
                        var tmp = ls[j].Label.Tmp;
                        tmp.ForceMeshUpdate();
                        var b = tmp.textBounds;
                        bool ok = b.size.x <= 0f || b.min.x >= -0.0006f && b.max.x <= ls[j].Rect.width + 0.0006f;
                        bool cols = ls[j].Rect.xMax <= cards[k].Width + 1e-4f && (j == 2 || ls[j].Rect.xMax <= ls[j + 1].Rect.x + 1e-4f);
                        if (!ok || !cols) { inside = false; worst = $"card {k} col {j}: {b.min.x:F4}..{b.max.x:F4} in {ls[j].Rect.width:F4}"; }
                        if (k == 4 && j < 2) cut |= tmp.isTextTruncated;
                    }
                }
                Check(inside && cut, $"every text stays in its column ({(inside ? "yes" : worst)}); the long project and title are cut with … ({cut})");
                foreach (var c in cards) c.ForceRefresh();
                menu.ForceRefresh();
                var mid = menu.transform.position - menu.transform.up * 0.08f * menu.transform.lossyScale.y;
                RenderAt("ui-orb-threads.png", head.position, Quaternion.LookRotation(mid - head.position, Vector3.up), 30f, 1400, 1000);

                // pointing at a card: "this thread" is that thread
                shell.PointAtForTest(cards[2]);
                string ctx = shell.SessionContextFor("what is this thread doing?");
                Check(ctx.Contains("“Ask before deleting”"), "pointing at a card, “this thread” is its thread (the session context)");

                // a tap shows the thread's window; the menu stays up; the same tap again hides it
                shell.TapOrbThreadForTest(1);
                bool shown = shell.IsPanelOpen("thread") && shell.OpenThreadIdForTest == "q1" && menu.IsOpen;
                bool marked = Near(shell.OrbCardLabelsForTest(1)[1].Label.Color, t.TextAccent);
                var orderNow = shell.OrbThreadsForTest.Select(q => q.Id).ToList();
                shell.TapOrbThreadForTest(1);
                bool hidden = !shell.IsPanelOpen("thread") && menu.IsOpen;
                Check(shown && marked && hidden && orderNow.SequenceEqual(ids), $"a tap shows THREAD on q1 ({shown}; its card marked: {marked}), the menu stays, the rows keep their places; again: hidden ({hidden})");
                // q2's answer, seen: no longer new
                shell.TapOrbThreadForTest(4);
                bool seen = shell.OpenThreadIdForTest == "q2" && shell.OrbThreadsForTest[4].Word == "DONE · 4 MIN";
                shell.ClosePanel("thread");
                Check(seen, "opening q2 shows its answer: DONE · 4 MIN, no longer NEW (" + shell.OrbThreadsForTest[4].Word + ")");

                // closed with a touch: the cards go with it; opened again: the thread used last comes first
                Step(12);
                shell.ToggleOrbMenu();
                Step(2);
                bool gone = !menu.IsOpen && cards.All(c => !c.IsOpen);
                Step(12);
                shell.ToggleOrbMenu();
                Step(2);
                var again = shell.OrbThreadsForTest.Select(q => q.Id).ToList();
                Check(gone && again.SequenceEqual(new[] { "q2", "q1", "q6", "q3", "q4" }), $"the cards go with the billboard ({gone}); next time the threads used last come first: {string.Join(" ", again)}");
                Step(12);
                shell.ToggleOrbMenu();
                Step(2);
            }
            finally
            {
                if (shell.OrbMenu != null && shell.OrbMenu.IsOpen) { shell.OrbMenu.Close(); shell.OrbMenu.ForceRefresh(); }
                foreach (var c in shell.OrbThreadCardsForTest) if (c != null && c.Transitioning) c.ForceRefresh();
                foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
                shell.Toasts.Tick(float.MaxValue);
                shell.ClearThreadUseForTest();
                shell.Settings.threadWorkspace = ws0;
                if (shell.Settings.orb != orb0) shell.SetOrb(orb0);
                foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
                head.SetPositionAndRotation(keepPos, keepRot);
                shell.TickOrbForTest(0.05f);
            }
        }
    }
}
