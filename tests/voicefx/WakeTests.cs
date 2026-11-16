// "Hey Whisp" without Unity: the matcher and the command after the phrase (Runtime/AI/WakeWord.cs), the voice gate on
// made-up audio and the check rate (Runtime/AI/WakeGate.cs). Run by tests/voicefx/run-voicefx-tests.sh.
using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;

internal static partial class Program
{
    private static void WakeTests()
    {
        WakeMatchTests();
        WakeGateTests();
        WakeRateTests();
    }

    private static void WakeMatchTests()
    {
        var bad = WakeWord.SelfCheck();
        Check(bad.Count == 0, $"wake word: {WakeWord.Cases.Length} match cases and {WakeWord.CommandCases.Length} command cases", string.Join("\n", bad));

        // the transcripts of the owner's test (session log 2026-11-16, lines 671-703)
        Check(WakeWord.Match("Hey Whisper. Hey Whisper. Hey", out string r1) && r1 == "", "log: the wake check's \"Hey Whisper. Hey Whisper. Hey\" wakes with no command after it (TALK waits for one)", "rest \"" + r1 + "\"");
        Check(WakeWord.Command("a whisp,", true) == "" && WakeWord.IsOnlyPhrase("a whisp,"), "log: TALK heard \"a whisp,\": no command (it ran set_wake_word)");
        Check(WakeWord.Command("a whisper, open the panels", true) == "open the panels", "log: TALK heard \"a whisper, open the panels\": the command is \"open the panels\"");
        Check(WakeWord.Command("Hey Whisper.", false) == "" && WakeWord.IsOnlyPhrase("Hey Whisper."), "TALK by the button that heard only the phrase runs nothing");

        // the brief's mis-hearings, each alone and with a command after it
        string[] variants = { "hey whisper", "a whisp", "hey wisp", "hey whist", "hey west", "hey wis", "hey this", "hey whisk", "whisp", "Hey Whisp. Hey Whisp.", "hey whispy", "hi whisp", "ok whisp", "heywhisp", "hey wesp", "hey wish", "hey with" };
        foreach (var v in variants)
        {
            Check(WakeWord.Match(v, out string rest) && rest == "", "wakes: \"" + v + "\"", "rest \"" + rest + "\"");
            Check(WakeWord.Match(v + ".", out _), "wakes: \"" + v + ".\"");
            Check(WakeWord.Command(v + ", open the threads", true) == "open the threads", "after a wake: \"" + v + ", open the threads\" -> \"open the threads\"", "got \"" + WakeWord.Command(v + ", open the threads", true) + "\"");
        }

        // everyday speech near the headset: no single common word alone may wake it
        string[] speech =
        {
            "Hey, what's up?", "Hey, this is my friend Tom.", "Hey this is great", "Hey, wish you were here", "Hey with the music on",
            "Hey, west coast is better", "Hey, wise guy.", "Hey, which one?", "This is it.", "Wish me luck.", "Whisk the eggs.", "Whiskey, please.",
            "West.", "With pleasure.", "He wished for more.", "He said this.", "A wish.", "A wisp of smoke", "A whisper of wind came through the window",
            "Okay, this is fine", "Okay, with that said", "Hey you.", "Hey there.", "Hi, how are you?", "Okay.", "Hey", "Hi.", "Yes.", "No.", "Thank you.",
            "Whisper it to me", "The wisp is pretty", "I said hey whisp yesterday", "Is it working?", "What is this?", "Hey, is this on?", "Hey, listen.",
            "Hey, wait for me", "Hey, with you it is different", "Hey west side", "This one, please.", "With the new update we ship today", "Hi, this is Anna.",
            "Hey, whisky tonight?", "Oh, this is nice", "So, west or east?", "Well, wish me luck", "Hey, what is that?", "He is right.", "A list of things",
        };
        foreach (var t in speech) Check(!WakeWord.Match(t, out _), "does not wake: \"" + t + "\"");
        int woke = speech.Count(t => WakeWord.Match(t, out _));
        Check(woke == 0, $"everyday speech: {speech.Length} sentences, {woke} woke it");

        // the button's TALK keeps everyday commands as they are
        foreach (var t in new[] { "open the threads", "close all panels", "This panel should close", "A new terminal", "With the files open", "turn off the wake word", "hey whisp off" })
            Check(WakeWord.Command(t, false) == t, "TALK by the button: \"" + t + "\" unchanged", "got \"" + WakeWord.Command(t, false) + "\"");
    }

    // ---- the gate on made-up audio: quiet, voiced pieces (a 180 Hz tone with a little noise), as the Editor's section `wake`

    private sealed class GateRun
    {
        public readonly WakeGate G = new WakeGate();
        public readonly List<(long start, long end, bool cut, bool merged, bool phrase, long emittedAt)> Segs = new List<(long, long, bool, bool, bool, long)>();
        private readonly float[] _block = new float[512];
        private readonly Random _rnd = new Random(7);
        public const int Rate = 16000;
        public GateRun() { G.Reset(Rate); }

        /// `seconds` of a tone at `level` (0: quiet), in whole blocks; returns where it started (Fed).
        public long Run(double seconds, float level)
        {
            long at = G.Fed;
            int blocks = (int)Math.Ceiling(seconds * Rate / _block.Length);
            for (int b = 0; b < blocks; b++)
            {
                for (int k = 0; k < _block.Length; k++)
                    _block[k] = level * (float)Math.Sin(2 * Math.PI * 180 * (G.Fed + k) / Rate) + 0.002f * (float)(_rnd.NextDouble() * 2 - 1);
                G.Feed(_block, _block.Length);
                if (G.HasSegment) { Segs.Add((G.SegmentStart, G.SegmentEnd, G.SegmentCut, G.SegmentMerged, G.SegmentPhraseLike, G.Fed)); G.Clear(); }
            }
            return at;
        }
    }

    private static void WakeGateTests()
    {
        const int rate = GateRun.Rate;
        int tol = 512;

        // the Editor's scenario: a 0.9 s phrase, a click, a far voice, a 4 s sentence -> the phrase and the sentence's start
        var a = new GateRun();
        a.Run(1.0, 0f);
        long phraseAt = a.Run(0.9, 0.1f);
        long phraseEnd = a.G.Fed;
        a.Run(1.0, 0f);
        a.Run(0.1, 0.2f); // a click: too short
        a.Run(1.0, 0f);
        a.Run(1.0, 0.025f); // voiced but under MinPeak: far away
        a.Run(1.0, 0f);
        long sentenceAt = a.Run(4.0, 0.1f);
        a.Run(1.0, 0f);
        var g = a.G;
        Check(a.Segs.Count == 2, $"gate: 2 segments (the phrase; the start of the long sentence), got {a.Segs.Count}");
        if (a.Segs.Count >= 2)
        {
            var p = a.Segs[0];
            Check(!p.cut && !p.merged && p.phrase && Math.Abs(p.start - (phraseAt - (long)(g.PreRollSeconds * rate))) <= tol && Math.Abs(p.end - (phraseEnd + (long)(g.TailSeconds * rate))) <= tol,
                $"gate: the phrase with {g.PreRollSeconds} s before and {g.TailSeconds} s after");
            double react = (p.emittedAt - phraseEnd) / (double)rate;
            Check(react <= g.HangSeconds + 512.0 / rate + 1e-6, $"gate: the phrase goes {react * 1000:F0} ms after its last voiced sample (hang {g.HangSeconds * 1000:F0} ms; was 300 ms)");
            var s = a.Segs[1];
            Check(s.cut && !s.phrase && Math.Abs(s.start - (sentenceAt - (long)(g.PreRollSeconds * rate))) <= tol && Math.Abs(s.end - s.start - (long)((g.PreRollSeconds + g.MaxSeconds) * rate)) <= tol,
                $"gate: a long sentence goes as its first {g.MaxSeconds} s (cut), once");
        }

        // a fast "Hey Whisp": 0.33 s voiced (under the old 0.4 s floor)
        var f = new GateRun();
        f.Run(0.5, 0f);
        f.Run(0.33, 0.1f);
        f.Run(0.6, 0f);
        Check(f.Segs.Count == 1, $"gate: a fast phrase (0.33 s voiced) is checked, got {f.Segs.Count} segments");

        // "Hey" (0.2 s), a 0.35 s pause, "Whisp" (0.2 s): one segment from "hey"
        var m = new GateRun();
        m.Run(0.5, 0f);
        long heyAt = m.Run(0.2, 0.1f);
        m.Run(0.35, 0f);
        m.Run(0.2, 0.1f);
        long whispEnd = m.G.Fed;
        m.Run(0.8, 0f);
        Check(m.Segs.Count == 1 && m.Segs[0].merged && Math.Abs(m.Segs[0].start - (heyAt - (long)(m.G.PreRollSeconds * rate))) <= tol && Math.Abs(m.Segs[0].end - (whispEnd + (long)(m.G.TailSeconds * rate))) <= tol,
            $"gate: \"hey\", a pause, \"whisp\" (two pieces too short alone) go as one segment from \"hey\" ({m.Segs.Count} segments)");

        // "Heyyy" (0.4 s, checked alone), a 0.3 s pause, "whisp" (0.2 s): the two go again as one
        var h = new GateRun();
        h.Run(0.5, 0f);
        long hAt = h.Run(0.4, 0.1f);
        h.Run(0.3, 0f);
        h.Run(0.2, 0.1f);
        h.Run(0.8, 0f);
        Check(h.Segs.Count == 2 && !h.Segs[0].merged && h.Segs[1].merged && Math.Abs(h.Segs[1].start - (hAt - (long)(h.G.PreRollSeconds * rate))) <= tol,
            $"gate: a long \"heyyy\" goes alone, then with the short \"whisp\" after it ({h.Segs.Count} segments)");

        // "Okay." (0.5 s), a 0.35 s pause, "Hey Whisp" (0.6 s): the phrase goes alone, from its own start
        var o = new GateRun();
        o.Run(0.5, 0f);
        o.Run(0.5, 0.1f);
        o.Run(0.35, 0f);
        long hwAt = o.Run(0.6, 0.1f);
        o.Run(0.8, 0f);
        Check(o.Segs.Count == 2 && !o.Segs[1].merged && Math.Abs(o.Segs[1].start - (hwAt - (long)(o.G.PreRollSeconds * rate))) <= tol,
            $"gate: a word, a pause, then the phrase: the phrase is its own segment ({o.Segs.Count} segments)");

        // two short pieces 1 s apart: no segment (not one phrase, each too short)
        var q = new GateRun();
        q.Run(0.5, 0f);
        q.Run(0.15, 0.1f);
        q.Run(1.0, 0f);
        q.Run(0.15, 0.1f);
        q.Run(0.8, 0f);
        Check(q.Segs.Count == 0, $"gate: two short sounds 1 s apart are not joined ({q.Segs.Count} segments)");

        // the phrase across the gate's blocks: the same segment whatever the offset of the audio in the blocks
        foreach (int offset in new[] { 0, 137, 300, 511 })
        {
            var gg = new WakeGate();
            gg.Reset(rate);
            var x = new float[(int)(2.5 * rate)];
            int at = (int)(0.6 * rate) + offset, len = (int)(0.5 * rate);
            for (int i = at; i < at + len; i++) x[i] = 0.1f * (float)Math.Sin(2 * Math.PI * 180 * i / rate);
            var blk = new float[512];
            int got = 0;
            long st = 0, en = 0;
            for (int i = 0; i + blk.Length <= x.Length; i += blk.Length)
            {
                Array.Copy(x, i, blk, 0, blk.Length);
                gg.Feed(blk, blk.Length);
                if (gg.HasSegment) { got++; st = gg.SegmentStart; en = gg.SegmentEnd; gg.Clear(); }
            }
            Check(got == 1 && st <= at - (long)(0.2 * rate) && en >= at + len + (long)(0.15 * rate), $"gate: a phrase {offset} samples into a block is caught whole (segment {st}-{en}, phrase {at}-{at + len})");
        }

        // the WAV the check gets
        var g2 = new WakeGate();
        g2.Reset(rate);
        var block = new float[512];
        byte[] wav = null;
        for (int b = 0; b < 32 + 19 + 16 && wav == null; b++)
        {
            bool voiced = b >= 32 && b < 32 + 19;
            for (int k = 0; k < block.Length; k++) block[k] = voiced ? 0.1f * (float)Math.Sin(2 * Math.PI * 180 * (b * block.Length + k) / 16000.0) : 0f;
            g2.Feed(block, block.Length);
            if (g2.HasSegment) wav = g2.SegmentWav16k();
        }
        Check(wav != null && wav.Length == 44 + g2.SegmentLength * 2 && BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 22) == 1, "gate: the segment WAV is 16 kHz mono 16-bit");

        // the gate allocates nothing per block: pieces of speech and quiet, after one round to warm up the code
        var zg = new WakeGate();
        zg.Reset(rate);
        var tone = new float[512];
        var hush = new float[512];
        for (int k = 0; k < tone.Length; k++) tone[k] = 0.1f * (float)Math.Sin(2 * Math.PI * 180 * k / rate);
        void Round() { for (int i = 0; i < 400; i++) { zg.Feed(i % 40 < 15 ? tone : hush, 512); zg.Clear(); } }
        Round();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Round();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(alloc == 0, $"gate: no allocation per block ({alloc} bytes over 400 blocks, {zg.Segments} segments)");
    }

    private static void WakeRateTests()
    {
        var r = new WakeRate();
        r.Reset(0f);
        bool l1 = r.Take(0.0f, false), l2 = r.Take(0.1f, false), l3 = r.Take(0.2f, false), l4 = r.Take(0.3f, false), p1 = r.Take(0.4f, true), p2 = r.Take(0.5f, true);
        Check(l1 && l2 && l3 && !l4 && p1 && !p2, $"rate: 3 long segments in a row, the 4th waits; the last check is kept for a phrase-sized one ({l1} {l2} {l3} {l4} {p1} {p2})");
        Check(r.Take(4.6f, true), "rate: one more after 4 s");

        // talk nearby for a minute: a segment every 0.5 s, every third one phrase-sized
        var s = new WakeRate();
        s.Reset(0f);
        int sent = 0;
        for (int i = 0; i < 120; i++) if (s.Take(i * 0.5f, i % 3 == 0)) sent++;
        float perSecond = sent / 60f;
        Check(sent <= 4 + 60 / 4 + 1, $"rate: a minute of talk nearby sends {sent} checks ({perSecond:F2} a second; at most 4 + 1 per 4 s)");
        if (Verbose) Console.WriteLine($"     cost: {perSecond:F2} checks/s x ~0.3 s each = {perSecond * 0.3:F3} s of the speech engine a second (was at most 0.2 x 0.3 = 0.060)");
    }
}
