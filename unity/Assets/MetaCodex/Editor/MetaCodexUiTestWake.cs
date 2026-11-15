using System;
using System.Collections.Generic;
using MetaCodex.AI;
using MetaCodex.Shell;

namespace MetaCodex.EditorTools
{
    /// Self-test section `wake` ("Hey Whisp", docs/voice.md): the tolerant match (WakeWord.SelfCheck), the voice gate on
    /// made-up audio (a phrase, a click, a far voice, a long sentence), the switch off by default, its registry command
    /// and the orb setting answering to "whisp". No microphone, no AI service.
    public static partial class MetaCodexUiTest
    {
        private static void WakeSection(MetaCodexShell shell)
        {
            var bad = WakeWord.SelfCheck();
            Check(bad.Count == 0, $"wake word: {WakeWord.Cases.Length} match cases" + (bad.Count > 0 ? ": " + string.Join("; ", bad) : ""));

            // the gate at 16 kHz: quiet, a 0.9 s phrase, quiet, a 0.1 s click, quiet, a far voice, quiet, a 4 s sentence, quiet
            const int rate = 16000;
            var g = new WakeGate();
            g.Reset(rate);
            var block = new float[512];
            var rnd = new Random(7);
            var segs = new List<(long start, long end, bool cut)>();
            void Run(double seconds, float level)
            {
                int blocks = (int)Math.Ceiling(seconds * rate / block.Length);
                for (int b = 0; b < blocks; b++)
                {
                    for (int k = 0; k < block.Length; k++)
                        block[k] = level * (float)Math.Sin(2 * Math.PI * 180 * (b * block.Length + k) / rate) + 0.002f * (float)(rnd.NextDouble() * 2 - 1);
                    g.Feed(block, block.Length);
                    if (g.HasSegment) { segs.Add((g.SegmentStart, g.SegmentEnd, g.SegmentCut)); g.Clear(); }
                }
            }
            Run(1.0, 0f);
            long phraseAt = g.Fed;
            Run(0.9, 0.1f);
            long phraseEnd = g.Fed;
            Run(1.0, 0f);
            Run(0.1, 0.2f); // a click: too short
            Run(1.0, 0f);
            Run(1.0, 0.025f); // voiced but under MinPeak: far away
            Run(1.0, 0f);
            long sentenceAt = g.Fed;
            Run(4.0, 0.1f);
            Run(1.0, 0f);
            int tol = block.Length;
            Check(segs.Count == 2, $"wake gate: 2 segments (the phrase; the start of the long sentence), got {segs.Count}");
            if (segs.Count >= 1)
                Check(!segs[0].cut && Math.Abs(segs[0].start - (phraseAt - (long)(g.PreRollSeconds * rate))) <= tol && Math.Abs(segs[0].end - (phraseEnd + (long)(g.TailSeconds * rate))) <= tol,
                    $"wake gate: the phrase with {g.PreRollSeconds} s before and {g.TailSeconds} s after ({segs[0].start}-{segs[0].end}, want {phraseAt - (long)(g.PreRollSeconds * rate)}-{phraseEnd + (long)(g.TailSeconds * rate)})");
            if (segs.Count >= 2)
                Check(segs[1].cut && Math.Abs(segs[1].start - (sentenceAt - (long)(g.PreRollSeconds * rate))) <= tol && Math.Abs(segs[1].end - segs[1].start - (long)((g.PreRollSeconds + g.MaxSeconds) * rate)) <= tol,
                    $"wake gate: a long sentence goes as its first {g.MaxSeconds} s (cut), once");
            Check(segs.Count >= 1 && WavLooks16k(g, segs[0]), "wake gate: the segment WAV is 16 kHz mono 16-bit");

            // the switch: off by default; the command and set_setting reach it; the orb setting answers to "whisp"
            Check(!shell.WakeWordOn && !shell.Settings.voice.wakeWord && shell.Wake != null && !shell.Wake.On, "Hey Whisp is off by default (the microphone is not opened)");
            var cmd = shell.Voice.Find("set_wake_word");
            var lint = cmd != null ? VoiceCommands.Lint(cmd) : new List<string> { "missing" };
            Check(cmd != null && lint.Count == 0, "set_wake_word is registered, lint clean" + (lint.Count > 0 ? ": " + string.Join("; ", lint) : ""));
            // set_setting (registered first) takes "turn off the wake word" as setting wake_word: the same SetWakeWord
            var m = shell.Voice.FastMatch("turn off the wake word");
            Check(m != null && (m.Command.Name == "set_wake_word" && (string)m.Args["state"] == "off" || m.Command.Name == "set_setting" && (string)m.Args["setting"] == "wake_word" && (string)m.Args["value"] == "off"),
                "\"turn off the wake word\" -> Hey Whisp off (fast path: " + (m != null ? m.Command.Name + " " + m.Args.ToString(Newtonsoft.Json.Formatting.None) : "no match") + ")");
            var ml = shell.Voice.FastMatch("listen for hey whisp");
            Check(ml != null && ml.Command.Name == "set_wake_word" && (string)ml.Args["state"] == "on", "\"listen for hey whisp\" -> set_wake_word on (fast path)");
            var w = shell.Voice.FastMatch("hey whisp open the terminal");
            var plain = shell.Voice.FastMatch("open the terminal");
            Check(plain == null || w != null && w.Command.Name == plain.Command.Name, "\"hey whisp\" in front of a command is dropped (fast path fillers)");
            string r = shell.SetSettingByVoice("orb", "on");
            Check(r == "Whisp on" && shell.Settings.orb, "set_setting orb on answers \"Whisp on\" (" + r + ")");
            var mw = shell.Voice.FastMatch("turn the whisp off");
            Check(mw == null || mw.Command.Name == "set_setting", "\"turn the whisp off\" is the Whisp's setting when it matches");
            shell.SetSettingByVoice("orb", "on");
        }

        private static bool WavLooks16k(WakeGate g, (long start, long end, bool cut) seg)
        {
            // the gate keeps only the last segment ready: re-make the phrase's one on a fresh gate
            var g2 = new WakeGate();
            g2.Reset(16000);
            var block = new float[512];
            for (int b = 0; b < 32 + 29 + 16; b++)
            {
                bool voiced = b >= 32 && b < 32 + 29;
                for (int k = 0; k < block.Length; k++) block[k] = voiced ? 0.1f * (float)Math.Sin(2 * Math.PI * 180 * (b * block.Length + k) / 16000.0) : 0f;
                g2.Feed(block, block.Length);
                if (!g2.HasSegment) continue;
                var wav = g2.SegmentWav16k();
                return wav != null && wav.Length == 44 + g2.SegmentLength * 2 && BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 22) == 1;
            }
            return false;
        }
    }
}
