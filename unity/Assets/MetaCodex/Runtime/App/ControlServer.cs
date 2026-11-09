using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace MetaCodex.App
{
    /// Line protocol on 127.0.0.1 for agents and tests: drive the terminal and read its screen without
    /// wearing the headset (adb forward tcp:47800 tcp:47800; tools/qterm). The first line must be
    /// "auth <token>"; the token is new at every launch and written to files/control.token in the app's
    /// shared folder, which only adb and this app can read. Every reply ends with a line ".".
    /// Commands run on the main thread (Pump), one at a time.
    public sealed class ControlServer : IDisposable
    {
        public const int DefaultPort = 47800;
        public string Token { get; }
        public int Port { get; }

        private readonly TcpListener _listener;
        private readonly Func<string, string> _handler;
        /// Commands that answer later (e.g. a secret request waiting for the user's approval in VR): return true
        /// to take the command and call the reply action once, from any thread, within LongWaitSeconds.
        public Func<string, Action<string>, bool> AsyncHandler;
        public const int LongWaitSeconds = 120;
        private readonly ConcurrentQueue<(string cmd, Action<string> reply)> _queue = new ConcurrentQueue<(string, Action<string>)>();
        private volatile bool _stop;

        /// Same time whatever the input (no timing hints about the token).
        private static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int d = a.Length ^ b.Length;
            for (int i = 0; i < b.Length; i++) d |= (i < a.Length ? a[i] : 0) ^ b[i];
            return d == 0;
        }

        public ControlServer(int port, Func<string, string> handler)
        {
            Port = port;
            _handler = handler;
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "control.token"), Token);
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "control-accept" }.Start();
        }

        /// Runs queued commands; call from Update.
        public void Pump()
        {
            for (int i = 0; i < 8 && _queue.TryDequeue(out var item); i++)
            {
                string result;
                try
                {
                    if (AsyncHandler != null && AsyncHandler(item.cmd, item.reply)) continue;
                    result = _handler(item.cmd);
                }
                catch (Exception ex) { result = "error: " + ex.Message; }
                item.reply(result);
            }
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { if (_stop) return; Thread.Sleep(200); continue; }
                new Thread(() => Serve(client)) { IsBackground = true, Name = "control-client" }.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                try
                {
                    string first = reader.ReadLine();
                    if (first == null || !FixedEquals(first.Trim(), "auth " + Token))
                    {
                        writer.WriteLine("error: auth");
                        writer.WriteLine(".");
                        return;
                    }
                    writer.WriteLine("ok");
                    writer.WriteLine(".");
                    string line;
                    while (!_stop && (line = reader.ReadLine()) != null)
                    {
                        if (line.Trim() == "quit") return;
                        var done = new ManualResetEventSlim(false);
                        string result = null;
                        _queue.Enqueue((line, r => { if (!done.IsSet) { result = r; done.Set(); } }));
                        int wait = line.StartsWith("secret ") || line.StartsWith("voice ") || line.StartsWith("view wait") ? LongWaitSeconds : 10;
                        if (!done.Wait(TimeSpan.FromSeconds(wait))) result = "error: timeout (app paused, or nobody answered in VR)";
                        writer.WriteLine(result ?? "");
                        writer.WriteLine(".");
                    }
                }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch (Exception) { }
        }

        /// "\n" -> Enter, "\e" -> Esc, "\t", "\xHH", "\\" in text typed through the port.
        public static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': case 'r': sb.Append('\r'); break;
                    case 'e': sb.Append('\x1b'); break;
                    case 't': sb.Append('\t'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'x' when i + 2 < s.Length:
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 2), 16));
                        i += 2;
                        break;
                    default: sb.Append('\\').Append(n); break;
                }
            }
            return sb.ToString();
        }
    }
}
