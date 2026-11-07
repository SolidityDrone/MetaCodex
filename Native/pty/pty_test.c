// pty_test: exercises libmcxpty the way the app does, without Unity.
// Usage: pty_test <rows> <cols> <seconds> <input|-> <program> [args...]
//   input: bytes typed into the terminal after 0.3 s ("\n" sends Enter as \r, "\x03" Ctrl+C); "-" types nothing
// Prints the child's output (escape bytes shown as \e) and its exit code. Environment is passed through.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

int mcx_pty_spawn(const char*, const char*, int, int, const char*, int, int, const char*, int, int, int*);
int mcx_pty_read(int, uint8_t*, int, int);
int mcx_pty_write(int, const uint8_t*, int);
int mcx_pty_wait(int, int);
int mcx_pty_kill(int, int);
int mcx_pty_close(int);

extern char** environ;

static int blob(char** items, int n, char** out) {
    int len = 0;
    for (int i = 0; i < n; i++) len += (int)strlen(items[i]) + 1;
    char* b = malloc((size_t)len + 1);
    int o = 0;
    for (int i = 0; i < n; i++) { size_t l = strlen(items[i]) + 1; memcpy(b + o, items[i], l); o += (int)l; }
    *out = b;
    return len;
}

static double now(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}

static int unescape(const char* s, uint8_t* out) {
    int n = 0;
    for (; *s; s++) {
        if (*s == '\\' && s[1] == 'n') { out[n++] = '\r'; s++; }
        else if (*s == '\\' && s[1] == 'x' && s[2] && s[3]) { char h[3] = { s[2], s[3], 0 }; out[n++] = (uint8_t)strtol(h, NULL, 16); s += 3; }
        else out[n++] = (uint8_t)*s;
    }
    return n;
}

int main(int argc, char** argv) {
    if (argc < 6) { fprintf(stderr, "usage: %s rows cols seconds input|- program [args...]\n", argv[0]); return 2; }
    int rows = atoi(argv[1]), cols = atoi(argv[2]);
    double seconds = atof(argv[3]);
    const char* input = argv[4];
    char* ab; int al = blob(argv + 5, argc - 5, &ab);
    int envc = 0; while (environ[envc]) envc++;
    char* eb; int el = blob(environ, envc, &eb);
    int pid = 0;
    double t0 = now();
    int fd = mcx_pty_spawn(argv[5], ab, al, argc - 5, eb, el, envc, NULL, rows, cols, &pid);
    if (fd < 0) { fprintf(stderr, "spawn failed: %d\n", fd); return 1; }
    fprintf(stderr, "[pty_test] pid %d fd %d spawn %.1f ms\n", pid, fd, (now() - t0) * 1000);
    int typed = strcmp(input, "-") == 0;
    uint8_t buf[4096];
    long total = 0;
    int code = -1;
    while (now() - t0 < seconds) {
        if (!typed && now() - t0 > 0.3) {
            uint8_t in[1024]; int n = unescape(input, in);
            mcx_pty_write(fd, in, n);
            typed = 1;
        }
        int n = mcx_pty_read(fd, buf, sizeof buf, 100);
        if (n > 0) {
            total += n;
            for (int i = 0; i < n; i++) {
                if (buf[i] == 0x1b) fputs("\\e", stdout);
                else if (buf[i] == '\r') continue;
                else putchar(buf[i]);
            }
            fflush(stdout);
        } else if (n < 0) {
            break; // -EIO: the child closed the terminal
        }
        code = mcx_pty_wait(pid, 1);
        if (code >= 0) break;
    }
    if (code < 0) code = mcx_pty_wait(pid, 1);
    if (code == -1) { mcx_pty_kill(pid, 9); code = mcx_pty_wait(pid, 0); fprintf(stderr, "[pty_test] timeout, killed\n"); }
    mcx_pty_close(fd);
    fprintf(stderr, "\n[pty_test] exit %d, %ld bytes, %.2f s\n", code, total, now() - t0);
    return 0;
}
