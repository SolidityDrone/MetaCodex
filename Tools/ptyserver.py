#!/usr/bin/env python3
"""Serve a shell in a pty over TCP for the Editor's TcpTerminalSession (MetaCodex's "remote userland").

Usage: python3 -I Tools/ptyserver.py [port] [command ...]      default: 47900, bash -l
Binds 127.0.0.1 only (WSL mirrored networking shares it with Windows). One client at a time; every
connection gets a fresh shell. Client frames: 0x00 len16 data = input, 0x01 rows16 cols16 = resize.
"""
import fcntl, os, pty, select, signal, socket, struct, sys, termios

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 47900
CMD = sys.argv[2:] or ["bash", "-l"]


def serve(conn):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ.update(TERM="xterm-256color", COLORTERM="truecolor", LANG="C.UTF-8")
        os.execvp(CMD[0], CMD)
    buf = b""
    try:
        while True:
            r, _, _ = select.select([conn, fd], [], [], 1.0)
            if fd in r:
                try:
                    data = os.read(fd, 16384)
                except OSError:
                    break
                if not data:
                    break
                conn.sendall(data)
            if conn in r:
                chunk = conn.recv(65536)
                if not chunk:
                    break
                buf += chunk
                while buf:
                    if buf[0] == 0 and len(buf) >= 3:
                        n = (buf[1] << 8) | buf[2]
                        if len(buf) < 3 + n:
                            break
                        os.write(fd, buf[3:3 + n])
                        buf = buf[3 + n:]
                    elif buf[0] == 1 and len(buf) >= 5:
                        rows, cols = (buf[1] << 8) | buf[2], (buf[3] << 8) | buf[4]
                        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
                        buf = buf[5:]
                    elif buf[0] in (0, 1):
                        break
                    else:
                        buf = b""  # unknown frame: drop
    finally:
        try:
            os.kill(pid, signal.SIGHUP)
        except OSError:
            pass
        os.waitpid(pid, 0)
        conn.close()


def main():
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", PORT))
    s.listen(1)
    print(f"ptyserver on 127.0.0.1:{PORT}: {' '.join(CMD)}", flush=True)
    while True:
        conn, _ = s.accept()
        serve(conn)


if __name__ == "__main__":
    main()
