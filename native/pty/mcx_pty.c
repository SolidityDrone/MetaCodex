// mcx_pty: pseudo-terminal + child process for MetaCodex's in-headset terminal (Android arm64, also
// builds on desktop Linux for tests). Called from C# through P/Invoke ([DllImport("mcxpty")]).
//
// Strings cross the boundary as "blobs": UTF-8 strings each followed by a NUL, plus a count, so no
// marshalling of string arrays is needed. Every call returns >= 0 on success or -errno.
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

#define EXPORT __attribute__((visibility("default")))

#ifndef __NR_close_range
#define __NR_close_range 436
#endif

static char** split_blob(const char* blob, int len, int count, char** copy_out) {
    if (count < 0 || len < 0) return NULL;
    char* copy = malloc((size_t)len + 1);
    char** arr = calloc((size_t)count + 1, sizeof(char*));
    if (!copy || !arr) { free(copy); free(arr); return NULL; }
    if (len > 0) memcpy(copy, blob, (size_t)len);
    copy[len] = 0;
    char* p = copy;
    char* end = copy + len;
    for (int i = 0; i < count; i++) {
        if (p >= end) { free(copy); free(arr); return NULL; }
        arr[i] = p;
        p += strlen(p) + 1;
    }
    arr[count] = NULL;
    *copy_out = copy;
    return arr;
}

// Async-signal-safe error line for the child (no stdio after fork).
static void child_fail(const char* what, int err) {
    char buf[96];
    int n = 0;
    const char* prefix = "\r\n[metacodex] ";
    for (const char* s = prefix; *s && n < 40; s++) buf[n++] = *s;
    for (const char* s = what; *s && n < 70; s++) buf[n++] = *s;
    const char* mid = " failed, errno ";
    for (const char* s = mid; *s && n < 86; s++) buf[n++] = *s;
    char digits[8];
    int d = 0;
    unsigned v = (unsigned)err;
    do { digits[d++] = (char)('0' + v % 10); v /= 10; } while (v && d < 7);
    while (d && n < 94) buf[n++] = digits[--d];
    buf[n++] = '\r';
    buf[n++] = '\n';
    (void)!write(STDERR_FILENO, buf, (size_t)n);
    _exit(127);
}

// Spawns path with argv/env in a new session whose controlling terminal is a new pty.
// Returns the master fd (>= 0) and writes the child's pid, or -errno.
EXPORT int mcx_pty_spawn(const char* path,
                          const char* argv_blob, int argv_len, int argc,
                          const char* env_blob, int env_len, int envc,
                          const char* cwd, int rows, int cols, int* out_pid) {
    if (!path || !out_pid || argc < 1) return -EINVAL;
    char *argv_copy = NULL, *env_copy = NULL;
    char** argv = split_blob(argv_blob, argv_len, argc, &argv_copy);
    char** envp = split_blob(env_blob, env_len, envc, &env_copy);
    if (!argv || !envp) { free(argv); free(argv_copy); free(envp); free(env_copy); return -EINVAL; }

    int err = 0;
    int ptm = open("/dev/ptmx", O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (ptm < 0) { err = -errno; goto out; }
    char devname[64];
    if (grantpt(ptm) || unlockpt(ptm) || ptsname_r(ptm, devname, sizeof devname)) {
        err = -errno; close(ptm); goto out;
    }

    // UTF-8 aware line discipline, no XON/XOFF flow control (Ctrl+S/Q reach the program).
    struct termios tios;
    if (tcgetattr(ptm, &tios) == 0) {
        tios.c_iflag |= IUTF8;
        tios.c_iflag &= (tcflag_t)~(IXON | IXOFF);
        tcsetattr(ptm, TCSANOW, &tios);
    }
    struct winsize ws = { .ws_row = (unsigned short)rows, .ws_col = (unsigned short)cols };
    ioctl(ptm, TIOCSWINSZ, &ws);

    struct rlimit rl;
    int maxfd = 4096;
    if (getrlimit(RLIMIT_NOFILE, &rl) == 0 && rl.rlim_cur != RLIM_INFINITY && rl.rlim_cur < 65536)
        maxfd = (int)rl.rlim_cur;
    else if (getrlimit(RLIMIT_NOFILE, &rl) == 0)
        maxfd = 65536;

    pid_t pid = fork();
    if (pid < 0) { err = -errno; close(ptm); goto out; }
    if (pid == 0) {
        // Child: only async-signal-safe calls until execve.
        sigset_t all;
        sigfillset(&all);
        sigprocmask(SIG_UNBLOCK, &all, NULL);
        struct sigaction dfl;
        memset(&dfl, 0, sizeof dfl);
        dfl.sa_handler = SIG_DFL;
        for (int s = 1; s < 65; s++) sigaction(s, &dfl, NULL); // the host app ignores SIGPIPE etc.

        if (setsid() < 0) child_fail("setsid", errno);
        int pts = open(devname, O_RDWR); // a session leader opening a tty acquires it
        if (pts < 0) child_fail("open pts", errno);
        ioctl(pts, TIOCSCTTY, 0);
        dup2(pts, STDIN_FILENO);
        dup2(pts, STDOUT_FILENO);
        dup2(pts, STDERR_FILENO);
        if (syscall(__NR_close_range, 3u, ~0u, 0u) != 0)
            for (int fd = 3; fd < maxfd; fd++) close(fd);
        if (cwd && *cwd && chdir(cwd) != 0) child_fail("chdir", errno);
        execve(path, argv, envp);
        child_fail("execve", errno);
    }
    *out_pid = (int)pid;
    err = ptm;
out:
    free(argv); free(argv_copy); free(envp); free(env_copy);
    return err;
}

// Waits up to timeout_ms for output. > 0 bytes read, 0 timeout, < 0 -errno (-EIO: the child side closed).
EXPORT int mcx_pty_read(int fd, uint8_t* buf, int len, int timeout_ms) {
    struct pollfd p = { .fd = fd, .events = POLLIN };
    for (;;) {
        int r = poll(&p, 1, timeout_ms);
        if (r < 0 && errno == EINTR) continue;
        if (r < 0) return -errno;
        if (r == 0) return 0;
        if (p.revents & (POLLIN | POLLHUP | POLLERR)) break;
    }
    for (;;) {
        ssize_t n = read(fd, buf, (size_t)len);
        if (n < 0 && errno == EINTR) continue;
        if (n < 0) return -errno;
        if (n == 0) return -EIO;
        return (int)n;
    }
}

// Writes all bytes (blocking). Returns len or -errno.
EXPORT int mcx_pty_write(int fd, const uint8_t* buf, int len) {
    int done = 0;
    while (done < len) {
        ssize_t n = write(fd, buf + done, (size_t)(len - done));
        if (n < 0 && errno == EINTR) continue;
        if (n < 0) return -errno;
        done += (int)n;
    }
    return done;
}

EXPORT int mcx_pty_resize(int fd, int rows, int cols) {
    struct winsize ws = { .ws_row = (unsigned short)rows, .ws_col = (unsigned short)cols };
    return ioctl(fd, TIOCSWINSZ, &ws) == 0 ? 0 : -errno;
}

// -1 still running (nohang), -2 no such child, else the exit code (128 + signal when killed).
EXPORT int mcx_pty_wait(int pid, int nohang) {
    int status = 0;
    for (;;) {
        pid_t r = waitpid((pid_t)pid, &status, nohang ? WNOHANG : 0);
        if (r < 0 && errno == EINTR) continue;
        if (r < 0) return -2;
        if (r == 0) return -1;
        if (WIFEXITED(status)) return WEXITSTATUS(status);
        if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
        return 255;
    }
}

// Signals the child's whole process group (it is a session leader, so pgid == pid).
EXPORT int mcx_pty_kill(int pid, int sig) {
    if (kill(-(pid_t)pid, sig) == 0) return 0;
    return kill((pid_t)pid, sig) == 0 ? 0 : -errno;
}

EXPORT int mcx_pty_close(int fd) { return close(fd) == 0 ? 0 : -errno; }

// File helpers for the rootfs installer (Android has no managed API for these).
EXPORT int mcx_symlink(const char* target, const char* linkpath) {
    return symlink(target, linkpath) == 0 ? 0 : -errno;
}

EXPORT int mcx_chmod(const char* path, int mode) {
    return chmod(path, (mode_t)mode) == 0 ? 0 : -errno;
}

// 0 missing, 1 file, 2 dir, 3 symlink, 4 other (lstat: links are not followed).
EXPORT int mcx_lstat_type(const char* path) {
    struct stat st;
    if (lstat(path, &st) != 0) return errno == ENOENT ? 0 : -errno;
    if (S_ISLNK(st.st_mode)) return 3;
    if (S_ISDIR(st.st_mode)) return 2;
    if (S_ISREG(st.st_mode)) return 1;
    return 4;
}

// The process' uid, gid and supplementary groups, for /etc/passwd and /etc/group in the rootfs.
EXPORT int mcx_ids(int* uid, int* gid, int* groups, int max_groups) {
    *uid = (int)getuid();
    *gid = (int)getgid();
    gid_t g[64];
    int n = getgroups(64, g);
    if (n < 0) return -errno;
    if (n > max_groups) n = max_groups;
    for (int i = 0; i < n; i++) groups[i] = (int)g[i];
    return n;
}

EXPORT int mcx_errno_text(int err, char* buf, int len) {
    if (len <= 0) return -EINVAL;
    const char* s = strerror(err < 0 ? -err : err);
    strncpy(buf, s ? s : "?", (size_t)len - 1);
    buf[len - 1] = 0;
    return 0;
}
