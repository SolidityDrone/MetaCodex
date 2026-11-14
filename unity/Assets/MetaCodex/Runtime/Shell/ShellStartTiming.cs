using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Where the start's time goes (perf/first-run). The 0.7.19 first-start log has one frame of 5,861 ms and about 4 s
    /// between the last line of the scripts' start and "shell up: 20 panels", with nothing logged in between. Build marks
    /// the start of each piece (StartMark: the vault, the voice, the context, each panel, the wrist menu, the orb, the splash);
    /// Start writes one line when Build is done: "[start] shell built in N ms: ..." with every piece that took 15 ms or more,
    /// in order. A mark lasts until the next one, so the number after a name is that piece plus whatever followed it up to the
    /// next mark. No cost afterwards (the list stops growing once reported).
    public sealed partial class MetaCodexShell
    {
        private readonly List<(string name, double at)> _startMarks = new List<(string, double)>(48);
        private bool _startReported;

        private void StartMark(string name)
        {
            if (!_startReported && _startMarks.Count < 200) _startMarks.Add((name, _hitchSw.Elapsed.TotalMilliseconds));
        }

        private void ReportStart(double begin)
        {
            _startReported = true;
            double end = _hitchSw.Elapsed.TotalMilliseconds;
            var sb = new StringBuilder(200);
            sb.Append("[start] shell built in ").Append((end - begin).ToString("F0")).Append(" ms");
            if (_startMarks.Count > 0) sb.Append(", before the first mark ").Append((_startMarks[0].at - begin).ToString("F0"));
            for (int i = 0; i < _startMarks.Count; i++)
            {
                double dur = (i + 1 < _startMarks.Count ? _startMarks[i + 1].at : end) - _startMarks[i].at;
                if (dur >= 15) sb.Append(", ").Append(_startMarks[i].name).Append(' ').Append(dur.ToString("F0"));
            }
            sb.Append(" (ms)");
            Log(sb.ToString());
            _startMarks.Clear();
        }
    }
}
