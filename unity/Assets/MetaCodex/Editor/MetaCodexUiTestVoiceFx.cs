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
    /// Self-test section `spokenreplies` (docs/voice.md "Spoken replies, sentence by sentence"): a reply is spoken one
    /// sentence at a time with a fake speech service (no AI service needed): one request in flight, the first clip plays
    /// as soon as it is made, Stop / a new reply drop late clips, played clips are freed, replies speak at most two
    /// sentences, units are spoken words; VOICE's service button says what a tap does and a COMMANDS row says how to say it.
    /// Section `voicefx` (below): the voice effect setting, VOICE > SOUND, set_setting voice_effect, `voice fx`.
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

                // a spoken reply: two sentences at most, spoken units
                shell.SayReply("It's been working for 3 min 20 s; now running the tests. Nothing new for 40 s. It may be stuck. STOP in the thread ends the turn.");
                var parts = shell.SpeechPartsForTest.ToList();
                Check(parts.Count == 2 && parts[0].Contains("3 minutes 20 seconds") && parts[1].Contains("40 seconds") && parts.Sum(x => x.Length) <= 230,
                    "a reply speaks 2 sentences with spoken units: " + string.Join(" | ", parts));
                shell.StopSpeaking();
                shell.VoiceSay("The thread has been working for 2 h, it is not stuck.");
                Check(shell.SpeechPartsForTest.Count == 1 && shell.SpeechPartsForTest[0].Contains("2 hours"), "a said text: spoken units (" + shell.SpeechPartsForTest.FirstOrDefault() + ")");
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

        /// Self-test section `voicefx` (docs/voice.md "Voice effect"): the setting voice.fx (default adjutant), VOICE >
        /// SOUND (OFF / AI / SHIP / COMM; a pick says a short line with it), set_setting voice_effect by name, + / - and
        /// spoken synonyms, the control port `voice fx`, and the clips played: off trimmed only, the presets longer by
        /// their tail, comm opening with its squelch.
        private static void VoiceFxSection(MetaCodexShell shell)
        {
            string saved = shell.Settings.voice.fx;
            var pending = new List<(string text, Action<byte[], string> cb)>();
            shell.StopSpeaking();
            shell.SpeakForTest = (text, cb) => pending.Add((text, cb));
            float now = Time.unscaledTime;
            try
            {
                Check(new VoiceSettings().fx == "adjutant", "voice.fx defaults to adjutant (AI)");
                Check(shell.GetSetting("voice.fx") == shell.Settings.voice.fx, "voice.fx is a settings key (" + shell.GetSetting("voice.fx") + ")");

                // VOICE > SOUND: four choices, the setting's one selected; a pick sets voice.fx and says the sample line
                shell.Settings.voice.fx = "adjutant";
                shell.OpenPanel("voice");
                var vp = shell.PanelsById["voice"];
                var sound = vp.Elements.OfType<UiSegmented>().FirstOrDefault(x => x.Text == "SOUND");
                Check(sound != null && sound.Options.SequenceEqual(new[] { "OFF", "AI", "SHIP", "COMM" }) && sound.Selected == 1,
                    "VOICE > SOUND: OFF / AI / SHIP / COMM, AI selected (" + (sound == null ? "missing" : string.Join(" ", sound.Options) + ", " + sound.Selected) + ")");
                if (sound != null)
                {
                    sound.Set(3, true);
                    Check(shell.Settings.voice.fx == "comm" && pending.Count == 1 && pending[0].text.StartsWith("Systems online"),
                        $"SOUND > COMM: voice.fx {shell.Settings.voice.fx}, says the sample line ({pending.Count} request: {pending.FirstOrDefault().text})");
                    // the sample comes back: the clip played is the processed one (comm opens with its squelch)
                    var wav = TestSpeechWav(1f);
                    int dry = (wav.Length - 44) / 2, trimmed = (int)(1f * 44100);
                    if (pending.Count > 0) { var p = pending[0]; pending.RemoveAt(0); p.cb(wav, null); }
                    var clip = shell.SpeechClipForTest;
                    Check(clip != null && clip.samples > trimmed + (int)(0.09 * 44100) && clip.samples < dry,
                        $"COMM clip: {(clip != null ? clip.samples : 0)} samples (dry {dry}, speech {trimmed}): the padding trimmed, the squelch added");
                    shell.StopSpeaking();

                    sound.Set(0, true);
                    if (pending.Count > 0) { var p = pending[0]; pending.RemoveAt(0); p.cb(wav, null); }
                    clip = shell.SpeechClipForTest;
                    Check(shell.Settings.voice.fx == "off" && clip != null && clip.samples <= trimmed + (int)(0.06 * 44100) && clip.samples >= trimmed - (int)(0.01 * 44100),
                        $"SOUND > OFF: the clip is the speech only, trimmed ({(clip != null ? clip.samples : 0)} samples for {trimmed}; dry {dry})");
                    shell.StopSpeaking();
                    pending.Clear();
                }

                // voice and the LLM: set_setting voice_effect
                var set = shell.Voice.Find("set_setting");
                bool enumHas = (set?.Properties["setting"]?["enum"] as JArray)?.Any(x => (string)x == "voice_effect") == true;
                string r1 = shell.SetSettingByVoice("voice_effect", "starship"), v1 = shell.Settings.voice.fx;
                string r2 = shell.SetSettingByVoice("voice_effect", "radio"), v2 = shell.Settings.voice.fx;
                string r3 = shell.SetSettingByVoice("voice_effect", "+"), v3 = shell.Settings.voice.fx;
                string r4 = shell.SetSettingByVoice("voice_effect", "normal"), v4 = shell.Settings.voice.fx;
                string r5 = shell.SetSettingByVoice("voice_effect", "on"), v5 = shell.Settings.voice.fx;
                string r6 = shell.SetSettingByVoice("voice_effect", "banana"), v6 = shell.Settings.voice.fx;
                Check(enumHas && v1 == "starship" && v2 == "comm" && v3 == "off" && v4 == "off" && v5 == "adjutant" && r6.StartsWith("error") && v6 == "adjutant",
                    $"set_setting voice_effect: starship -> {v1} ({r1}), radio -> {v2} ({r2}), + -> {v3} ({r3}), normal -> {v4}, on -> {v5} ({r5}), banana -> {r6}");
                bool fits = set != null && set.Check != null && set.Check(new JObject { ["setting"] = "voice_effect", ["value"] = "ship" }) == null
                            && set.Check(new JObject { ["setting"] = "voice_effect", ["value"] = "banana" }) != null;
                Check(fits, "set_setting's check: voice_effect takes ship, not banana");
                // VOICE > SPEECH: FAST (4 diffusion steps) by default, CLEAR = 5; set_setting speech_quality moves it
                var speech = vp.Elements.OfType<UiSegmented>().FirstOrDefault(x => x.Text == "SPEECH");
                string sq0 = shell.Settings.voice.speech;
                string q1 = shell.SetSettingByVoice("speech_quality", "clear"), w1 = shell.Settings.voice.speech;
                shell.SyncSettingsControls();
                int sel1 = speech != null ? speech.Selected : -1;
                string q2 = shell.SetSettingByVoice("speech_quality", "quick"), w2 = shell.Settings.voice.speech;
                Check(speech != null && sq0 == "fast" && w1 == "clear" && sel1 == 1 && w2 == "fast",
                    $"VOICE > SPEECH: default {sq0}, clear -> {w1} (row {sel1}, {q1}), quick -> {w2} ({q2})");
                var lint = VoiceCommands.Lint(set);
                Check(lint.Count == 0, "set_setting lint stays clean" + (lint.Count > 0 ? ": " + string.Join("; ", lint) : ""));

                // the control port, and SOUND follows it
                string c1 = shell.ControlCommand("voice", "fx comm"), c2 = shell.ControlCommand("voice", "fx banana"), c3 = shell.ControlCommand("voice", "fx");
                Check(c1.StartsWith("fx=comm") && c2.StartsWith("error") && c3.StartsWith("fx=comm") && sound != null && sound.Selected == 3,
                    $"control port: voice fx comm -> {c1}; banana -> {c2}; SOUND shows {(sound != null ? sound.Options[sound.Selected] : "?")}");
                Check(shell.ControlCommand("voice", "nonsense").Contains("fx off|adjutant|starship|comm"), "control port usage lists voice fx");
            }
            finally
            {
                shell.SpeakForTest = null;
                shell.StopSpeaking();
                shell.Settings.voice.fx = saved;
                shell.SyncSettingsControls();
                shell.ClosePanel("voice");
            }
        }
    }
}
