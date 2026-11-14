using System.Collections.Generic;
using MetaCodex.UI;

namespace MetaCodex.Shell
{
    /// Thread groups: not in this step yet (the multi-window refactor alone); every window is free. The next commit
    /// replaces this file with the groups.
    public sealed partial class MetaCodexShell
    {
        private sealed class ThreadGroup
        {
            public int Number;
            public UiPanel Panel;
            public readonly List<ThreadWindow> Members = new List<ThreadWindow>();
        }

        private readonly HashSet<string> _pulledOut = new HashSet<string>();
        private bool IsGroupedPanel(UiPanel p) => false;
        private bool IsResizing(UiPanel p) => false;
        private ThreadGroup GroupOfPanel(UiPanel p) => null;
        private void RebuildGroupPanels() { }
        private void ApplyGroupScales() { }
        private void RemoveFromGroup(ThreadWindow w, bool pulledOut) { w.Group = null; }
        private void AutoGroup(ThreadWindow w) { }
        private void TickThreadGroups(float dt) { }
        private void RegisterThreadWindowVoiceCommand() { }
        private void UpdateThreadWindowVoice() { }
    }
}
