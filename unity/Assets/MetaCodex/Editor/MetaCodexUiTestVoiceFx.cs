using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `spokenreplies` (docs/voice.md "Spoken replies, sentence by sentence"): a reply is spoken one
    /// sentence at a time with a fake speech service (no AI service needed): one request in flight, the first clip plays
    /// as soon as it is made, Stop / a new reply drop late clips, played clips are freed, replies speak at most two
    /// sentences, units are spoken words; VOICE's service button says what a tap does and a COMMANDS row says how to say it.
    public static partial class MetaCodexUiTest
    {
        /// A 16-bit mono WAV of a quiet tone with the silence the service pads around speech.
        private static byte[] TestSpeechWav(float seconds, int rate = 44100)
        {
            int pad = (int)(0.3f * rate), n = (int)(seconds * rate);
            var pcm = new short[pad + n + pad];
            for (int i = 0; i < n; i++) pcm[pad + i] = (short)(9000 * Math.Sin(2 * Math.PI * 220 * i / rate) * Math.Sin(Math.PI * i / n));
            return VoiceRecorder.Wav(pcm, rate);
        }

        private static int SpeechClipCount() => Resources.FindObjectsOfTypeAll<AudioClip>().Count(c => c != null && c.name == "speech");

        private static void SpokenRepliesSection(MetaCodexShell shell)
        {
            var pending = new List<(string text, Action<byte[], string> cb)>();
            int maxInFlight = 0;
            shell.StopSpeaking();
            int clips0 = SpeechClipCount();
            shell.SpeakForTest = (text, cb) => { pending.Add((text, cb)); maxInFlight = Math.Max(maxInFlight, pending.Count); };
            float now = Time.unscaledTime;
            void Answer(int i) { var p = pending[i]; pending.RemoveAt(i); p.cb(TestSpeechWav(0.2f), null); }
            void Tick(float dt = 0.1f) { now += dt; shell.TickSpeechForTest(now); }
            try
            {
                // one sentence at a time, in order; the first plays as soon as it is made
                shell.VoiceSay("The build finished with no errors at all. Two threads are waiting for you now. The terminal has a new message for you.");
                bool oneAtATime = pending.Count == 1 && shell.SpeechRequestedForTest == 1 && shell.SpeechPartsForTest.Count == 3;
                Answer(0);
                bool firstPlays = shell.SpeechClipsPlayedForTest == 1 && pending.Count == 1 && pending[0].text.StartsWith("Two threads");
                for (int k = 0; k < 300 && (pending.Count > 0 || shell.Speaking); k++)
                {
                    if (pending.Count > 0) Answer(0);
                    Tick();
                    if (shell.SpeechPlayingForTest) System.Threading.Thread.Sleep(20); // where the Editor has audio, the clip really plays
                }
                Check(oneAtATime && firstPlays && maxInFlight == 1 && shell.SpeechClipsPlayedForTest == 3 && !shell.Speaking,
                    $"spoken reply: 3 sentences, one request at a time (most in flight {maxInFlight}), sentence 1 plays as soon as it is made ({firstPlays}), all 3 played ({shell.SpeechClipsPlayedForTest})");

                // Stop: a sentence that arrives later never plays
                shell.VoiceSay("This is a long first sentence of the reply. And this is the second sentence of it.");
                shell.StopSpeaking();
                bool stopped = !shell.Speaking;
                Answer(0);
                Tick(1f);
                Check(stopped && shell.SpeechClipsPlayedForTest == 0 && shell.SpeechClipForTest == null && shell.SpeechReadyForTest == 0,
                    "Stop: the queue is empty and the sentence that arrives after it never plays");

                // a new reply replaces the old one: the old one's late sentence is dropped
                shell.VoiceSay("The first reply has this sentence in it.");
                shell.VoiceSay("The second reply has another sentence.");
                Answer(0); // the first reply's sentence, late
                bool dropped = shell.SpeechClipsPlayedForTest == 0;
                Answer(0);
                Check(dropped && shell.SpeechClipsPlayedForTest == 1 && shell.SpeechPartsForTest.Count == 1 && shell.SpeechPartsForTest[0].StartsWith("The second reply"),
                    "a new reply replaces the old one; the old one's late sentence does not play");
                shell.VoiceCancel(); // the HUD's STOP
                Check(!shell.Speaking && shell.SpeechClipForTest == null, "STOP on the voice HUD stops the reply");

                // a spoken reply: two sentences at most, spoken units, Italian ones too
                shell.SayReply("It's been working for 3 min 20 s; now running the tests. Nothing new for 40 s. It may be stuck. STOP in the thread ends the turn.");
                var parts = shell.SpeechPartsForTest.ToList();
                Check(parts.Count == 2 && parts[0].Contains("3 minutes 20 seconds") && parts[1].Contains("40 seconds") && parts.Sum(x => x.Length) <= 230,
                    "a reply speaks 2 sentences with spoken units: " + string.Join(" | ", parts));
                shell.StopSpeaking();
                shell.VoiceSay("Il thread sta ancora lavorando da 3 min, non è bloccato.");
                Check(shell.SpeechPartsForTest.Count == 1 && shell.SpeechPartsForTest[0].Contains("3 minuti"), "Italian reply: Italian units (" + shell.SpeechPartsForTest.FirstOrDefault() + ")");
                shell.StopSpeaking();
                pending.Clear();

                // played and dropped clips are freed
                int clips1 = SpeechClipCount();
                Check(clips1 == clips0, $"speech clips are destroyed once played or dropped ({clips0} before, {clips1} after)");

                // a failed sentence: done gets the error, nothing after it is asked for
                string err = "-";
                shell.SpeakForTest = (text, cb) => cb(null, "409: model not installed: tts");
                shell.VoiceSay("A first sentence that will fail here. And the second one after it.", e => err = e);
                Check(err != null && err.StartsWith("409") && !shell.Speaking, "a failed sentence reports its error and stops the queue: " + err);
            }
            finally
            {
                shell.SpeakForTest = null;
                shell.StopSpeaking();
            }

            // VOICE: the service button says what a tap does; a COMMANDS row says how to say it, never the id
            shell.OpenPanel("voice");
            var vp = shell.PanelsById["voice"];
            bool running = shell.Qvac != null && (shell.Qvac.IsRunning || shell.Qvac.IsBusyStarting);
            var btns = vp.Elements.OfType<UiButton>().Select(b => b.Text).ToList();
            Check(btns.Contains(running ? "■ STOP" : "▶ START") && !btns.Contains("START / STOP"), "VOICE: the service button shows " + (running ? "■ STOP" : "▶ START") + " (" + string.Join(", ", btns.Take(6)) + ")");
            var list = vp.Elements.OfType<UiList>().FirstOrDefault();
            var item = list?.Items.FirstOrDefault(x => (x.Tag as string) == "open_panel") ?? list?.Items.FirstOrDefault();
            if (list != null && item != null) list.OnSelect?.Invoke(item, 0);
            string toast = shell.Toasts?.Last ?? "";
            string id = item?.Tag as string ?? "?";
            Check(toast.StartsWith("Say “") && !toast.Contains(id) && toast.Split('\n').Length <= 2 && toast.Length <= 160,
                $"a COMMANDS row tap says how to say it, not the id {id}: {toast.Replace("\n", " / ")}");
            var hints = shell.Voice.All.Select(c => (c.Name, hint: MetaCodexShell.CommandHint(c))).ToList();
            var bad = hints.Where(h => h.hint.Length > 200 || h.hint.Split('\n').Length > 2 || h.Name.Contains("_") && h.hint.Contains(h.Name) || !h.hint.StartsWith("Say “")).Select(h => h.Name).ToList();
            Check(bad.Count == 0, $"every command's hint: “Say …” first, at most two lines, never its id ({hints.Count - bad.Count}/{hints.Count}" + (bad.Count > 0 ? ": " + string.Join(", ", bad.Take(6)) : "") + ")");
            shell.ClosePanel("voice");
        }
    }
}
