// Spoken-reply tests without Unity: the sentence splitter, the spoken units and the shortening of replies
// (Runtime/AI/SpokenText.cs), and the voice effect (Runtime/AI/VoiceFx.cs, VoiceFxTests.cs). Build + run: tests/voicefx/run-voicefx-tests.sh (also run by tests/terminal/run-core-tests.sh).
using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;

internal static partial class Program
{
    private static int _fails, _passes;

    private static int Main(string[] args)
    {
        Verbose = Array.IndexOf(args, "--verbose") >= 0;
        Sentences();
        Speakable();
        ForSpeech();
        VoiceFxTests(); // VoiceFxTests.cs
        int si = Array.IndexOf(args, "--samples");
        if (si >= 0 && si + 1 < args.Length) VoiceFxSamples(args[si + 1]); // the service's own replies, when the script found them
        Console.WriteLine(_fails == 0 ? $"voicefx: ALL OK ({_passes} checks)" : $"voicefx: {_fails} FAILED, {_passes} passed");
        return _fails == 0 ? 0 : 1;
    }

    /// --verbose: every passing check with its numbers too (for the commit and docs/voice.md).
    private static bool Verbose;

    private static void Check(bool ok, string what, string detail = null)
    {
        if (ok) { _passes++; if (Verbose) Console.WriteLine("ok   " + what); return; }
        _fails++;
        Console.WriteLine("FAIL " + what + (detail != null ? "\n     " + detail.Replace("\n", "\n     ") : ""));
    }

    private static string Show(List<string> s) => "[" + string.Join(" | ", s) + "]";

    private static void Split(string text, params string[] want)
    {
        var got = SpokenText.Sentences(text);
        Check(got.SequenceEqual(want), "sentences of: " + text, "got  " + Show(got) + "\nwant " + Show(want.ToList()));
    }

    private static void Sentences()
    {
        Split("The build finished with no errors. Two threads are waiting for you.",
            "The build finished with no errors.", "Two threads are waiting for you.");
        // file names, decimals, versions: no split inside
        Split("I changed ShellVoice.cs and VoiceFx.cs in the AI folder. The tests take 3.5 seconds on version 1.2.4 now.",
            "I changed ShellVoice.cs and VoiceFx.cs in the AI folder.", "The tests take 3.5 seconds on version 1.2.4 now.");
        // abbreviations and initials
        Split("Use a short flag, e.g. the dry run, before you push it. It is safe.",
            "Use a short flag, e.g. the dry run, before you push it.", "It is safe.");
        Split("Dr. Rossi and J. Smith reviewed the patch in detail. It merged cleanly.",
            "Dr. Rossi and J. Smith reviewed the patch in detail.", "It merged cleanly.");
        Split("See issue No. 5 for the details of the crash. The fix is in.",
            "See issue No. 5 for the details of the crash.", "The fix is in.");
        // ellipses: a lower-case word after them goes on; an upper-case one starts a sentence
        Split("It waited... then it finished the whole build. Then it stopped for good.",
            "It waited... then it finished the whole build.", "Then it stopped for good.");
        Split("It is still thinking… Nothing new for 3 minutes.", "It is still thinking…", "Nothing new for 3 minutes.");
        // questions, exclamations, closing quotes
        Split("Is the build stuck at the end? No, it is linking the final binary right now!",
            "Is the build stuck at the end?", "No, it is linking the final binary right now!");
        Split("Is it stuck? No, it is linking.", "Is it stuck? No, it is linking."); // a short question goes with its answer
        Split("It said “all tests passed.” Then the agent opened a pull request.",
            "It said “all tests passed.”", "Then the agent opened a pull request.");
        // Italian
        Split("Il thread sta ancora lavorando da 3 minuti. Non è bloccato: sta compilando il progetto.",
            "Il thread sta ancora lavorando da 3 minuti.", "Non è bloccato: sta compilando il progetto.");
        Split("Il sig. Rossi ha approvato la modifica alle ore 10.30 di oggi. È tutto pronto.",
            "Il sig. Rossi ha approvato la modifica alle ore 10.30 di oggi.", "È tutto pronto.");
        // short sentences go with the next one; replies joined with " · " and lines are separate
        Split("Done. Nothing needs your attention right now.", "Done. Nothing needs your attention right now.");
        Split("Opened GIT · Closed 2 panels", "Opened GIT Closed 2 panels"); // both short: one request
        Split("The first workspace builds the Android app. · The second one runs the test suite.",
            "The first workspace builds the Android app.", "The second one runs the test suite.");
        Split("- **First** item of the list here\n- Second item of the list here", "First item of the list here", "Second item of the list here");
        // a very long sentence is cut at a comma
        string longOne = "The agent read the build script, changed the gradle settings to use less memory, ran the build again with the new limits, and then waited for the device to come back online before it started the tests";
        var ls = SpokenText.Sentences(longOne);
        Check(ls.Count >= 2 && ls.All(x => x.Length <= SpokenText.LongSentence) && string.Join(" ", ls) == longOne, "a long sentence is cut at commas: " + Show(ls));
        Check(SpokenText.Sentences("").Count == 0 && SpokenText.Sentences("   \n ").Count == 0 && SpokenText.Sentences(null).Count == 0, "empty text: no sentences");
        Check(SpokenText.Sentences("No punctuation at all here").SequenceEqual(new[] { "No punctuation at all here" }), "no punctuation: one sentence");
        Check(SpokenText.Sentences("```\ncode block\n```\nThe code above is the fix for it.").SequenceEqual(new[] { "The code above is the fix for it." }), "code blocks are not read aloud");
    }

    private static void Speakable()
    {
        void S(string text, bool it, string want)
        {
            string got = SpokenText.Speakable(text, it);
            Check(got == want, "speakable: " + text, "got  " + got + "\nwant " + want);
        }
        S("It's been working for 3 min 20 s; now running npm test.", false, "It's been working for 3 minutes 20 seconds; now running npm test.");
        S("Nothing new for 1 min: it may be stuck.", false, "Nothing new for 1 minute: it may be stuck.");
        S("Quiet for 40 s, still within normal.", false, "Quiet for 40 seconds, still within normal.");
        S("It answered 1.5 h ago, in 250 ms.", false, "It answered 1.5 hours ago, in 250 milliseconds.");
        S("Sta lavorando da 3 min e 1 s.", true, "Sta lavorando da 3 minuti e 1 secondo.");
        S("Fermo da 2 h.", true, "Fermo da 2 ore.");
        S("Music from the 1990s and 3 minutes of silence.", false, "Music from the 1990s and 3 minutes of silence.");
        S("Opened GIT · Closed 2 panels", false, "Opened GIT, Closed 2 panels");
        S("It moved main -> feat/menu.", false, "It moved main to feat/menu.");
        S("The file foo.cs has 12 lines; s is a variable, min() a function.", false, "The file foo.cs has 12 lines; s is a variable, min() a function.");

        Check(SpokenText.Duration(40) == "40 seconds" && SpokenText.Duration(1) == "1 second" && SpokenText.Duration(60) == "1 minute"
              && SpokenText.Duration(200) == "3 minutes 20 seconds" && SpokenText.Duration(900) == "15 minutes" && SpokenText.Duration(5400) == "1.5 hours"
              && SpokenText.Duration(3600) == "1 hour", "spoken durations (English)",
            string.Join(", ", new[] { 40, 1, 60, 200, 900, 5400, 3600 }.Select(x => SpokenText.Duration(x))));
        Check(SpokenText.Duration(40, true) == "40 secondi" && SpokenText.Duration(61, true) == "1 minuto" && SpokenText.Duration(200, true) == "3 minuti 20 secondi"
              && SpokenText.Duration(5400, true) == "1,5 ore", "spoken durations (Italian)",
            string.Join(", ", new[] { 40, 61, 200, 5400 }.Select(x => SpokenText.Duration(x, true))));

        Check(SpokenText.LooksItalian("Il thread sta ancora lavorando, non è bloccato."), "Italian reply detected");
        Check(!SpokenText.LooksItalian("It's been working for 3 minutes; it is not stuck."), "English reply is not Italian");
        Check(!SpokenText.LooksItalian("") && !SpokenText.LooksItalian("OK"), "no words: English");
    }

    private static void ForSpeech()
    {
        string reply = "The build finished with no errors after 4 minutes. Two threads are waiting for you in the second workspace. " +
                       "The terminal has a new message from the agent. It also updated the docs.";
        var f = SpokenText.ForSpeech(reply);
        Check(f.Count == 2 && f[0].StartsWith("The build finished") && f[1].StartsWith("Two threads") && f.Sum(x => x.Length) <= 200, "a reply speaks its first 2 sentences: " + Show(f));
        var one = SpokenText.ForSpeech("It's idle. " + new string('x', 10) + " " + string.Join(" ", Enumerable.Repeat("word", 70)) + ".");
        Check(one.Count >= 1 && one.Sum(x => x.Length) <= 230, "a reply never speaks much more than 200 characters: " + one.Sum(x => x.Length));
        var huge = SpokenText.ForSpeech(string.Join(" ", Enumerable.Repeat("wordy", 120)));
        Check(huge.Count == 1 && huge[0].Length <= 202 && huge[0].EndsWith("…"), "one endless sentence is cut at a word: " + huge[0].Length);
        Check(SpokenText.ForSpeech("Short answer.").SequenceEqual(new[] { "Short answer." }), "a short reply is spoken whole");
        Check(SpokenText.ForSpeech("").Count == 0, "an empty reply speaks nothing");
    }
}
