using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Where the start's time goes. The start is a list of pieces (BuildSteps: the settings file, the splash, each font,
    /// the router, the vault, the voice, the scripts, every panel, the wrist menu, the Whisp, the guide card, the glyphs);
    /// each piece is timed alone. When the last one ran, one line goes to the log:
    /// "[start] shell built in N ms over F frames: main thread M ms, longest frame L ms (longest piece P x ms), splash on
    /// show at +S ms: settings file 4, splash 22, ... (ms)", listing every piece of 5 ms or more in order.
    /// N is the wall time from the first piece to the last; M the sum of the pieces; L the most main-thread time the start
    /// used in one frame (StartFrameBudgetMs plus the piece that crossed it). No cost afterwards.
    public sealed partial class MetaCodexShell
    {
        private readonly List<(string name, double ms)> _startTimes = new List<(string, double)>(64);
        private bool _startReported;
        private int _startFrames;
        private double _startMainMs, _startLongestFrame, _splashShownAtMs = -1;

        /// The build methods still mark where they begin (from the time one call built everything): the pieces are timed
        /// by the start itself now, so a mark does nothing.
        private void StartMark(string name) { }

        private void NoteStartPiece(string name, double ms)
        {
            if (_startReported) return;
            _startTimes.Add((name, ms));
            _startMainMs += ms;
        }

        private void NoteStartFrame(double ms)
        {
            if (_startReported) return;
            _startFrames++;
            if (ms > _startLongestFrame) _startLongestFrame = ms;
        }

        /// The splash became visible (ShellSplash): its time from the start's first piece goes into the line.
        private void NoteSplashShown()
        {
            if (_splashShownAtMs < 0) _splashShownAtMs = _hitchSw.Elapsed.TotalMilliseconds - _startBegin;
        }

        private void ReportStart(double begin)
        {
            if (_startReported) return;
            _startReported = true;
            double end = _hitchSw.Elapsed.TotalMilliseconds;
            if (_startFrames == 0) { _startFrames = 1; _startLongestFrame = _startMainMs; } // Build: all in one call
            string longest = "";
            double longestMs = -1;
            foreach (var (name, ms) in _startTimes) if (ms > longestMs) { longestMs = ms; longest = name; }
            var sb = new StringBuilder(400);
            sb.Append("[start] shell built in ").Append((end - begin).ToString("F0")).Append(" ms over ").Append(_startFrames)
              .Append(" frames: main thread ").Append(_startMainMs.ToString("F0")).Append(" ms, longest frame ").Append(_startLongestFrame.ToString("F0"))
              .Append(" ms (longest piece ").Append(longest).Append(' ').Append(longestMs.ToString("F0")).Append(" ms)");
            if (_splashShownAtMs >= 0) sb.Append(", splash on show at +").Append(_splashShownAtMs.ToString("F0")).Append(" ms");
            sb.Append(':');
            bool any = false;
            foreach (var (name, ms) in _startTimes)
            {
                if (ms < 5) continue;
                sb.Append(any ? ", " : " ").Append(name).Append(' ').Append(ms.ToString("F0"));
                any = true;
            }
            sb.Append(" (ms)");
            Log(sb.ToString());
            _startTimes.Clear();
        }
    }
}
