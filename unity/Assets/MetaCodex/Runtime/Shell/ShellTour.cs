namespace MetaCodex.Shell
{
    /// The guided tour of the first start (ShellFirstRun starts it beside the SETUP panel).
    public sealed partial class MetaCodexShell
    {
        private int _tourStep = -1;
        private bool _tourOffered;

        /// Starts the guided tour.
        public void StartTour() { }

        private void TickTour(float now, float dt) { }
    }
}
