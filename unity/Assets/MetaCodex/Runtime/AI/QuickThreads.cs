using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    // The orb's quick list (owner 2026-11-11, docs/ui.md): under its billboard, the five threads used last, each with
    // its project, its title and its state in a word and a colour: working (blue, it pulses), done (green), needs you
    // or waiting (orange), error (red). Pure (no Unity objects): tested without the app.

    public enum QuickState { Working, Done, NeedsYou, Waiting, Error, Stopped }

    /// One row of the quick list.
    public sealed class QuickThread
    {
        public string Id = "", ProjectId = "", Project = "", Title = "";
        public QuickState State = QuickState.Done;
        /// The state in a word or two, as shown: "WORKING 3:12", "DONE · 4 MIN", "NEEDS YOU", "WAITING", "ERROR".
        public string Word = "";
        /// It finished while its window was not open (its answer has not been seen).
        public bool Fresh;
        /// Working rows pulse; the others keep their colour.
        public bool Pulses => State == QuickState.Working;
        /// Needs the user: a question, an approval, a turn held in the queue, no host.
        public bool Attention => State == QuickState.NeedsYou || State == QuickState.Waiting;
    }

    public static class QuickThreads
    {
        public const int Max = 5;

        private static string S(JToken t) => t != null && t.Type == JTokenType.String ? (string)t : null;
        private static long L(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (long)t : 0;
        private static string Status(JObject t) => S(ThreadTimeline.G(ThreadTimeline.G(t, "runtime"), "displayStatus")) ?? S(t?["status"]) ?? "";

        /// The threads to list, most recently used first: used here by the user (opened, written to: `usedAt`, unix ms)
        /// or by its agent (the row's updatedAt), whichever is later. Archived threads are not in MQH's list: they drop out.
        public static List<string> Order(JArray threads, IReadOnlyDictionary<string, long> usedAt, int max = Max)
        {
            var list = new List<(string id, long at)>();
            var seen = new HashSet<string>();
            foreach (var t in (threads ?? new JArray()).OfType<JObject>())
            {
                string id = S(t["id"]);
                if (id == null || !seen.Add(id)) continue;
                long at = L(t["updatedAt"]);
                if (usedAt != null && usedAt.TryGetValue(id, out var u) && u > at) at = u;
                list.Add((id, at));
            }
            return list.OrderByDescending(x => x.at).ThenBy(x => x.id, StringComparer.Ordinal).Take(Math.Max(0, max)).Select(x => x.id).ToList();
        }

        /// A row's state. The thread's row (MQH's status, its queue, a pending question) decides; the pulse (its last
        /// timeline read, when there is one) adds the turn's clock, a question seen in the timeline, an error or a stop.
        public static QuickThread Describe(JObject thread, string projectName, ThreadPulse pulse, long nowMs, bool fresh)
        {
            var q = new QuickThread
            {
                Id = S(thread?["id"]) ?? "",
                ProjectId = S(thread?["projectId"]) ?? "",
                Project = (projectName ?? "").Trim(),
                Title = Regex.Replace(WorkspaceMap.TitleOf(thread), @"\s+", " ").Trim(),
            };
            string st = Status(thread), queued = S(thread?["queuedWork"]) ?? "";
            bool working = ThreadTimeline.IsWorking(st);
            bool asks = thread?["hasPendingInteraction"]?.Type == JTokenType.Boolean && (bool)thread["hasPendingInteraction"]
                        || st.Contains("approval") || st.Contains("wait") && st != "waiting-for-host"
                        || working && pulse != null && pulse.State == "waiting";
            // as ThreadPulse: a turn MQH holds in the queue (the workspace guard, a limit) has not started
            bool held = queued == "waiting" && st != "active" && st != "starting" && st != "stopping" && st != "running";
            if (st == "error") Set(q, QuickState.Error, "ERROR");
            else if (asks) Set(q, QuickState.NeedsYou, "NEEDS YOU");
            else if (queued == "failed") Set(q, QuickState.Error, "NOT SENT");
            else if (held) Set(q, QuickState.Waiting, "WAITING");
            else if (st == "waiting-for-host") Set(q, QuickState.Waiting, "OFFLINE"); // MQH waits for a host to run it
            else if (working)
            {
                bool starting = st == "pending" || st == "provisioning" || st == "starting";
                int secs = pulse != null && pulse.Working && pulse.TurnStart > 0 && nowMs > pulse.TurnStart ? (int)((nowMs - pulse.TurnStart) / 1000) : -1;
                Set(q, QuickState.Working, st == "stopping" ? "STOPPING" : starting && secs < 0 ? "STARTING" : secs >= 0 ? "WORKING " + ThreadPulse.Clock(secs) : "WORKING");
            }
            else if (pulse != null && pulse.State == "error") Set(q, QuickState.Error, "ERROR");
            else if (pulse != null && pulse.State == "stopped") Set(q, QuickState.Stopped, "STOPPED");
            else
            {
                long at = pulse != null && pulse.AnsweredAt > 0 ? pulse.AnsweredAt : L(thread?["updatedAt"]);
                Set(q, QuickState.Done, fresh ? "DONE · NEW" : "DONE" + Since(at, nowMs));
                q.Fresh = fresh;
            }
            return q;
        }

        private static void Set(QuickThread q, QuickState s, string word) { q.State = s; q.Word = word; }

        /// " · 4 MIN" since `at` (nothing when unknown).
        public static string Since(long at, long nowMs)
        {
            if (at <= 0 || nowMs <= 0) return "";
            double m = Math.Max(0, (nowMs - at) / 60000.0);
            return m < 1 ? " · NOW" : m < 60 ? " · " + (int)m + " MIN" : m < 1440 ? " · " + (int)(m / 60) + " H" : " · " + ((int)(m / 1440)).ToString(CultureInfo.InvariantCulture) + " D";
        }
    }
}
