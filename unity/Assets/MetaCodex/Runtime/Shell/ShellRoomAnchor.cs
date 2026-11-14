using System;
using System.Linq;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The panels stay in the room across a native app (owner 2026-11-14: "when we close a native app we launched from
    /// within MetaCodex, we get back and the panels are not exactly as they were").
    /// Cause: the tracking space (Floor Level, a local space) is set up again when MetaCodex resumes, so the room moves
    /// under the panels, which keep their Unity coordinates. Meta's docs say the local space is not an app-independent
    /// reference (unity-spatial-anchors-overview: Floor Level needs anchors to stay in sync), and that the Stage space is
    /// not changed by the user's own recenter (unity-boundary: "return from a paused state").
    /// Fix: while the app is live, the Stage origin's pose in Unity world is read every 0.25 s (the room reference). On
    /// resume, once the headset tracks again, the shell (every window, orb, wrist menu, toasts: all under _root) moves by
    /// the change of that reference, so each panel keeps its place relative to the room. No permission, no manifest flag.
    /// The move is logged on resume: how far the panels moved (mean and most, cm) and how much they turned (deg).
    public sealed partial class MetaCodexShell
    {
        /// The room reference: the Stage origin (not the user's recenter; not the Stationary origin, which is experimental
        /// and needs the experimental manifest flag). Change here to test another origin on the headset.
        public const OVRPlugin.TrackingOrigin RoomReferenceOrigin = OVRPlugin.TrackingOrigin.Stage;
        /// A reference that moved more than this (metres, degrees) is a room change, not a re-centre: nothing is moved.
        public const float RoomMaxShiftM = 3f, RoomMaxTurnDeg = 120f;
        /// After a resume: how long to wait for tracking to come back before giving up.
        public const float RoomWaitSeconds = 10f;

        private Pose? _roomAnchorLast;          // the room reference as last read while the app was live (Unity world)
        private float _roomAnchorSampleAt;
        private bool _roomAnchorWaiting;        // resumed: the correction waits for the headset to track again
        private float _roomAnchorResumeAt;
        private int _roomAnchorResumeFrame;
        private bool _roomAnchorErrorLogged;

        /// Pure geometry: the rigid move that takes the room as it was (`before`) to where it is now (`after`). A point
        /// fixed to the room at `before` is at `move + turn * p` after it, so it keeps its place relative to the room.
        public static (Vector3 move, Quaternion turn) RoomCorrection(Pose before, Pose after)
        {
            var turn = after.rotation * Quaternion.Inverse(before.rotation);
            return (after.position - turn * before.position, turn);
        }

        /// The room reference now in Unity world coordinates, or null when tracking has none (not tracked, the plugin
        /// has no pose: it answers the identity then, and a real room origin is never exactly there).
        private Pose? ReadRoomReference()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (!OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head)) return null;
                OVRPose local = OVRPlugin.GetTrackingTransformRelativePose(RoomReferenceOrigin).ToOVRPose();
                if (local.position == Vector3.zero && local.orientation == Quaternion.identity) return null;
                var space = TrackingSpace();
                if (space == null) return null;
                return new Pose(space.TransformPoint(local.position), space.rotation * local.orientation);
            }
            catch (Exception ex)
            {
                if (!_roomAnchorErrorLogged) { _roomAnchorErrorLogged = true; Log("room reference: " + ex.Message); }
                return null;
            }
#else
            return null;
#endif
        }

        /// From TickSessionContext, every frame.
        private void TickRoomAnchor()
        {
            if (_roomAnchorWaiting)
            {
                // the first frames after a resume still hold the poses from before the pause
                if (Time.frameCount < _roomAnchorResumeFrame + 2) return;
                var now = ReadRoomReference();
                if (now == null)
                {
                    if (Time.unscaledTime - _roomAnchorResumeAt > RoomWaitSeconds)
                    {
                        _roomAnchorWaiting = false;
                        Log($"room anchor: no room reference {RoomWaitSeconds:F0} s after the resume: panels stay where they are");
                    }
                    return;
                }
                _roomAnchorWaiting = false;
                if (_roomAnchorLast == null) Log("room anchor: no room reference was read before the app was away: panels stay where they are");
                else ApplyRoomReference(_roomAnchorLast.Value, now.Value, "the native app", out _, out _);
                _roomAnchorLast = now;
                return;
            }
            if (Time.unscaledTime < _roomAnchorSampleAt) return;
            _roomAnchorSampleAt = Time.unscaledTime + 0.25f;
            var s = ReadRoomReference();
            if (s != null) _roomAnchorLast = s;
        }

        /// From OnApplicationPause (ShellContext). Pause: the last reference is the one the panels were placed with (it is
        /// read once more, unless a correction is still pending). Resume: wait for tracking, then move the shell back.
        private void RoomAnchorPaused(bool paused)
        {
            if (paused)
            {
                if (!_roomAnchorWaiting) { var s = ReadRoomReference(); if (s != null) _roomAnchorLast = s; }
                return;
            }
            if (_roomAnchorWaiting) return;
            _roomAnchorWaiting = true;
            _roomAnchorResumeAt = Time.unscaledTime;
            _roomAnchorResumeFrame = Time.frameCount;
        }

        /// Moves the whole shell by the change of the room reference, so everything in it keeps its place in the room.
        /// Logs the move: the mean and the most a panel moved (cm) and the turn (deg). Returns false when nothing moved.
        private bool ApplyRoomReference(Pose before, Pose after, string why, out float meanCm, out float turnDeg)
        {
            meanCm = turnDeg = 0f;
            var (move, turn) = RoomCorrection(before, after);
            turnDeg = Quaternion.Angle(Quaternion.identity, turn);
            float roomShift = Vector3.Distance(before.position, after.position);
            if (_root == null) { Log("room anchor: no shell: nothing moved"); return false; }
            if (roomShift > RoomMaxShiftM || turnDeg > RoomMaxTurnDeg)
            {
                Log($"room anchor: the room reference moved {roomShift:F2} m and turned {turnDeg:F0} deg after {why}: too far to be a re-centre, panels stay where they are");
                return false;
            }
            var open = _root.GetComponentsInChildren<UiPanel>(false).Where(p => p.IsOpen).ToList();
            var was = open.Select(p => p.transform.position).ToList();
            _root.SetPositionAndRotation(turn * _root.position + move, turn * _root.rotation);
            float sum = 0f, most = 0f;
            for (int i = 0; i < open.Count; i++)
            {
                float d = Vector3.Distance(was[i], open[i].transform.position);
                sum += d;
                most = Mathf.Max(most, d);
            }
            meanCm = open.Count > 0 ? sum / open.Count * 100f : move.magnitude * 100f;
            Log($"room anchor: back in the room after {why}: panels moved {meanCm:F1} cm (most {most * 100f:F1} cm), turned {turnDeg:F1} deg, {open.Count} open");
            return true;
        }

        /// Self-test: the room reference changes from `before` to `after` (as after a native app); the shell's answer.
        public bool RoomAnchorForTest(Pose before, Pose after, out float meanCm, out float turnDeg)
        {
            _roomAnchorLast = before;
            bool moved = ApplyRoomReference(before, after, "test", out meanCm, out turnDeg);
            _roomAnchorLast = after;
            return moved;
        }
    }
}
