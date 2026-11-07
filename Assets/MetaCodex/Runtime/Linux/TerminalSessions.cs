using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MetaCodex.Linux
{
    /// A byte stream to a program running in a terminal: output is read on the main thread with TryRead,
    /// input and resizes go through a background writer, so a full pty never blocks a frame.
    public interface ITerminalSession : IDisposable
    {
        string Description { get; }
        bool IsRunning { get; }
        int ExitCode { get; } // -1 while running
        bool TryRead(out byte[] chunk);
        void Write(byte[] data);
        void Resize(int rows, int cols);
    }

    /// A process in a pseudo-terminal (Android: proot + bash), through libmcxpty.
    public sealed class PtySession : ITerminalSession
    {
        public string Description { get; }
        public int Pid { get; }
        public bool IsRunning => _exitCode < 0;
        public int ExitCode => _exitCode;

        private readonly int _fd;
        private volatile int _exitCode = -1;
        private volatile bool _stop;
        private readonly ConcurrentQueue<byte[]> _out = new ConcurrentQueue<byte[]>();
        private readonly BlockingCollection<Action> _in = new BlockingCollection<Action>();
        private readonly Thread _reader, _writer;
        private readonly Action<string> _log;

        private PtySession(int fd, int pid, string description, Action<string> log)
        {
            _fd = fd;
            Pid = pid;
            Description = description;
            _log = log;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" };
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "pty-write" };
            _reader.Start();
            _writer.Start();
        }

        public static PtySession Start(string path, string[] argv, string[] env, string cwd, int rows, int cols, Action<string> log)
        {
            var a = PtyNative.Blob(argv, out int al);
            var e = PtyNative.Blob(env, out int el);
            int fd = PtyNative.mcx_pty_spawn(path, a, al, argv.Length, e, el, env.Length, cwd, rows, cols, out int pid);
            if (fd < 0) throw new InvalidOperationException($"spawn {path}: {PtyNative.ErrorText(fd)}");
            log?.Invoke($"pty: pid {pid} fd {fd} {rows}x{cols}: {path} {string.Join(" ", argv, 1, argv.Length - 1)}");
            return new PtySession(fd, pid, System.IO.Path.GetFileName(path), log);
        }

        public bool TryRead(out byte[] chunk) => _out.TryDequeue(out chunk);

        public void Write(byte[] data)
        {
            if (data == null || data.Length == 0 || _stop) return;
            _in.Add(() => PtyNative.mcx_pty_write(_fd, data, data.Length));
        }

        public void Resize(int rows, int cols)
        {
            if (_stop) return;
            _in.Add(() => PtyNative.mcx_pty_resize(_fd, rows, cols));
        }

        private void ReadLoop()
        {
            var buf = new byte[16384];
            try
            {
                int reaped = -1;
                while (!_stop)
                {
                    int n = PtyNative.mcx_pty_read(_fd, buf, buf.Length, 250);
                    if (n > 0)
                    {
                        var c = new byte[n];
                        Buffer.BlockCopy(buf, 0, c, 0, n);
                        _out.Enqueue(c);
                    }
                    else if (n < 0) break; // -EIO: every writer of the terminal is gone
                    else
                    {
                        int w = PtyNative.mcx_pty_wait(Pid, 1); // idle: did the child exit?
                        if (w >= 0) { reaped = w; break; }
                    }
                }
                if (reaped >= 0) _exitCode = reaped;
                // the terminal closed: collect the exit code (bounded wait, then kill)
                for (int i = 0; i < 50 && !_stop && _exitCode < 0; i++)
                {
                    int code = PtyNative.mcx_pty_wait(Pid, 1);
                    if (code >= 0 || code == -2) { _exitCode = Math.Max(0, code); break; }
                    Thread.Sleep(100);
                }
                if (_exitCode < 0 && !_stop)
                {
                    PtyNative.mcx_pty_kill(Pid, 9);
                    int code = PtyNative.mcx_pty_wait(Pid, 0);
                    _exitCode = code >= 0 ? code : 137;
                }
                _log?.Invoke($"pty: pid {Pid} exited with {_exitCode}");
            }
            catch (Exception ex)
            {
                _log?.Invoke("pty reader: " + ex.Message);
                if (_exitCode < 0) _exitCode = 255;
            }
        }

        private void WriteLoop()
        {
            try
            {
                foreach (var action in _in.GetConsumingEnumerable())
                {
                    if (_stop) break;
                    action();
                }
            }
            catch (Exception ex) { _log?.Invoke("pty writer: " + ex.Message); }
        }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            _in.CompleteAdding();
            if (_exitCode < 0)
            {
                PtyNative.mcx_pty_kill(Pid, 1); // SIGHUP, as a closing terminal does
                for (int i = 0; i < 10 && PtyNative.mcx_pty_wait(Pid, 1) == -1; i++) Thread.Sleep(50);
                if (PtyNative.mcx_pty_wait(Pid, 1) == -1) { PtyNative.mcx_pty_kill(Pid, 9); PtyNative.mcx_pty_wait(Pid, 0); }
            }
            PtyNative.mcx_pty_close(_fd);
        }
    }

    /// A terminal served by another machine (Tools/ptyserver.py in WSL, or any host running it): the
    /// Editor uses it to drive the real view against a real shell; the "remote userland" mode later.
    /// Wire format to the server: 0x00 len16 bytes = input, 0x01 rows16 cols16 = resize. From it: raw output.
    public sealed class TcpTerminalSession : ITerminalSession
    {
        public string Description { get; }
        public bool IsRunning => _exitCode < 0;
        public int ExitCode => _exitCode;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private volatile int _exitCode = -1;
        private readonly ConcurrentQueue<byte[]> _out = new ConcurrentQueue<byte[]>();
        private readonly BlockingCollection<byte[]> _in = new BlockingCollection<byte[]>();

        public TcpTerminalSession(string host, int port, int rows, int cols)
        {
            Description = $"tcp {host}:{port}";
            _client = new TcpClient();
            _client.Connect(host, port);
            _client.NoDelay = true;
            _stream = _client.GetStream();
            Resize(rows, cols);
            new Thread(ReadLoop) { IsBackground = true, Name = "tcp-term-read" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "tcp-term-write" }.Start();
        }

        public bool TryRead(out byte[] chunk) => _out.TryDequeue(out chunk);

        public void Write(byte[] data)
        {
            for (int off = 0; off < data.Length; off += 60000)
            {
                int n = Math.Min(60000, data.Length - off);
                var frame = new byte[3 + n];
                frame[0] = 0;
                frame[1] = (byte)(n >> 8);
                frame[2] = (byte)n;
                Buffer.BlockCopy(data, off, frame, 3, n);
                if (!_in.IsAddingCompleted) _in.Add(frame);
            }
        }

        public void Resize(int rows, int cols)
        {
            if (!_in.IsAddingCompleted) _in.Add(new byte[] { 1, (byte)(rows >> 8), (byte)rows, (byte)(cols >> 8), (byte)cols });
        }

        private void ReadLoop()
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = _stream.Read(buf, 0, buf.Length)) > 0)
                {
                    var c = new byte[n];
                    Buffer.BlockCopy(buf, 0, c, 0, n);
                    _out.Enqueue(c);
                }
            }
            catch (Exception) { }
            _exitCode = 0;
        }

        private void WriteLoop()
        {
            try { foreach (var f in _in.GetConsumingEnumerable()) _stream.Write(f, 0, f.Length); }
            catch (Exception) { _exitCode = 0; }
        }

        public void Dispose()
        {
            _in.CompleteAdding();
            try { _client.Close(); } catch (Exception) { }
        }
    }

    /// No Linux available (Editor without a bridge): a local line editor that echoes, so the terminal
    /// view, keyboard and key bar can still be exercised.
    public sealed class EchoSession : ITerminalSession
    {
        public string Description => "echo";
        public bool IsRunning => true;
        public int ExitCode => -1;
        private readonly ConcurrentQueue<byte[]> _out = new ConcurrentQueue<byte[]>();
        private readonly StringBuilder _line = new StringBuilder();

        public EchoSession(string banner)
        {
            Emit(banner.Replace("\n", "\r\n") + "\r\n$ ");
        }

        public bool TryRead(out byte[] chunk) => _out.TryDequeue(out chunk);

        public void Write(byte[] data)
        {
            foreach (char c in Encoding.UTF8.GetString(data))
            {
                if (c == '\r') { Emit("\r\n" + (_line.Length > 0 ? "echo: " + _line + "\r\n" : "") + "$ "); _line.Clear(); }
                else if (c == '\x7f') { if (_line.Length > 0) { _line.Length--; Emit("\b \b"); } }
                else if (c == '\x03') { _line.Clear(); Emit("^C\r\n$ "); }
                else if (c >= ' ') { _line.Append(c); Emit(c.ToString()); }
            }
        }

        public void Resize(int rows, int cols) { }

        private void Emit(string s) => _out.Enqueue(Encoding.UTF8.GetBytes(s));

        public void Dispose() { }
    }
}
