using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MetaCodex.Linux
{
    /// Work that runs inside the app process but must not take CPU from the frame loop (feat perf/first-run): a dedicated
    /// background thread at nice 10. The Linux unpack (8,393 files, 362 MB) and the AI runtime unpack ran on thread-pool
    /// threads at normal priority, on the same cores 3-5 as the main and render threads. Horizon OS keeps everything the app
    /// starts on those cores, so the only lever left is the thread's priority: at nice 10 the scheduler gives it about a
    /// tenth of the share of a normal thread while the frame loop wants the core, and all of it when the core is idle.
    /// (Programs the app starts, and the AI service, are already started with `nice -n 10`.)
    public static class LowPriority
    {
        public const int Nice = 10;

#if UNITY_ANDROID && !UNITY_EDITOR
        [DllImport("libc", EntryPoint = "setpriority")] private static extern int SetPriority(int which, uint who, int prio);
        [DllImport("libc", EntryPoint = "gettid")] private static extern int GetTid();
#endif

        /// Lowers the calling thread to nice 10. Android only (Linux's per-thread nice: PRIO_PROCESS with a thread id);
        /// elsewhere, and when the call fails, nothing changes. Returns true when it took.
        public static bool LowerCurrentThread()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { return SetPriority(0 /* PRIO_PROCESS */, (uint)GetTid(), Nice) == 0; }
            catch (Exception) { return false; }
#else
            return false;
#endif
        }

        /// `work` on its own background thread at low priority (a thread-pool thread would keep the lowered priority for the
        /// next job that borrows it). The task behaves like Task.Run's: IsFaulted, Exception, Result.
        public static Task<T> Run<T>(Func<T> work, string name)
        {
            var done = new TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                LowerCurrentThread();
                try { done.SetResult(work()); }
                catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true, Name = name };
            thread.Start();
            return done.Task;
        }

        public static Task Run(Action work, string name) => Run<bool>(() => { work(); return true; }, name);
    }
}
