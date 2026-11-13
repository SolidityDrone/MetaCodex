using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.Hands;
using MetaCodex.Shell;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `orbplace` (owner 2026-11-11: "avoid any weird position when it can with the menu or stuff cover
    /// my view on the panels i'm watching. have the orb to be less invasive but always available"):
    /// - the window the user looks at is the watched one; the orb, its open menu and its callouts keep off it (they glide
    ///   aside or below, smoothly, and stay in view), and the orb goes back to its own place once that is free;
    /// - FIXED layout: THREAD's sidebar opens and the pair is centred where the user faces when there is room (never over
    ///   another window), and goes back when it folds; not when the user moved the thread meanwhile.
    /// Pictures: ui-orb-watched.png, ui-orb-watched-menu.png, ui-sidebar-pair-fixed.png, ui-sidebar-pair-limited.png.
    public static partial class MetaCodexUiTest
    {
        private static void OrbPlaceSection(MetaCodexShell shell, Transform head, SimulatedHands hands)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            shell.SetWristMenu(true); // no dock in the pictures
            var keepPos = head.position; var keepRot = head.rotation;
            head.SetPositionAndRotation(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            hands.Frames[0].Tracked = false; hands.Frames[1].Tracked = false;
            bool orb0 = shell.Settings.orb, bend0 = shell.Settings.bendPanels;
            var terms = shell.GetComponentsInChildren<UiPanel>(true).Where(x => x.Id != null && x.Id.StartsWith("term-") && x.gameObject.activeSelf).ToList();
            foreach (var tm in terms) tm.gameObject.SetActive(false);
            foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
            shell.Toasts.Tick(float.MaxValue);
            UiPanel.AnimateInEditor = false;
            shell.SetVoiceStateForTest("idle", "", ""); // no voice HUD left from an earlier section in the pictures
            if (shell.Toasts.Pinned != null && shell.Toasts.Pinned.IsOpen) { shell.Toasts.Pinned.Close(); shell.Toasts.Pinned.ForceRefresh(); }
            var orb = shell.Orb;
            void Step(int n, float dt = 0.05f) { for (int i = 0; i < n; i++) { shell.TickGesturesForTest(dt); shell.TickOrbForTest(dt); shell.TickLayoutForTest(dt, 1); } }
            (float yaw, float pitch) Where()
            {
                var rel = orb.Position - head.position;
                return (Mathf.DeltaAngle(Mathf.Atan2(head.forward.x, head.forward.z) * Mathf.Rad2Deg, Mathf.Atan2(rel.x, rel.z) * Mathf.Rad2Deg), Mathf.Asin(rel.y / rel.magnitude) * Mathf.Rad2Deg);
            }
            MetaCodexShell.ViewRect RectOf(UiPanel p) { MetaCodexShell.ViewRectOf(p, head.position, 0f, out var r); return r; }
            // what the orb and its open items really cover (their panels, and its disc)
            List<MetaCodexShell.ViewRect> Covered()
            {
                var list = new List<MetaCodexShell.ViewRect>();
                foreach (var a in shell.OrbActionsForTest) if (a.IsOpen) list.Add(RectOf(a));
                foreach (var c in shell.Toasts.Panels) list.Add(RectOf(c));
                var w = Where();
                float r = Mathf.Atan2(shell.OrbOuterRadiusForTest, Vector3.Distance(orb.Position, head.position)) * Mathf.Rad2Deg;
                list.Add(new MetaCodexShell.ViewRect { Yaw0 = w.yaw - r, Yaw1 = w.yaw + r, Pitch0 = w.pitch - r, Pitch1 = w.pitch + r });
                return list;
            }
            try
            {
                if (!shell.Settings.orb) shell.SetOrb(true);
                shell.ResetOrbPlace();
                Step(60);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var projects = new JArray { new JObject { ["id"] = "p1", ["name"] = "skillset" } };
                JObject T(string id, string title, string st, long ago) => new JObject
                {
                    ["id"] = id, ["projectId"] = "p1", ["title"] = title, ["providerId"] = "codex", ["status"] = st, ["runtime"] = new JObject { ["displayStatus"] = st },
                    ["updatedAt"] = now - ago, ["queuedWork"] = "none", ["hasPendingInteraction"] = false,
                };
                shell.SetWorkspacesForTest(projects, new JArray { T("w1", "Fix the build", "active", 5000), T("w2", "Docs pass", "idle", 60000), T("w3", "Shader fix", "error", 90000), T("w4", "Bench", "idle", 120000), T("w5", "Voice words", "idle", 150000) }, new JArray());

                // a window straight ahead, the one being read: about 44 degrees wide, its top just below the orb's level
                shell.OpenPanel("files");
                var win = shell.PanelsById["files"];
                float halfW = win.Width * win.RestLossyScale.x * 0.5f;
                float d = halfW / Mathf.Tan(22f * Mathf.Deg2Rad);
                win.transform.SetPositionAndRotation(head.position + new Vector3(0f, -0.02f, d), Quaternion.identity);
                win.ForceRefresh();
                Step(30);
                var wr = RectOf(win);
                var home = Where();
                bool watched = shell.WatchedPanel == win;
                bool homeFree = !Covered().Any(c => c.Overlaps(wr, 0f)) && shell.OrbAvoidForTest == Vector2.zero;
                Check(watched && homeFree, $"the window the user looks at is the watched one ({watched}); the orb at its own place ({home.yaw:F0}, {home.pitch:F0} deg) covers nothing of it, so it stays ({homeFree})");

                // the menu opens: at the orb's place it would lie over the window; the orb glides aside (smoothly) until
                // nothing of it, its menu or its cards covers the window, and stays in view
                shell.ToggleOrbMenu();
                Step(1);
                bool wouldCover = Covered().Any(c => c.Overlaps(wr, 0f));
                var prev = orb.Position;
                float maxStep = 0f;
                for (int i = 0; i < 70; i++) { Step(1); maxStep = Mathf.Max(maxStep, Vector3.Distance(orb.Position, prev)); prev = orb.Position; }
                var moved = Where();
                var cov = Covered();
                var hit = cov.Where(c => c.Overlaps(wr, 0f)).ToList();
                var all = cov.Aggregate((a, b) => new MetaCodexShell.ViewRect { Yaw0 = Mathf.Min(a.Yaw0, b.Yaw0), Yaw1 = Mathf.Max(a.Yaw1, b.Yaw1), Pitch0 = Mathf.Min(a.Pitch0, b.Pitch0), Pitch1 = Mathf.Max(a.Pitch1, b.Pitch1) });
                bool inView = Mathf.Abs(moved.yaw) <= 61f && moved.pitch <= 41f && moved.pitch >= -35f;
                Check(wouldCover && hit.Count == 0 && inView && maxStep < 0.03f && shell.OrbMenuOpen,
                    $"the menu would cover the watched window at the orb's place ({wouldCover}); the orb glides to ({moved.yaw:F0}, {moved.pitch:F0} deg; offset {shell.OrbAvoidForTest.x:F0}, {shell.OrbAvoidForTest.y:F0}), never more than {maxStep * 100f:F1} cm a frame, and then the orb and its four squares cover nothing of it ({hit.Count} overlap; the window {wr.Yaw0:F0}..{wr.Yaw1:F0} x {wr.Pitch0:F0}..{wr.Pitch1:F0} deg, the orb's items {all.Yaw0:F0}..{all.Yaw1:F0} x {all.Pitch0:F0}..{all.Pitch1:F0}); in view ({inView})");
                foreach (var a in shell.OrbActionsForTest) a.ForceRefresh();
                RenderAt("ui-orb-watched-menu.png", head.position - Vector3.forward * 0.05f, Quaternion.LookRotation(new Vector3(0.18f, 0.12f, 1f)), 100f, 1600, 1000);

                // looking at the menu keeps the window watched (it does not count as looking elsewhere)
                var keepHead = head.rotation;
                head.rotation = Quaternion.LookRotation(shell.OrbActionsForTest[1].transform.position - head.position);
                Step(8);
                bool stillWatched = shell.WatchedPanel == win;
                head.rotation = keepHead;
                Step(4);
                Check(stillWatched, $"looking at the orb's menu keeps the window watched ({stillWatched})");

                // messages while the menu is up: the callouts on the orb's right keep off the window too
                shell.Toast("Build finished: 0 errors", "ok");
                shell.Toast("Thread “Docs pass” answered: tap to open it", "info");
                Step(30);
                var hit2 = Covered().Where(c => c.Overlaps(wr, 0f)).Count();
                Check(shell.Toasts.Count == 2 && hit2 == 0, $"with two callouts as well, nothing of the orb covers the watched window ({hit2} overlaps)");
                foreach (var c in shell.Toasts.Panels) c.ForceRefresh();
                foreach (var a in shell.OrbActionsForTest) a.ForceRefresh();
                RenderAt("ui-orb-watched.png", head.position - Vector3.forward * 0.05f, Quaternion.LookRotation(new Vector3(0.18f, 0.12f, 1f)), 100f, 1600, 1000);
                foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
                shell.Toasts.Tick(float.MaxValue);

                // the menu closes: once its own place is free, after a moment, it goes back there
                shell.ToggleOrbMenu();
                Step(10);
                bool waits = shell.OrbAvoidForTest != Vector2.zero;
                Step(60);
                var back = Where();
                Check(waits && shell.OrbAvoidForTest == Vector2.zero && Mathf.Abs(back.yaw - home.yaw) < 3f && Mathf.Abs(back.pitch - home.pitch) < 3f,
                    $"the menu closed, it waits a moment ({waits}) and goes back to its own place ({back.yaw:F0}, {back.pitch:F0} deg)");
                shell.ClosePanel("files");
                Step(10);

                // ---- THREAD + sidebar, FIXED: centred where the user faces when the sidebar opens
                shell.Settings.bendPanels = true;
                shell.ForgetSidebarMemoryForTest();
                var row = new JObject
                {
                    ["id"] = "thr_pair", ["projectId"] = "p1", ["providerId"] = "codex", ["title"] = "Centre the pair", ["status"] = "idle",
                    ["runtime"] = new JObject { ["displayStatus"] = "idle" }, ["environmentPath"] = null, ["updatedAt"] = now,
                };
                shell.LoadThreadForTest(row, new JArray(Row("conversation", 1, new JObject { ["role"] = "user", ["text"] = "Keep the pair in front of me" })),
                    new JObject { ["usedTokens"] = 1000, ["modelContextWindow"] = 200000, ["estimated"] = false }, new JObject { ["model"] = "gpt-5", ["reasoningLevel"] = "medium", ["permissionMode"] = "accept-edits" }, projects, false);
                var th = shell.PanelsById["thread"];
                var sb = shell.PanelsById[MetaCodexShell.SidebarId];
                var place = head.position + new Vector3(0f, -0.05f, 0.85f);
                th.transform.SetPositionAndRotation(place, Quaternion.identity);
                Step(20); // bent round the user
                float alone = RectOf(th).YawMid;
                shell.SetSidebar(true);
                Step(1);
                var opened = MetaCodexShell.ViewRectOf(th, head.position, 0f, out var ta) && MetaCodexShell.ViewRectOf(sb, head.position, 0f, out var sa) ? (ta, sa) : default;
                Step(20);
                var a1 = RectOf(th); var s1 = RectOf(sb);
                float mid = (Mathf.Min(a1.Yaw0, s1.Yaw0) + Mathf.Max(a1.Yaw1, s1.Yaw1)) * 0.5f;
                float span = Mathf.Max(a1.Yaw1, s1.Yaw1) - Mathf.Min(a1.Yaw0, s1.Yaw0);
                float before = (Mathf.Min(opened.ta.Yaw0, opened.sa.Yaw0) + Mathf.Max(opened.ta.Yaw1, opened.sa.Yaw1)) * 0.5f;
                bool attached = Vector3.Distance(sb.transform.position, th.transform.position) < th.Width * th.RestLossyScale.x + 0.05f;
                Check(sb.IsOpen && Mathf.Abs(alone) < 1f && before > 8f && Mathf.Abs(mid) < 2f && !shell.PairGlidingForTest && attached,
                    $"FIXED: THREAD alone in front ({alone:F1} deg); its sidebar opens at its right edge (the pair's middle at {before:F0} deg, {span:F0} deg wide), and the pair glides round the user until it is centred ({mid:F1} deg; shift {shell.PairShiftForTest:F0} deg), the sidebar still at the thread's edge ({attached})");
                th.ForceRefresh(); sb.ForceRefresh();
                RenderAt("ui-sidebar-pair-fixed.png", head.position - Vector3.forward * 0.08f, Quaternion.identity, 110f, 1920, 1080);
                shell.SetSidebar(false);
                Step(20);
                Check(!sb.IsOpen && Vector3.Distance(th.transform.position, place) < 0.01f, $"folded: THREAD goes back where it was ({Vector3.Distance(th.transform.position, place) * 100f:F1} cm off)");

                // a window on the left: the pair moves only as far as that window lets it, never over it
                shell.OpenPanel("files");
                var left = shell.PanelsById["files"];
                float lw = left.Width * left.RestLossyScale.x * 0.5f;
                float ly = -50f - Mathf.Atan2(lw, 0.9f) * Mathf.Rad2Deg; // its right edge at about 50 degrees left
                var ldir = Quaternion.Euler(0f, ly, 0f) * Vector3.forward;
                left.transform.SetPositionAndRotation(head.position + ldir * 0.9f + Vector3.down * 0.05f, Quaternion.LookRotation(ldir));
                left.ForceRefresh();
                Step(4);
                var lr = RectOf(left);
                shell.SetSidebar(true);
                Step(20);
                var a2 = RectOf(th); var s2 = RectOf(sb);
                float mid2 = (Mathf.Min(a2.Yaw0, s2.Yaw0) + Mathf.Max(a2.Yaw1, s2.Yaw1)) * 0.5f;
                bool clear = !a2.Overlaps(lr, 1.5f) && !s2.Overlaps(lr, 1.5f);
                float shift2 = shell.PairShiftForTest;
                Check(clear && shift2 < -2.9f && mid2 > 2f, $"a window on the left ({lr.Yaw0:F0}..{lr.Yaw1:F0} deg): the pair moves {shift2:F0} deg, as far as it lets it (its middle at {mid2:F0} deg), and covers nothing of it ({clear}; the thread from {a2.Yaw0:F0} deg)");
                th.ForceRefresh(); sb.ForceRefresh(); left.ForceRefresh();
                RenderAt("ui-sidebar-pair-limited.png", head.position - Vector3.forward * 0.08f, Quaternion.Euler(0f, -22f, 0f), 104f, 1920, 1080);
                shell.SetSidebar(false);
                Step(20);
                bool back2 = Vector3.Distance(th.transform.position, place) < 0.01f;
                shell.ClosePanel("files");
                Step(4);

                // the user moves the thread after the pair was centred: folding leaves it where they put it
                shell.SetSidebar(true);
                Step(20);
                var put = th.transform.position + Vector3.up * 0.10f;
                th.transform.position = put;
                Step(2);
                shell.SetSidebar(false);
                Step(20);
                bool stays = Vector3.Distance(th.transform.position, put) < 0.005f;
                Check(back2 && stays, $"folded again it goes back ({back2}); moved by the user after it was centred, folding leaves it where it was put ({stays})");

                // FLOAT is the ring's business: nothing glides there
                shell.SetLayout("float");
                Step(10);
                shell.SetSidebar(true);
                Step(10);
                bool noGlide = shell.PairShiftForTest == 0f && !shell.PairGlidingForTest;
                shell.SetSidebar(false);
                shell.SetLayout("fixed");
                Step(4);
                Check(noGlide, "FLOAT: the ring places the pair; no centring glide");
            }
            finally
            {
                UiPanel.AnimateInEditor = false;
                shell.CloseOrbMenuForTest();
                foreach (var a in shell.OrbActionsForTest) a.ForceRefresh();
                foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
                shell.Toasts.Tick(float.MaxValue);
                shell.Settings.bendPanels = bend0;
                if (shell.Settings.orb != orb0) shell.SetOrb(orb0);
                if (shell.Floating) shell.SetLayout("fixed");
                foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
                foreach (var tm in terms) if (tm != null) tm.gameObject.SetActive(true);
                shell.SetWristMenu(false);
                head.SetPositionAndRotation(keepPos, keepRot);
                shell.TickOrbForTest(0.05f);
            }
        }
    }
}
