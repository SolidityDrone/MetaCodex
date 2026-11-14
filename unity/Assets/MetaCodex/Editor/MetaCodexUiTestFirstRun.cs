using System;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `firstrun` (owner 2026-11-14, ShellFirstRun / ShellSetupLocks / ShellTour): the first start's
    /// one-time SETUP panel, its locks and the guided tour, with steps the test sets (the Editor has no Linux, no AI
    /// service, no MQH). Not a first start in the Editor: the flag is set at once. Then a simulated first start: the panel
    /// shows the steps one at a time (✓ and their time, a failed one with what still works), TALK and the windows that
    /// need a step not over are locked with one callout and lift by themselves, the end says "Setup done in N s" and sets
    /// the flag; the tour waits for each action, skips, finishes; its voice command lints clean and matches.
    public static partial class MetaCodexUiTest
    {
        private static void FirstRunSection(MetaCodexShell shell, Transform head)
        {
            Check(shell.Settings.firstStartDone && !shell.SetupActive, "first run: the Editor is never a first start (flag set at once, no setup)");

            // ---- a first start, steps set by the test
            shell.BeginFirstRunForTest();
            shell.TickFirstRunForTest(0.1f);
            var sp = shell.SetupPanel;
            Check(shell.SetupActive && sp != null && sp.IsOpen && sp.Title == MetaCodexShell.SetupTitle && !sp.Closable,
                $"first run: the SETUP panel shows ({sp?.Title}), not closable while it runs");
            Check(Labels(sp).Any(l => l.Contains("takes about a minute") && l.Contains("Nothing is downloaded")), "first run: the header says once, about a minute, nothing downloaded");
            Check(Labels(sp).Any(l => l == "STEP 1 OF 5 · ONE-TIME SETUP") && Labels(sp).Any(l => l == "Unpacking the AI models"), "first run: step 1 of 5 on show with its action name");

            // ---- locks while nothing is done
            int notes = shell.Toasts.History.Count;
            shell.VoiceToggle();
            Check(!shell.Recorder.Recording && shell.Toasts.History.Count == notes + 1 && shell.Toasts.History[0].Text == MetaCodexShell.LockText(5, "TALK"),
                $"first run: TALK is locked: no recording, one callout ({(shell.Toasts.History.Count > notes ? shell.Toasts.History[0].Text : "none")})");
            notes = shell.Toasts.History.Count;
            shell.VoiceToggle();
            Check(shell.Toasts.History.Count == notes, "first run: TALK again at once: no second callout");
            Check(shell.RunAction("voice:listen") == null && !shell.Recorder.Recording, "first run: a gesture binding's voice:listen is locked too");
            shell.OpenPanel("threads");
            shell.OpenPanel("terminals");
            shell.OpenPanel("files");
            Check(!shell.IsPanelOpen("threads") && !shell.IsPanelOpen("terminals") && !shell.IsPanelOpen("files"), "first run: THREADS, TERMINALS and FILES stay shut while their step runs");
            Check(!shell.OpenControl("agents"), "first run: CONTROL > AGENTS is locked");
            shell.OpenPanel("clipboard");
            shell.OpenControl(null);
            Check(shell.IsPanelOpen("clipboard") && shell.ControlRailPage != null, "first run: CLIP and CONTROL stay usable");
            shell.ClosePanel("clipboard");
            shell.ClosePanel(MetaCodexShell.ControlId);
            var dock = shell.PanelsById.TryGetValue("dock", out var dp) ? dp : null;
            UiButton Tile(string label) => dock?.Elements.OfType<UiButton>().FirstOrDefault(b => b.Text != null && b.Text.EndsWith("\n" + label));
            Check(Tile("TALK") != null && Tile("TALK").Dimmed && Tile("TALK").Text.StartsWith("🔒") && Tile("THREADS").Dimmed && Tile("TERMS").Dimmed && !Tile("WEB").Dimmed && !Tile("CLIP").Dimmed,
                "first run: the dock's TALK, THREADS, TERMS show 🔒 dimmed; WEB and CLIP do not");
            var wristTalk = shell.Wrist?.Inner.FirstOrDefault(i => i.Label == "TALK");
            Check(wristTalk?.Locked != null && wristTalk.Locked(), "first run: the wrist menu's TALK is locked");

            // ---- steps 1-2 done long ago, 3 running: the panel still shows 1, then 2, one at a time
            shell.SetSetupStepForTest(1, MetaCodexShell.SetupState.Running, 0.3f);
            shell.SetSetupStepForTest(1, MetaCodexShell.SetupState.Done, 1f);
            shell.SetSetupStepForTest(2, MetaCodexShell.SetupState.Running, 0.2f);
            shell.SetSetupStepForTest(2, MetaCodexShell.SetupState.Done, 1f);
            shell.SetSetupStepForTest(3, MetaCodexShell.SetupState.Running, 0.4f);
            shell.TickFirstRunForTest(0.3f);
            Check(shell.SetupShownStep == 1, $"first run: a done step stays on show at least {MetaCodexShell.SetupMinShow} s (on show: {shell.SetupShownStep})");
            shell.TickFirstRunForTest(2.0f);
            Check(shell.SetupShownStep == 3 && Labels(sp).Any(l => l.Contains("✓") && l.Contains("AI models in place")) && Labels(sp).Any(l => l.Contains("Local AI running")),
                $"first run: steps 1 and 2 shown in turn with ✓ and their time, step 3 on show ({shell.SetupShownStep})");
            Check(Labels(sp).Any(l => l == "Building your Linux workstation") && Labels(sp).Any(l => l.EndsWith(" %")), "first run: step 3's name and its percentage");

            // ---- step 3 done: the Linux's locks lift by themselves
            shell.SetSetupStepForTest(3, MetaCodexShell.SetupState.Done, 1f);
            shell.TickFirstRunForTest(0.1f);
            Check(!Tile("TERMS").Dimmed && Tile("THREADS").Dimmed && !shell.SetupLocked(3) && shell.SetupLocked(4), "first run: step 3 over: TERMS unlocked, THREADS still waits");
            shell.OpenPanel("files");
            Check(shell.IsPanelOpen("files"), "first run: FILES opens once the Linux is ready");
            shell.ClosePanel("files");

            // ---- step 4 fails: what failed and what still works, no endless spinner; its lock lifts
            shell.SetSetupStepForTest(4, MetaCodexShell.SetupState.Running, 0.2f);
            shell.SetSetupStepForTest(4, MetaCodexShell.SetupState.Failed, 0.2f, "test: MQH did not start");
            shell.TickFirstRunForTest(1.2f);
            Check(!shell.SetupLocked(4) && !Tile("THREADS").Dimmed, "first run: a failed step lifts its lock (THREADS reports through its own paths)");
            Check(shell.SetupShownStep == 4 && Labels(sp).Any(l => l.Contains("failed")) && Labels(sp).Any(l => l.Contains("still work")),
                $"first run: the failed step on show says what failed and what still works ({shell.SetupShownStep})");
            shell.TickFirstRunForTest(MetaCodexShell.SetupFailedShow + 0.5f);
            Check(shell.SetupShownStep == 5, $"first run: after {MetaCodexShell.SetupFailedShow} s the next step ({shell.SetupShownStep})");

            // ---- step 5 counts its examples, then done: the end
            shell.SetSetupStepForTest(5, MetaCodexShell.SetupState.Running, 0.5f, "958 of 1,916 examples");
            shell.TickFirstRunForTest(0.3f);
            Check(Labels(sp).Any(l => l == "958 of 1,916 examples"), "first run: step 5 shows its examples count");
            Check(shell.SetupLocked(5) && Tile("TALK").Dimmed, "first run: TALK waits for step 5");
            shell.SetSetupStepForTest(5, MetaCodexShell.SetupState.Done, 1f);
            shell.TickFirstRunForTest(1.5f);
            Check(!shell.SetupActive && !Tile("TALK").Dimmed && !Tile("TALK").Text.StartsWith("🔒"), "first run: all steps over: every lock lifted");
            Check(shell.SetupShownStep == 6 && Labels(sp).Any(l => l.StartsWith("Setup finished in")) && shell.Settings.firstStartDone,
                $"first run: the end says it finished (a step failed) and the flag is set ({shell.SetupShownStep})");

            // ---- a clean first start: "Setup done in N s", closes itself
            shell.EndFirstRunForTest();
            shell.BeginFirstRunForTest();
            shell.TickFirstRunForTest(0.1f);
            sp = shell.SetupPanel;
            for (int n = 1; n <= MetaCodexShell.SetupSteps; n++)
            {
                shell.SetSetupStepForTest(n, MetaCodexShell.SetupState.Running, 0.1f);
                shell.TickFirstRunForTest(0.2f);
                shell.SetSetupStepForTest(n, MetaCodexShell.SetupState.Done, 1f);
            }
            shell.TickFirstRunForTest(5f);
            Check(Labels(sp).Any(l => l.StartsWith("Setup done in")) && Labels(sp).Any(l => l.StartsWith("Ready now: TALK")), "first run: the end says \"Setup done in N s\" and what is ready");
            shell.TickFirstRunForTest(MetaCodexShell.SetupCloseAfter + 0.5f);
            Check(!sp.IsOpen, "first run: the SETUP panel closes by itself");

            // ---- frames: a step running with nothing new costs no allocation
            shell.EndFirstRunForTest();
            shell.BeginFirstRunForTest();
            shell.SetSetupStepForTest(1, MetaCodexShell.SetupState.Running, 0.5f);
            shell.TickFirstRunForTest(3f); // the bar has caught up, the percentage is set
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            shell.TickFirstRunForTest(2f);
            long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            Check(alloc < 2048, $"first run: 40 frames of a running step allocate {alloc} bytes (spinner, bar, rows, locks)");

            // ---- the tour
            shell.StartTour();
            Check(shell.TourRunning && shell.TourStepNow == 1 && shell.TourCard != null && shell.TourCard.IsOpen && shell.TourCard.Title.Contains("1 OF 5"),
                $"tour: starts on card 1 ({shell.TourCard?.Title})");
            Check(shell.TourTextNow.Contains("🔒"), "tour: during the setup card 1 says that 🔒 tiles wake up by themselves");
            UiButton CardButton(string text) => shell.TourCard.Elements.OfType<UiButton>().FirstOrDefault(b => b.Text == text && b.Visible);
            CardButton("SKIP STEP")?.Click();
            Check(shell.TourStepNow == 2 || !shell.Settings.orb, $"tour: SKIP STEP goes to the orb's card ({shell.TourStepNow})");
            CardButton("SKIP STEP")?.Click();
            Check(shell.TourStepNow == 3 && shell.TourTextNow.Contains("SETUP window"), $"tour: card 3 asks to move a window, the SETUP one ({shell.TourStepNow})");
            CardButton("SKIP STEP")?.Click();
            Check(shell.TourStepNow == 4 && shell.TourTextNow.Contains("CONTROL"), "tour: card 4 asks for CONTROL");
            shell.OpenControl(null);
            shell.TickFirstRunForTest(0.1f);
            Check(shell.TourStepNow == 4 && shell.TourCard.Elements.OfType<UiLabel>().Any(l => l.Text == "✓ Done"), "tour: CONTROL opened: ✓ Done");
            shell.TickFirstRunForTest(MetaCodexShell.TourDonePause + 0.2f);
            Check(shell.TourStepNow == 5 && shell.TourTextNow.Contains("AGENTS > GET") && shell.TourTextNow.Contains("still finishing") && CardButton("FINISH ✓") != null && CardButton("SKIP TOUR") == null,
                $"tour: the last card says what comes next while the setup finishes; FINISH only ({shell.TourStepNow})");
            shell.ClosePanel(MetaCodexShell.ControlId);
            CardButton("FINISH ✓")?.Click();
            Check(!shell.TourRunning && !shell.TourCard.IsOpen, "tour: FINISH ends it");
            Check(shell.RunAction("tour") == null && shell.TourRunning && shell.RunAction("tour:stop") == null && !shell.TourRunning, "tour: the action tour starts it, tour:stop ends it");
            shell.StartTour();
            CardButton("SKIP TOUR")?.Click();
            Check(!shell.TourRunning, "tour: SKIP TOUR ends it");

            // ---- its voice command
            var cmd = shell.Voice.Find("start_tour");
            var lint = cmd != null ? VoiceCommands.Lint(cmd) : new System.Collections.Generic.List<string> { "missing" };
            Check(lint.Count == 0, "tour: voice command start_tour lints clean" + (lint.Count > 0 ? ": " + string.Join("; ", lint) : ""));
            string[] said = { "start the tour", "give me the guided tour again", "replay the tutorial", "walk me through the app" };
            var wrong = said.Where(x => shell.Voice.FastMatch(x)?.Command.Name != "start_tour").ToList();
            Check(wrong.Count == 0, "tour: phrases match start_tour" + (wrong.Count > 0 ? ", not: " + string.Join(" | ", wrong) : ""));

            shell.EndFirstRunForTest();
            shell.Settings.firstStartDone = true;
        }

        private static System.Collections.Generic.IEnumerable<string> Labels(UiPanel p) =>
            p == null ? Enumerable.Empty<string>() : p.Elements.OfType<UiLabel>().Select(l => ChatText.Strip(l.Text ?? ""));
    }
}
