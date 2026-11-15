using System;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `firstrun` (ShellFirstRun / ShellSetupLocks / ShellTour): the first start's one guide card (its
    /// setup strip and the Whisp's tour), the locks while the setup runs, with steps the test sets (the Editor has no Linux,
    /// no AI service, no MQH). Not a first start in the Editor: the flag is set at once. Then a simulated first start: the
    /// card opens with the strip (the step's words, STEP k OF 5, one-time and nothing downloaded) and the tour's first beat;
    /// TALK and the windows that need a step not over are locked with one callout and lift by themselves; a failed step says
    /// it did not start and lifts its lock; the end says "Setup done in N s", sets the flag, and the strip folds away; the
    /// beats wait for their actions; the finale follows TALK's lock; a replay has no strip; the voice command lints clean.
    /// Also: the staged start's pieces make the same windows as BuildPanels (MetaCodexShell.BuildSteps).
    public static partial class MetaCodexUiTest
    {
        private static void FirstRunSection(MetaCodexShell shell, Transform head)
        {
            Check(shell.Settings.firstStartDone && !shell.SetupActive, "first run: the Editor is never a first start (flag set at once, no setup)");
            Check(shell.ShellUp, "start: the shell is up after Build (all pieces ran)");

            // ---- the start's pieces and BuildPanels make the same windows (an accent change rebuilds through BuildPanels)
            var built = shell.PanelIds.OrderBy(x => x).ToList();
            shell.RebuildPanels();
            var rebuilt = shell.PanelIds.OrderBy(x => x).ToList();
            Check(built.SequenceEqual(rebuilt), "start: BuildSteps and BuildPanels make the same windows"
                + (built.SequenceEqual(rebuilt) ? "" : ": only at start " + string.Join(",", built.Except(rebuilt)) + "; only in BuildPanels " + string.Join(",", rebuilt.Except(built))));

            // ---- a first start, steps set by the test: the card opens with the strip and the first beat
            shell.BeginFirstRunForTest();
            shell.TickFirstRunForTest(0.5f);
            var card = shell.TourCard;
            Check(shell.SetupActive && card != null && card.IsOpen && shell.SetupStripShowing && !card.Closable,
                $"first run: the guide card shows the setup strip, not closable while it runs ({card?.Title})");
            Check(shell.SetupStripStepNow == "STEP 1 OF 5" && shell.SetupStripTextNow.Length > 0 && shell.SetupStripNoteNow == MetaCodexShell.SetupNote,
                $"first run: the strip says the step ({shell.SetupStripStepNow}: {shell.SetupStripTextNow}) and that it is once, nothing downloaded");
            Check(shell.TourRunning && shell.TourStepNow == 1 && shell.TourTextNow.Contains("I'm the Whisp") && shell.TourTextNow.Contains("only this once"),
                $"first run: the tour starts with the Whisp's hello ({shell.TourTextNow})");

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
            shell.OpenPanel("thread-2");
            Check(!shell.IsPanelOpen("threads") && !shell.IsPanelOpen("terminals") && !shell.IsPanelOpen("files") && !shell.IsPanelOpen("thread-2"),
                "first run: THREADS, TERMINALS, FILES and other thread windows stay shut while their step runs");
            Check(MetaCodexShell.PanelNeedsStep("thread-2") == 4 && MetaCodexShell.PanelNeedsStep("tgroup-1") == 4, "first run: thread windows and groups wait for MQH (step 4)");
            Check(!shell.OpenControl("agents"), "first run: CONTROL > AGENTS is locked");
            var dock = shell.PanelsById.TryGetValue("dock", out var dp) ? dp : null;
            UiButton Tile(string label) => dock?.Elements.OfType<UiButton>().FirstOrDefault(b => b.Text != null && b.Text.EndsWith("\n" + label));
            Check(Tile("TALK") != null && Tile("TALK").Dimmed && Tile("TALK").Text.StartsWith("🔒") && Tile("THREADS").Dimmed && Tile("TERMS").Dimmed && !Tile("WEB").Dimmed && !Tile("CLIP").Dimmed,
                "first run: the dock's TALK, THREADS, TERMS show 🔒 dimmed; WEB and CLIP do not");
            var wristTalk = shell.Wrist?.Inner.FirstOrDefault(i => i.Label == "TALK");
            Check(wristTalk?.Locked != null && wristTalk.Locked(), "first run: the wrist menu's TALK is locked");

            // ---- the beats: NEXT, then CLIP on the DOCK (a 🔒 sentence only while tiles are locked)
            UiButton CardButton(string text) => shell.TourCard?.Elements.OfType<UiButton>().FirstOrDefault(b => b.Text == text && b.Visible);
            CardButton("NEXT ▸")?.Click();
            shell.TickFirstRunForTest(0.5f);
            Check(shell.TourStepNow == 2 && shell.TourTextNow.Contains("Tap CLIP") && shell.TourTextNow.Contains("🔒"),
                $"tour: beat 2 asks for CLIP and says the 🔒 tiles unlock by themselves ({shell.TourStepNow}: {shell.TourTextNow})");
            shell.OpenPanel("clipboard");
            shell.TickFirstRunForTest(0.1f);
            Check(shell.TourHintNow.StartsWith("✓"), $"tour: CLIP opened: the done line ({shell.TourHintNow})");
            shell.TickFirstRunForTest(MetaCodexShell.TourDonePause + 0.5f);
            shell.ClosePanel("clipboard");
            Check(shell.TourStepNow == 3 || !shell.Settings.orb, $"tour: then the Whisp's beat ({shell.TourStepNow})");

            // ---- steps: 1-3 done, 4 fails (its lock lifts, the strip says it did not start), 5 runs
            for (int n = 1; n <= 3; n++)
            {
                shell.SetSetupStepForTest(n, MetaCodexShell.SetupState.Running, 0.3f);
                shell.SetSetupStepForTest(n, MetaCodexShell.SetupState.Done, 1f);
            }
            shell.TickFirstRunForTest(1.2f);
            Check(!Tile("TERMS").Dimmed && Tile("THREADS").Dimmed && !shell.SetupLocked(3) && shell.SetupLocked(4), "first run: step 3 over: TERMS unlocked, THREADS still waits");
            shell.OpenPanel("files");
            Check(shell.IsPanelOpen("files"), "first run: FILES opens once the Linux is ready");
            shell.ClosePanel("files");
            shell.SetSetupStepForTest(4, MetaCodexShell.SetupState.Running, 0.2f);
            shell.SetSetupStepForTest(4, MetaCodexShell.SetupState.Failed, 0.2f, "test: MQH did not start");
            shell.TickFirstRunForTest(0.2f);
            Check(!shell.SetupLocked(4) && !Tile("THREADS").Dimmed, "first run: a failed step lifts its lock (THREADS reports through its own paths)");
            Check(shell.SetupStripTextNow.Contains("did not start"), $"first run: the strip says the failed step did not start ({shell.SetupStripTextNow})");
            shell.SetSetupStepForTest(5, MetaCodexShell.SetupState.Running, 0.5f, "958 of 1,916 examples");
            shell.TickFirstRunForTest(MetaCodexShell.SetupFailedShow + 1.2f);
            Check(shell.SetupLocked(5) && Tile("TALK").Dimmed && shell.SetupStripStepNow == "STEP 5 OF 5", $"first run: TALK waits for step 5 ({shell.SetupStripStepNow})");

            // ---- frames: the strip and the tour waiting, with nothing new, allocate nothing
            shell.TickFirstRunForTest(3f);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            shell.TickFirstRunForTest(2f);
            long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            Check(alloc < 2048, $"first run: 40 frames of a running step allocate {alloc} bytes (spinner, bar, strip, locks, tour)");

            // ---- the finale while TALK is locked: it does not ask for it; when the setup ends it changes
            if (shell.TourStepNow == 3) CardButton("SKIP STEP")?.Click();
            shell.TickFirstRunForTest(0.5f);
            CardButton("SKIP STEP")?.Click();
            shell.TickFirstRunForTest(0.5f);
            Check(shell.TourStepNow == 5 && shell.TourTextNow.Contains("CONTROL"), $"tour: beat 5 asks for CONTROL ({shell.TourStepNow})");
            shell.OpenControl(null);
            shell.TickFirstRunForTest(MetaCodexShell.TourDonePause + 0.6f);
            Check(shell.TourStepNow == 6 && shell.TourTextNow.StartsWith("Almost done") && !shell.TourTextNow.Contains("Press TALK") && CardButton("FINISH ✓") != null && CardButton("SKIP TOUR") == null,
                $"tour: the finale while TALK is locked does not ask for it; FINISH only ({shell.TourStepNow}: {shell.TourTextNow})");
            shell.ClosePanel(MetaCodexShell.ControlId);
            shell.SetSetupStepForTest(5, MetaCodexShell.SetupState.Done, 1f);
            shell.TickFirstRunForTest(1.0f);
            Check(!shell.SetupActive && !Tile("TALK").Dimmed && shell.Settings.firstStartDone, "first run: all steps over: every lock lifted, the flag set");
            Check(shell.SetupStripTextNow.StartsWith("Setup finished in") || shell.SetupStripTextNow.Contains("Setup done in"), $"first run: the strip says it is over ({shell.SetupStripTextNow})");
            Check(shell.TourTextNow.StartsWith("Everything is ready") && shell.TourTextNow.Contains("Press TALK") && !shell.TourTextNow.Contains("when the setup"),
                $"tour: the finale changes when TALK unlocks ({shell.TourTextNow})");
            shell.TickFirstRunForTest(MetaCodexShell.SetupEndShowFailed + 1.5f);
            Check(!shell.SetupStripShowing && shell.TourCard != null && shell.TourCard.IsOpen && shell.TourRunning, "first run: the strip folded away, the tour goes on");
            CardButton("FINISH ✓")?.Click();
            Check(!shell.TourRunning && (shell.TourCard == null || !shell.TourCard.IsOpen), "tour: FINISH ends it and the card goes");

            // ---- a replay: no strip, the replay's hello; the action and SKIP TOUR
            shell.StartTour();
            Check(shell.TourRunning && shell.TourStepNow == 1 && !shell.SetupStripShowing && shell.TourTextNow.StartsWith("Hi again"), $"tour: a replay has no strip ({shell.TourTextNow})");
            CardButton("SKIP TOUR")?.Click();
            Check(!shell.TourRunning, "tour: SKIP TOUR ends it");
            Check(shell.RunAction("tour") == null && shell.TourRunning && shell.RunAction("tour:stop") == null && !shell.TourRunning, "tour: the action tour starts it, tour:stop ends it");

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
    }
}
