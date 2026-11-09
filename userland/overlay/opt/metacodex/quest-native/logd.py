"""Log listener for apps built with mcx-app: python3 -I logd.py <port> <dir> <idle seconds>

Template apps (main.cpp, Log) connect to 127.0.0.1:<port> when they start and send their log lines (logcat cannot be
read from MetaCodex's Linux). The first line names the app ("== <package> connected"); its lines are appended to
<dir>/<package>.log with the time. Exits after <idle seconds> without a new connection. Started by `mcx-app`.
"""
import os, socket, sys, threading, time

port, logdir, idle = int(sys.argv[1]), sys.argv[2], float(sys.argv[3])
os.makedirs(logdir, exist_ok=True)
srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
try:
    srv.bind(("127.0.0.1", port))
except OSError:
    sys.exit(0)  # another listener has the port
srv.listen(16)
srv.settimeout(5)
last = time.time()


def safe(name):
    name = "".join(ch for ch in name if ch.isalnum() or ch in "._-")
    return name or "unknown"


def serve(conn):
    out = None
    try:
        with conn, conn.makefile("r", encoding="utf-8", errors="replace") as f:
            for line in f:
                if out is None:
                    name = line[3:].split()[0] if line.startswith("== ") and len(line.split()) > 1 else "unknown"
                    out = open(os.path.join(logdir, safe(name) + ".log"), "a", buffering=1, encoding="utf-8")
                    out.write("---- started %s\n" % time.strftime("%Y-%m-%d %H:%M:%S"))
                    if line.startswith("== "):
                        continue
                out.write(time.strftime("%H:%M:%S ") + line if line.endswith("\n") else line + "\n")
    except OSError:
        pass
    finally:
        if out is not None:
            out.write("---- ended %s\n" % time.strftime("%H:%M:%S"))
            out.close()


while time.time() - last < idle:
    try:
        conn, _ = srv.accept()
    except socket.timeout:
        continue
    last = time.time()
    threading.Thread(target=serve, args=(conn,), daemon=True).start()
