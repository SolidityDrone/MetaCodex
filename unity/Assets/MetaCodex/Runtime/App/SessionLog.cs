using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MetaCodex.App
{
    /// "[MetaCodex]" lines to logcat and to files/session_log.txt in the app's shared folder, so a session
    /// run unplugged can be read later: adb pull /sdcard/Android/data/com.pnncl.metacodex/files/session_log.txt
    public static class SessionLog
    {
        private static readonly object Gate = new object();
        private static readonly LinkedList<string> Recent = new LinkedList<string>();
        private static StreamWriter _file;
        public static string FilePath { get; private set; }

        public static void Init()
        {
            // a development build captures and prints the managed stack of every Debug.Log call (18 lines under each
            // "[MetaCodex] ..." line in the 0.7.19 logcat): a stack walk on the main thread per line, and the log's bulk.
            // Plain lines carry none now; warnings, errors and exceptions keep theirs.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            lock (Gate)
            {
                if (_file != null) return;
                try
                {
                    FilePath = Path.Combine(Application.persistentDataPath, "session_log.txt");
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 4 * 1024 * 1024)
                        File.Copy(FilePath, FilePath + ".old", true);
                    bool append = File.Exists(FilePath) && new FileInfo(FilePath).Length <= 4 * 1024 * 1024;
                    _file = new StreamWriter(FilePath, append) { AutoFlush = true };
                }
                catch (Exception ex) { Debug.LogWarning("[MetaCodex] session log unavailable: " + ex.Message); }
            }
            Application.logMessageReceivedThreaded += (msg, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error) Write($"{type}: {msg} {FirstLine(stack)}", false);
            };
        }

        public static void Log(string message) => Write(message, true);

        private static void Write(string message, bool toUnity)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            if (toUnity) Debug.Log("[MetaCodex] " + message);
            lock (Gate)
            {
                Recent.AddLast(line);
                while (Recent.Count > 500) Recent.RemoveFirst();
                try { _file?.WriteLine(line); } catch (Exception) { }
            }
        }

        public static List<string> Tail(int n)
        {
            lock (Gate)
            {
                var r = new List<string>(Recent);
                return r.GetRange(Math.Max(0, r.Count - n), Math.Min(n, r.Count));
            }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i);
        }
    }
}
