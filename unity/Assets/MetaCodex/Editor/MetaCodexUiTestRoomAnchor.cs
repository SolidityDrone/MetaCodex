using System.Linq;
using MetaCodex.Shell;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `roomanchor` (owner 2026-11-14: after a native app the panels must be where they were in the room).
    /// The room reference (the Stage origin) moves while the app is away: the shell moves with it, so every open panel
    /// keeps its place relative to the room. Nothing moves when the reference did not change, and nothing moves for a
    /// change too big to be a re-centre (a move of 3 m or a turn of more than 120 deg). The shell is put back at the end.
    public static partial class MetaCodexUiTest
    {
        private static void RoomAnchorSection(MetaCodexShell shell)
        {
            shell.OpenPanel("dock"); // (in wrist mode the dock may stay shut: SETTINGS opens too)
            shell.OpenPanel("settings");
            var open = shell.PanelsById.Values.Where(p => p.IsOpen).ToList();
            Check(open.Count > 0, $"room anchor: panels open for the test ({open.Count})");
            var before = new Pose(new Vector3(0.4f, 0f, 0.3f), Quaternion.Euler(0f, 20f, 0f));
            var after = new Pose(new Vector3(-0.9f, 0f, 1.7f), Quaternion.Euler(0f, 65f, 0f));
            var at0 = open.Select(p => p.transform.position).ToList();

            // the room reference moved (a new local space after the app was away): the panels keep their room places
            bool moved = shell.RoomAnchorForTest(before, after, out float cm, out float deg);
            bool kept = true;
            for (int i = 0; i < open.Count; i++)
            {
                var a = open[i].transform.position;
                var relBefore = Quaternion.Inverse(before.rotation) * (at0[i] - before.position);
                var relAfter = Quaternion.Inverse(after.rotation) * (a - after.position);
                if (Vector3.Distance(relBefore, relAfter) > 0.001f) kept = false;
            }
            Check(moved && kept && cm > 1f && Mathf.Abs(deg - 45f) < 0.1f,
                $"room anchor: the reference moved, the panels keep their places in the room ({open.Count} panels, moved {cm:F1} cm, turned {deg:F1} deg; kept {kept})");

            // no change of the reference: nothing moves
            var at1 = open.Select(p => p.transform.position).ToList();
            bool still = shell.RoomAnchorForTest(after, after, out float cm0, out _) && cm0 < 0.01f
                && open.Select((p, i) => Vector3.Distance(p.transform.position, at1[i])).All(d => d < 0.001f);
            Check(still, "room anchor: no change of the reference, no move");

            // a change too big to be a re-centre: refused, nothing moves
            var far = new Pose(new Vector3(0f, 0f, 9f), after.rotation);
            var at2 = open.Select(p => p.transform.position).ToList();
            bool refusedFar = !shell.RoomAnchorForTest(after, far, out _, out _)
                && open.Select((p, i) => Vector3.Distance(p.transform.position, at2[i])).All(d => d < 0.001f);
            var turned = new Pose(after.position, Quaternion.Euler(0f, 200f, 0f));
            bool refusedTurn = !shell.RoomAnchorForTest(after, turned, out _, out _)
                && open.Select((p, i) => Vector3.Distance(p.transform.position, at2[i])).All(d => d < 0.001f);
            Check(refusedFar && refusedTurn, $"room anchor: a 9 m change or a turn of 135 deg is refused, nothing moves (far {refusedFar}, turn {refusedTurn})");

            // put the shell back where the test found it
            shell.RoomAnchorForTest(after, before, out _, out _);
            bool back = open.Select((p, i) => Vector3.Distance(p.transform.position, at0[i])).All(d => d < 0.001f);
            Check(back, "room anchor: the shell is back where the test found it");
        }
    }
}
