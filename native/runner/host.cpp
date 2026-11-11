// MetaCodex native host (libmcxhost.so): runs an app module (libapp.so, see mcx_module.h) on Horizon OS.
// One NativeActivity with EGL (OpenGL ES 3.2) and OpenXR; the module draws. Two modes, same code:
//   * shipped app: libapp.so sits next to this library in the APK; it is loaded once at start.
//   * Runner (no libapp.so in the APK): modules arrive from MetaCodex's Linux (`mcx-app dev`, 127.0.0.1:47951) while
//     the app runs and replace each other without a new OpenXR session. Every module is checked before it is loaded:
//     the sender signs "MCXR1|<nonce>|<sha256>" with the key that signs the user's APKs (RSA PKCS#1 v1.5, SHA-256);
//     the Runner picks the nonce and holds the public key in assets/mcx-dev.pub. Another app taking the port cannot
//     make the Runner load anything (port squatting), and an old module cannot be replayed.
// Build: mcx-build (TYPE=module or TYPE=runner in build.conf) compiles this file once per toolchain and caches it.
#include "mcx_module.h"
#include <android/log.h>
#include <android_native_app_glue.h>
#define XR_USE_PLATFORM_ANDROID
#define XR_USE_GRAPHICS_API_OPENGL_ES
#include <openxr/openxr_platform.h>
#include <arpa/inet.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <unistd.h>
#include <atomic>
#include <cerrno>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

// the package name (read from the activity at start): one host binary serves every app
char g_appId[160] = "mcxhost";
#define APP_ID g_appId

// ---------------------------------------------------------------- log: logcat + `mcx-app logs` (127.0.0.1:47950)
std::mutex g_logMutex;
int g_logFd = -2;
void Log(const char* fmt, ...)
{
    char line[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(line, sizeof line - 1, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > (int)sizeof line - 2) n = (int)sizeof line - 2;
    __android_log_print(ANDROID_LOG_INFO, APP_ID, "%s", line);
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (g_logFd == -2)
    {
        g_logFd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
        sockaddr_in a{};
        a.sin_family = AF_INET;
        a.sin_port = htons(47950);
        a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (g_logFd >= 0 && connect(g_logFd, (sockaddr*)&a, sizeof a) != 0) { close(g_logFd); g_logFd = -1; }
        if (g_logFd >= 0)
        {
            char hello[200];
            int h = snprintf(hello, sizeof hello, "== %s connected\n", APP_ID);
            send(g_logFd, hello, h, MSG_NOSIGNAL | MSG_DONTWAIT);
        }
    }
    if (g_logFd >= 0)
    {
        line[n] = '\n';
        if (send(g_logFd, line, n + 1, MSG_NOSIGNAL | MSG_DONTWAIT) < 0 && errno != EAGAIN && errno != EWOULDBLOCK)
        {
            close(g_logFd);
            g_logFd = -1;
        }
    }
}
#define LOG(...) Log(__VA_ARGS__)

double Now()
{
    timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec * 1e-9;
}

// ---------------------------------------------------------------- SHA-256
struct Sha256
{
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    uint8_t buf[64];
    uint64_t len = 0;
    size_t fill = 0;
    static uint32_t R(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
    void Block(const uint8_t* p)
    {
        static const uint32_t k[64] = {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01,
            0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
            0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
            0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08,
            0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
            0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
        uint32_t w[64];
        for (int i = 0; i < 16; i++) w[i] = (uint32_t)p[4 * i] << 24 | (uint32_t)p[4 * i + 1] << 16 | (uint32_t)p[4 * i + 2] << 8 | p[4 * i + 3];
        for (int i = 16; i < 64; i++)
        {
            uint32_t s0 = R(w[i - 15], 7) ^ R(w[i - 15], 18) ^ (w[i - 15] >> 3), s1 = R(w[i - 2], 17) ^ R(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; i++)
        {
            uint32_t t1 = hh + (R(e, 6) ^ R(e, 11) ^ R(e, 25)) + ((e & f) ^ (~e & g)) + k[i] + w[i];
            uint32_t t2 = (R(a, 2) ^ R(a, 13) ^ R(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
            hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }
    void Update(const void* data, size_t n)
    {
        const uint8_t* p = (const uint8_t*)data;
        len += n;
        while (n)
        {
            size_t take = 64 - fill < n ? 64 - fill : n;
            memcpy(buf + fill, p, take);
            fill += take; p += take; n -= take;
            if (fill == 64) { Block(buf); fill = 0; }
        }
    }
    void Final(uint8_t out[32])
    {
        uint64_t bits = len * 8;
        uint8_t pad = 0x80;
        Update(&pad, 1);
        uint8_t z = 0;
        while (fill != 56) Update(&z, 1);
        uint8_t l[8];
        for (int i = 0; i < 8; i++) l[i] = (uint8_t)(bits >> (56 - 8 * i));
        Update(l, 8);
        for (int i = 0; i < 8; i++) { out[4 * i] = h[i] >> 24; out[4 * i + 1] = h[i] >> 16; out[4 * i + 2] = h[i] >> 8; out[4 * i + 3] = h[i]; }
    }
};

std::string Hex(const uint8_t* p, size_t n)
{
    static const char* d = "0123456789abcdef";
    std::string s;
    for (size_t i = 0; i < n; i++) { s += d[p[i] >> 4]; s += d[p[i] & 15]; }
    return s;
}

// ---------------------------------------------------------------- RSA PKCS#1 v1.5 SHA-256 verification (e = 65537)
// Montgomery arithmetic on 64-bit limbs, little-endian; n, n0 = -n^-1 mod 2^64 and r2 = R^2 mod n come from the asset.
struct RsaKey
{
    std::vector<uint64_t> n, r2;
    uint64_t n0 = 0;
    size_t bytes = 0;
    bool ok = false;
};

std::vector<uint64_t> FromBigEndian(const uint8_t* p, size_t len, size_t limbs)
{
    std::vector<uint64_t> v(limbs, 0);
    for (size_t i = 0; i < len; i++)
    {
        size_t bit = (len - 1 - i) * 8;
        if (bit / 64 < limbs) v[bit / 64] |= (uint64_t)p[i] << (bit % 64);
    }
    return v;
}

bool ParseHex(const char* s, std::vector<uint8_t>& out)
{
    out.clear();
    size_t n = strlen(s);
    if (n % 2) return false;
    for (size_t i = 0; i < n; i += 2)
    {
        unsigned v;
        if (sscanf(s + i, "%2x", &v) != 1) return false;
        out.push_back((uint8_t)v);
    }
    return true;
}

void MontMul(const RsaKey& k, const uint64_t* a, const uint64_t* b, uint64_t* out)
{
    size_t s = k.n.size();
    std::vector<uint64_t> t(s + 2, 0);
    for (size_t i = 0; i < s; i++)
    {
        unsigned __int128 c = 0;
        for (size_t j = 0; j < s; j++)
        {
            c += (unsigned __int128)a[j] * b[i] + t[j];
            t[j] = (uint64_t)c;
            c >>= 64;
        }
        c += t[s];
        t[s] = (uint64_t)c;
        t[s + 1] = (uint64_t)(c >> 64);
        uint64_t m = t[0] * k.n0;
        c = (unsigned __int128)m * k.n[0] + t[0];
        c >>= 64;
        for (size_t j = 1; j < s; j++)
        {
            c += (unsigned __int128)m * k.n[j] + t[j];
            t[j - 1] = (uint64_t)c;
            c >>= 64;
        }
        c += t[s];
        t[s - 1] = (uint64_t)c;
        c >>= 64;
        t[s] = t[s + 1] + (uint64_t)c;
    }
    // t < 2n: subtract n once if t >= n
    bool ge = t[s] != 0;
    if (!ge)
    {
        ge = true;
        for (size_t j = s; j-- > 0;)
            if (t[j] != k.n[j]) { ge = t[j] > k.n[j]; break; }
    }
    if (ge)
    {
        unsigned __int128 borrow = 0;
        for (size_t j = 0; j < s; j++)
        {
            unsigned __int128 d = (unsigned __int128)t[j] - k.n[j] - borrow;
            t[j] = (uint64_t)d;
            borrow = (d >> 64) ? 1 : 0;
        }
    }
    memcpy(out, t.data(), s * sizeof(uint64_t));
}

bool RsaVerify(const RsaKey& k, const uint8_t* msg, size_t msgLen, const uint8_t* sig, size_t sigLen)
{
    if (!k.ok || sigLen != k.bytes) return false;
    size_t s = k.n.size();
    std::vector<uint64_t> x = FromBigEndian(sig, sigLen, s);
    for (size_t j = s; j-- > 0;) // the signature must be < n
    {
        if (x[j] < k.n[j]) break;
        if (x[j] > k.n[j] || j == 0) return false;
    }
    std::vector<uint64_t> xm(s), y(s), one(s, 0);
    one[0] = 1;
    MontMul(k, x.data(), k.r2.data(), xm.data()); // x * R
    y = xm;
    for (int i = 0; i < 16; i++) MontMul(k, y.data(), y.data(), y.data());
    MontMul(k, y.data(), xm.data(), y.data());     // x^65537 * R
    MontMul(k, y.data(), one.data(), y.data());    // x^65537
    // expected: 00 01 FF..FF 00 DigestInfo(SHA-256) hash
    uint8_t h[32];
    Sha256 sh;
    sh.Update(msg, msgLen);
    sh.Final(h);
    static const uint8_t info[19] = {0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20};
    std::vector<uint8_t> em(k.bytes, 0xff);
    em[0] = 0;
    em[1] = 1;
    size_t tail = 19 + 32;
    em[k.bytes - tail - 1] = 0;
    memcpy(&em[k.bytes - tail], info, 19);
    memcpy(&em[k.bytes - 32], h, 32);
    std::vector<uint64_t> want = FromBigEndian(em.data(), em.size(), s);
    uint64_t diff = 0;
    for (size_t j = 0; j < s; j++) diff |= want[j] ^ y[j];
    return diff == 0;
}

RsaKey LoadKey(AAssetManager* am)
{
    RsaKey k;
    AAsset* a = am ? AAssetManager_open(am, "mcx-dev.pub", AASSET_MODE_BUFFER) : nullptr;
    if (!a) return k;
    std::string text((const char*)AAsset_getBuffer(a), (size_t)AAsset_getLength(a));
    AAsset_close(a);
    std::vector<uint8_t> n, r2, n0;
    size_t pos = 0;
    while (pos < text.size())
    {
        size_t e = text.find('\n', pos);
        if (e == std::string::npos) e = text.size();
        std::string line = text.substr(pos, e - pos);
        pos = e + 1;
        if (line.rfind("n=", 0) == 0) ParseHex(line.c_str() + 2, n);
        else if (line.rfind("r2=", 0) == 0) ParseHex(line.c_str() + 3, r2);
        else if (line.rfind("n0=", 0) == 0) ParseHex(line.c_str() + 3, n0);
    }
    if (n.empty() || r2.empty() || n0.size() != 8) return k;
    size_t limbs = (n.size() + 7) / 8;
    k.n = FromBigEndian(n.data(), n.size(), limbs);
    k.r2 = FromBigEndian(r2.data(), r2.size(), limbs);
    for (int i = 0; i < 8; i++) k.n0 = k.n0 << 8 | n0[i];
    k.bytes = n.size();
    k.ok = true;
    return k;
}

// ---------------------------------------------------------------- matrices (column-major)
void Projection(const XrFovf& f, float n, float fr, float* p)
{
    float l = tanf(f.angleLeft), r = tanf(f.angleRight), d = tanf(f.angleDown), u = tanf(f.angleUp);
    float w = r - l, h = u - d;
    memset(p, 0, 16 * sizeof(float));
    p[0] = 2.f / w; p[5] = 2.f / h; p[8] = (r + l) / w; p[9] = (u + d) / h;
    p[10] = -(fr + n) / (fr - n); p[11] = -1.f; p[14] = -2.f * fr * n / (fr - n);
}

void ViewMatrix(const XrPosef& pose, float* v)
{
    float x = pose.orientation.x, y = pose.orientation.y, z = pose.orientation.z, w = pose.orientation.w;
    float r[16] = {1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w), 0, 2 * (x * y - z * w), 1 - 2 * (x * x + z * z),
                   2 * (y * z + x * w), 0, 2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y), 0, 0, 0, 0, 1};
    memset(v, 0, 16 * sizeof(float));
    for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++) v[j * 4 + i] = r[i * 4 + j];
    const XrVector3f& t = pose.position;
    for (int i = 0; i < 3; i++) v[12 + i] = -(v[i] * t.x + v[4 + i] * t.y + v[8 + i] * t.z);
    v[15] = 1.f;
}

// ---------------------------------------------------------------- the host
struct Eye
{
    XrSwapchain swapchain = XR_NULL_HANDLE;
    int32_t width = 0, height = 0;
    std::vector<XrSwapchainImageOpenGLESKHR> images;
    GLuint depth = 0;
};

struct Module
{
    void* handle = nullptr;
    McxInitFn init = nullptr;
    McxFrameFn frame = nullptr;
    McxDrawFn draw = nullptr;
    McxEventFn event = nullptr;
    McxShutdownFn shutdown = nullptr;
    std::string path, sha;
    double started = 0;
};

// defaults enabled when the runtime has them: the same list in the Runner and in shipped apps
const char* kDefaultExtensions[] = {
    "XR_EXT_hand_tracking", "XR_FB_hand_tracking_aim", "XR_FB_hand_tracking_mesh", "XR_FB_hand_tracking_capsules",
    "XR_FB_passthrough", "XR_FB_triangle_mesh", "XR_FB_display_refresh_rate", "XR_FB_color_space",
    "XR_FB_composition_layer_alpha_blend", "XR_FB_composition_layer_settings", "XR_KHR_composition_layer_depth",
    "XR_FB_spatial_entity", "XR_FB_spatial_entity_query", "XR_FB_spatial_entity_storage", "XR_FB_spatial_entity_container",
    "XR_FB_scene", "XR_META_environment_depth", "XR_FB_foveation", "XR_FB_foveation_configuration",
    "XR_FB_swapchain_update_state", "XR_FB_render_model", "XR_META_touch_controller_plus", "XR_FB_touch_controller_pro",
    "XR_EXT_local_floor", "XR_META_simultaneous_hands_and_controllers", "XR_EXT_performance_settings",
    "XR_META_performance_metrics", "XR_FB_body_tracking", "XR_FB_face_tracking2", "XR_FB_eye_tracking_social",
    "XR_EXT_eye_gaze_interaction"};

struct Host
{
    android_app* android = nullptr;
    McxHost api{};
    bool resumed = false, running = false, exit = false, ok = false;
    EGLDisplay display = EGL_NO_DISPLAY;
    EGLContext context = EGL_NO_CONTEXT;
    EGLSurface surface = EGL_NO_SURFACE;
    XrSessionState state = XR_SESSION_STATE_UNKNOWN;
    Eye eyes[2];
    GLuint fbo = 0;
    std::vector<std::string> enabled;
    Module mod;
    bool dev = false;
    RsaKey key;
    // dev channel: a verified module written to disk, waiting for the main thread
    std::mutex pendingMutex;
    std::string pendingPath, pendingSha;
    std::string lastSha; // the module last tried (loaded or refused), under pendingMutex: not sent again
    std::atomic<bool> poll{false}, quit{false};
    std::thread channel;
    double lastFrame = 0;
    int seq = 0;
};

Host* g_host = nullptr;

int HasExtension(const char* name)
{
    if (!g_host || !name) return 0;
    for (const std::string& e : g_host->enabled)
        if (e == name) return 1;
    return 0;
}

template <class T> T Sym(void* h, const char* name) { return (T)dlsym(h, name); }

bool LoadModule(Host& h, const std::string& path, const std::string& sha)
{
    double t0 = Now();
    void* handle = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!handle) { LOG("module: dlopen %s failed: %s", path.c_str(), dlerror()); return false; }
    McxAbiFn abi = Sym<McxAbiFn>(handle, "mcx_app_abi");
    McxDrawFn draw = Sym<McxDrawFn>(handle, "mcx_app_draw");
    if (!draw || (abi && abi() != MCX_MODULE_ABI))
    {
        LOG("module: %s has %s", path.c_str(), !draw ? "no mcx_app_draw" : "another MCX_MODULE_ABI");
        dlclose(handle);
        return false;
    }
    // the old module goes first (its GL objects, its threads)
    if (h.mod.handle)
    {
        if (h.mod.shutdown) h.mod.shutdown();
        dlclose(h.mod.handle);
        if (h.dev && !h.mod.path.empty()) unlink(h.mod.path.c_str());
    }
    Module m;
    m.handle = handle;
    m.init = Sym<McxInitFn>(handle, "mcx_app_init");
    m.frame = Sym<McxFrameFn>(handle, "mcx_app_frame");
    m.draw = draw;
    m.event = Sym<McxEventFn>(handle, "mcx_app_event");
    m.shutdown = Sym<McxShutdownFn>(handle, "mcx_app_shutdown");
    m.path = path;
    m.sha = sha;
    m.started = Now();
    int rc = m.init ? m.init(&h.api) : 0;
    h.mod = m;
    if (rc != 0) LOG("module: mcx_app_init returned %d", rc);
    LOG("module loaded: %s (%s) in %.1f ms", path.c_str(), sha.empty() ? "shipped" : sha.substr(0, 12).c_str(), (Now() - t0) * 1000.0);
    return true;
}

// ---- the dev channel (Runner only): poll 127.0.0.1:47951 while the app is resumed
bool ReadAll(int fd, void* p, size_t n, int timeoutMs)
{
    uint8_t* b = (uint8_t*)p;
    while (n)
    {
        pollfd pf{fd, POLLIN, 0};
        if (poll(&pf, 1, timeoutMs) <= 0) return false;
        ssize_t r = recv(fd, b, n, 0);
        if (r <= 0) return false;
        b += r;
        n -= (size_t)r;
    }
    return true;
}

bool ReadLine(int fd, std::string& line, int timeoutMs)
{
    line.clear();
    char c;
    while (line.size() < 1024)
    {
        if (!ReadAll(fd, &c, 1, timeoutMs)) return false;
        if (c == '\n') return true;
        line += c;
    }
    return false;
}

void ChannelOnce(Host& h)
{
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    if (fd < 0) return;
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_port = htons(47951);
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    int r = connect(fd, (sockaddr*)&a, sizeof a);
    if (r != 0 && errno == EINPROGRESS)
    {
        pollfd pf{fd, POLLOUT, 0};
        int err = 0;
        socklen_t el = sizeof err;
        if (poll(&pf, 1, 300) == 1 && getsockopt(fd, SOL_SOCKET, SO_ERROR, &err, &el) == 0 && err == 0) r = 0;
    }
    if (r != 0) { close(fd); return; }
    fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) & ~O_NONBLOCK);
    uint8_t nonce[16];
    int rnd = open("/dev/urandom", O_RDONLY | O_CLOEXEC);
    if (rnd < 0 || read(rnd, nonce, sizeof nonce) != (ssize_t)sizeof nonce) { if (rnd >= 0) close(rnd); close(fd); return; }
    close(rnd);
    std::string nh = Hex(nonce, sizeof nonce);
    std::string cur;
    {
        std::lock_guard<std::mutex> lock(h.pendingMutex);
        cur = h.pendingSha.empty() ? h.lastSha : h.pendingSha;
    }
    std::string hello = "MCXR1 " + nh + " " + (cur.empty() ? "-" : cur) + " " + std::string(APP_ID) + "\n";
    send(fd, hello.data(), hello.size(), MSG_NOSIGNAL);
    std::string line;
    if (!ReadLine(fd, line, 3000) || line.rfind("MOD ", 0) != 0) { close(fd); return; }
    // MOD <bytes> <sha256 hex> <signature hex>
    char shaHex[80] = {0};
    static char sigHex[2100];
    unsigned long size = 0;
    if (sscanf(line.c_str(), "MOD %lu %64s %2048s", &size, shaHex, sigHex) != 3 || size == 0 || size > (64ul << 20))
    {
        LOG("dev channel: bad header");
        close(fd);
        return;
    }
    std::vector<uint8_t> body(size);
    if (!ReadAll(fd, body.data(), size, 10000)) { LOG("dev channel: module cut short"); close(fd); return; }
    close(fd);
    uint8_t digest[32];
    Sha256 sh;
    sh.Update(body.data(), body.size());
    sh.Final(digest);
    std::string sha = Hex(digest, 32);
    std::vector<uint8_t> sig;
    std::string signedText = "MCXR1|" + nh + "|" + sha;
    if (sha != shaHex || !ParseHex(sigHex, sig) || !RsaVerify(h.key, (const uint8_t*)signedText.data(), signedText.size(), sig.data(), sig.size()))
    {
        LOG("dev channel: REFUSED a module that is not signed with this headset's key (%s)", h.key.ok ? "bad signature" : "no key in assets");
        return;
    }
    std::string dir = std::string(h.api.dataPath) + "/modules";
    mkdir(dir.c_str(), 0700);
    std::string path = dir + "/mod-" + std::to_string(++h.seq) + "-" + sha.substr(0, 12) + ".so";
    int out = open(path.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0700);
    if (out < 0) { LOG("dev channel: cannot write %s: %s", path.c_str(), strerror(errno)); return; }
    bool written = write(out, body.data(), body.size()) == (ssize_t)body.size();
    close(out);
    if (!written) { unlink(path.c_str()); return; }
    std::lock_guard<std::mutex> lock(h.pendingMutex);
    if (!h.pendingPath.empty()) unlink(h.pendingPath.c_str());
    h.pendingPath = path;
    h.pendingSha = sha;
}

void ChannelLoop(Host* h)
{
    while (!h->quit.load())
    {
        if (h->poll.load()) ChannelOnce(*h);
        for (int i = 0; i < 4 && !h->quit.load(); i++) usleep(100 * 1000);
    }
}

// ---- EGL + OpenXR
#define XR(call) do { XrResult r_ = (call); if (XR_FAILED(r_)) { LOG("%s failed: %d", #call, (int)r_); return false; } } while (0)

bool InitEgl(Host& h)
{
    h.display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    eglInitialize(h.display, nullptr, nullptr);
    const EGLint cfg[] = {EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_RENDERABLE_TYPE, 0x40, EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE};
    EGLConfig config;
    EGLint n = 0;
    if (!eglChooseConfig(h.display, cfg, &config, 1, &n) || n == 0) { LOG("no EGL config"); return false; }
    const EGLint ctx32[] = {EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 2, EGL_NONE};
    h.context = eglCreateContext(h.display, config, EGL_NO_CONTEXT, ctx32);
    if (h.context == EGL_NO_CONTEXT)
    {
        const EGLint ctx3[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
        h.context = eglCreateContext(h.display, config, EGL_NO_CONTEXT, ctx3);
    }
    const EGLint pb[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE};
    h.surface = eglCreatePbufferSurface(h.display, config, pb);
    if (h.context == EGL_NO_CONTEXT || !eglMakeCurrent(h.display, h.surface, h.surface, h.context)) { LOG("EGL context failed"); return false; }
    return true;
}

bool InitXr(Host& h, const std::vector<std::string>& extra)
{
    PFN_xrInitializeLoaderKHR initLoader = nullptr;
    XR(xrGetInstanceProcAddr(XR_NULL_HANDLE, "xrInitializeLoaderKHR", (PFN_xrVoidFunction*)&initLoader));
    XrLoaderInitInfoAndroidKHR li{XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR};
    li.applicationVM = h.android->activity->vm;
    li.applicationContext = h.android->activity->clazz;
    XR(initLoader((const XrLoaderInitInfoBaseHeaderKHR*)&li));

    uint32_t ne = 0;
    XR(xrEnumerateInstanceExtensionProperties(nullptr, 0, &ne, nullptr));
    std::vector<XrExtensionProperties> props(ne, {XR_TYPE_EXTENSION_PROPERTIES});
    XR(xrEnumerateInstanceExtensionProperties(nullptr, ne, &ne, props.data()));
    auto available = [&](const std::string& name) {
        for (auto& p : props)
            if (name == p.extensionName) return true;
        return false;
    };
    std::vector<std::string> want = {XR_KHR_ANDROID_CREATE_INSTANCE_EXTENSION_NAME, XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME};
    for (const char* e : kDefaultExtensions) want.push_back(e);
    for (const std::string& e : extra) want.push_back(e);
    for (const std::string& e : want)
    {
        bool dup = false;
        for (const std::string& x : h.enabled) dup |= x == e;
        if (!dup && available(e)) h.enabled.push_back(e);
        else if (!dup && !available(e)) LOG("extension %s: not offered by this runtime", e.c_str());
    }
    std::vector<const char*> names;
    for (const std::string& e : h.enabled) names.push_back(e.c_str());

    XrInstanceCreateInfoAndroidKHR android{XR_TYPE_INSTANCE_CREATE_INFO_ANDROID_KHR};
    android.applicationVM = h.android->activity->vm;
    android.applicationActivity = h.android->activity->clazz;
    XrInstanceCreateInfo ci{XR_TYPE_INSTANCE_CREATE_INFO};
    ci.next = &android;
    snprintf(ci.applicationInfo.applicationName, sizeof ci.applicationInfo.applicationName, "%s", APP_ID);
    ci.applicationInfo.applicationVersion = 1;
    strcpy(ci.applicationInfo.engineName, "MetaCodex native host");
    ci.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ci.enabledExtensionCount = (uint32_t)names.size();
    ci.enabledExtensionNames = names.data();
    XR(xrCreateInstance(&ci, &h.api.instance));

    XrSystemGetInfo si{XR_TYPE_SYSTEM_GET_INFO};
    si.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XR(xrGetSystem(h.api.instance, &si, &h.api.system));
    PFN_xrGetOpenGLESGraphicsRequirementsKHR req = nullptr;
    XR(xrGetInstanceProcAddr(h.api.instance, "xrGetOpenGLESGraphicsRequirementsKHR", (PFN_xrVoidFunction*)&req));
    XrGraphicsRequirementsOpenGLESKHR gr{XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR};
    XR(req(h.api.instance, h.api.system, &gr));
    if (!InitEgl(h)) return false;
    XrGraphicsBindingOpenGLESAndroidKHR gb{XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR};
    gb.display = h.display;
    gb.config = (EGLConfig)0;
    gb.context = h.context;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &gb;
    sci.systemId = h.api.system;
    XR(xrCreateSession(h.api.instance, &sci, &h.api.session));
    XrReferenceSpaceCreateInfo rs{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rs.poseInReferenceSpace.orientation.w = 1.f;
    XR(xrCreateReferenceSpace(h.api.session, &rs, &h.api.localSpace));
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;
    if (XR_FAILED(xrCreateReferenceSpace(h.api.session, &rs, &h.api.stageSpace))) h.api.stageSpace = XR_NULL_HANDLE;

    uint32_t nv = 0;
    XR(xrEnumerateViewConfigurationViews(h.api.instance, h.api.system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &nv, nullptr));
    std::vector<XrViewConfigurationView> views(nv, {XR_TYPE_VIEW_CONFIGURATION_VIEW});
    XR(xrEnumerateViewConfigurationViews(h.api.instance, h.api.system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, nv, &nv, views.data()));
    uint32_t nf = 0;
    XR(xrEnumerateSwapchainFormats(h.api.session, 0, &nf, nullptr));
    std::vector<int64_t> formats(nf);
    XR(xrEnumerateSwapchainFormats(h.api.session, nf, &nf, formats.data()));
    int64_t format = formats.empty() ? GL_RGBA8 : formats[0];
    for (int64_t f : formats)
        if (f == GL_SRGB8_ALPHA8) format = f;
    for (uint32_t e = 0; e < 2 && e < nv; e++)
    {
        Eye& eye = h.eyes[e];
        eye.width = (int32_t)views[e].recommendedImageRectWidth;
        eye.height = (int32_t)views[e].recommendedImageRectHeight;
        XrSwapchainCreateInfo sc{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        sc.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        sc.format = format;
        sc.sampleCount = 1;
        sc.width = (uint32_t)eye.width;
        sc.height = (uint32_t)eye.height;
        sc.faceCount = 1;
        sc.arraySize = 1;
        sc.mipCount = 1;
        XR(xrCreateSwapchain(h.api.session, &sc, &eye.swapchain));
        uint32_t ni = 0;
        XR(xrEnumerateSwapchainImages(eye.swapchain, 0, &ni, nullptr));
        eye.images.assign(ni, {XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_ES_KHR});
        XR(xrEnumerateSwapchainImages(eye.swapchain, ni, &ni, (XrSwapchainImageBaseHeader*)eye.images.data()));
        glGenRenderbuffers(1, &eye.depth);
        glBindRenderbuffer(GL_RENDERBUFFER, eye.depth);
        glRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, eye.width, eye.height);
    }
    glGenFramebuffers(1, &h.fbo);
    h.api.refreshRate = 72.f;
    if (HasExtension("XR_FB_display_refresh_rate"))
    {
        PFN_xrGetDisplayRefreshRateFB get = nullptr;
        if (XR_SUCCEEDED(xrGetInstanceProcAddr(h.api.instance, "xrGetDisplayRefreshRateFB", (PFN_xrVoidFunction*)&get)) && get)
            get(h.api.session, &h.api.refreshRate);
    }
    h.api.eglDisplay = h.display;
    h.api.eglContext = h.context;
    LOG("OpenXR ready: %dx%d per eye, %zu extensions enabled, %.0f Hz", h.eyes[0].width, h.eyes[0].height, h.enabled.size(), h.api.refreshRate);
    return true;
}

void PollXr(Host& h)
{
    XrEventDataBuffer ev{XR_TYPE_EVENT_DATA_BUFFER};
    while (xrPollEvent(h.api.instance, &ev) == XR_SUCCESS)
    {
        if (h.mod.event) h.mod.event(&ev);
        if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED)
        {
            auto* sc = (XrEventDataSessionStateChanged*)&ev;
            h.state = sc->state;
            if (h.state == XR_SESSION_STATE_READY)
            {
                XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO};
                bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                h.running = XR_SUCCEEDED(xrBeginSession(h.api.session, &bi));
            }
            else if (h.state == XR_SESSION_STATE_STOPPING) { xrEndSession(h.api.session); h.running = false; }
            else if (h.state == XR_SESSION_STATE_EXITING || h.state == XR_SESSION_STATE_LOSS_PENDING) h.exit = true;
        }
        else if (ev.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) h.exit = true;
        ev = {XR_TYPE_EVENT_DATA_BUFFER};
    }
}

void RenderFrame(Host& h)
{
    XrFrameState fs{XR_TYPE_FRAME_STATE};
    if (XR_FAILED(xrWaitFrame(h.api.session, nullptr, &fs))) return;
    xrBeginFrame(h.api.session, nullptr);
    McxFrameOut out{};
    XrCompositionLayerProjectionView pv[2] = {{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}, {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}};
    XrCompositionLayerProjection layer{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    const XrCompositionLayerBaseHeader* layers[6];
    uint32_t count = 0;
    if (fs.shouldRender)
    {
        XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO};
        li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
        li.displayTime = fs.predictedDisplayTime;
        li.space = h.api.localSpace;
        XrViewState vs{XR_TYPE_VIEW_STATE};
        McxFrame f{};
        f.views[0] = {XR_TYPE_VIEW};
        f.views[1] = {XR_TYPE_VIEW};
        uint32_t nv = 0;
        if (XR_SUCCEEDED(xrLocateViews(h.api.session, &li, &vs, 2, &nv, f.views)) && nv == 2)
        {
            double now = Now();
            f.displayTime = fs.predictedDisplayTime;
            f.seconds = h.mod.handle ? now - h.mod.started : 0.0;
            f.dt = h.lastFrame > 0 ? (float)(now - h.lastFrame) : 0.f;
            h.lastFrame = now;
            f.state = h.state;
            if (h.mod.frame) h.mod.frame(&f, &out);
            for (int e = 0; e < 2; e++)
            {
                Eye& eye = h.eyes[e];
                uint32_t idx = 0;
                XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
                xrAcquireSwapchainImage(eye.swapchain, &ai, &idx);
                XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
                wi.timeout = XR_INFINITE_DURATION;
                xrWaitSwapchainImage(eye.swapchain, &wi);
                glBindFramebuffer(GL_FRAMEBUFFER, h.fbo);
                glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, eye.images[idx].image, 0);
                glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, eye.depth);
                glViewport(0, 0, eye.width, eye.height);
                if (h.mod.draw)
                {
                    McxEye me{};
                    me.index = e;
                    me.width = eye.width;
                    me.height = eye.height;
                    me.framebuffer = h.fbo;
                    me.view = &f.views[e];
                    Projection(f.views[e].fov, 0.05f, 100.f, me.projection);
                    ViewMatrix(f.views[e].pose, me.viewMatrix);
                    h.mod.draw(&me);
                }
                else
                {
                    // the Runner without a module yet: a slow dark-blue pulse ("waiting for mcx-app dev")
                    float p = 0.03f + 0.02f * sinf((float)Now() * 2.f);
                    glClearColor(0.01f, 0.015f, p, 1.f);
                    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
                }
                glBindFramebuffer(GL_FRAMEBUFFER, 0);
                XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
                xrReleaseSwapchainImage(eye.swapchain, &ri);
                pv[e].pose = f.views[e].pose;
                pv[e].fov = f.views[e].fov;
                pv[e].subImage.swapchain = eye.swapchain;
                pv[e].subImage.imageRect = {{0, 0}, {eye.width, eye.height}};
            }
            for (uint32_t i = 0; i < out.underlayCount && i < 4; i++)
                if (out.underlays[i]) layers[count++] = out.underlays[i];
            if (!out.skipProjection)
            {
                layer.layerFlags = out.projectionFlags;
                layer.space = h.api.localSpace;
                layer.viewCount = 2;
                layer.views = pv;
                layers[count++] = (XrCompositionLayerBaseHeader*)&layer;
            }
        }
    }
    XrFrameEndInfo ei{XR_TYPE_FRAME_END_INFO};
    ei.displayTime = fs.predictedDisplayTime;
    ei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    ei.layerCount = count;
    ei.layers = layers;
    xrEndFrame(h.api.session, &ei);
}

void OnCmd(android_app* app, int32_t cmd)
{
    auto* h = (Host*)app->userData;
    if (cmd == APP_CMD_RESUME) h->resumed = true;
    else if (cmd == APP_CMD_PAUSE) h->resumed = false;
    else if (cmd == APP_CMD_DESTROY) h->exit = true;
}

} // namespace

void android_main(android_app* android)
{
    Host* hp = new Host(); // lives for the process: the channel thread may still hold it
    Host& h = *hp;
    g_host = hp;
    h.android = android;
    android->userData = hp;
    android->onAppCmd = OnCmd;
    JNIEnv* env = nullptr;
    android->activity->vm->AttachCurrentThread(&env, nullptr);
    {
        jclass cls = env->GetObjectClass(android->activity->clazz);
        jmethodID m = env->GetMethodID(cls, "getPackageName", "()Ljava/lang/String;");
        jstring js = m ? (jstring)env->CallObjectMethod(android->activity->clazz, m) : nullptr;
        if (js)
        {
            const char* c = env->GetStringUTFChars(js, nullptr);
            snprintf(g_appId, sizeof g_appId, "%s", c);
            env->ReleaseStringUTFChars(js, c);
        }
        if (env->ExceptionCheck()) env->ExceptionClear();
    }
    h.api.abi = MCX_MODULE_ABI;
    h.api.vm = android->activity->vm;
    h.api.activity = env->NewGlobalRef(android->activity->clazz);
    h.api.assets = android->activity->assetManager;
    h.api.dataPath = android->activity->internalDataPath;
    h.api.getProcAddr = xrGetInstanceProcAddr;
    h.api.hasExtension = HasExtension;
    h.api.log = Log;
    LOG("starting %s (host built %s %s)", APP_ID, __DATE__, __TIME__);

    // a shipped app carries its module; the Runner has none and waits for `mcx-app dev`
    void* shipped = dlopen("libapp.so", RTLD_NOW | RTLD_LOCAL);
    std::vector<std::string> extra;
    if (shipped)
    {
        auto exts = Sym<McxExtensionsFn>(shipped, "mcx_app_extensions");
        uint32_t n = 0;
        const char* const* list = exts ? exts(&n) : nullptr;
        for (uint32_t i = 0; list && i < n; i++) extra.push_back(list[i]);
    }
    else
    {
        h.dev = true;
        h.key = LoadKey(h.api.assets);
        LOG("Runner: no module in the APK; waiting for `mcx-app dev` on 127.0.0.1:47951 (key %s)", h.key.ok ? "loaded" : "MISSING: no module will be accepted");
    }
    h.api.dev = h.dev ? 1 : 0;
    h.ok = InitXr(h, extra);
    if (!h.ok) LOG("OpenXR init failed: see the lines above");
    if (h.ok && shipped) LoadModule(h, "libapp.so", "");
    if (shipped) dlclose(shipped); // LoadModule took its own reference
    if (h.dev) h.channel = std::thread(ChannelLoop, hp);

    while (!android->destroyRequested && !h.exit)
    {
        int events = 0;
        android_poll_source* source = nullptr;
        while (ALooper_pollOnce((h.ok && (h.running || h.resumed)) ? 0 : 100, nullptr, &events, (void**)&source) >= 0)
        {
            if (source) source->process(android, source);
            if (android->destroyRequested) break;
        }
        h.poll.store(h.dev && h.resumed);
        if (!h.ok) continue;
        if (h.dev)
        {
            std::string path, sha;
            {
                std::lock_guard<std::mutex> lock(h.pendingMutex);
                path.swap(h.pendingPath);
                sha.swap(h.pendingSha);
            }
            if (!path.empty())
            {
                {
                    std::lock_guard<std::mutex> lock(h.pendingMutex);
                    h.lastSha = sha;
                }
                if (!LoadModule(h, path, sha)) unlink(path.c_str());
            }
        }
        PollXr(h);
        if (h.running) RenderFrame(h);
    }
    LOG("exiting");
    h.quit.store(true);
    if (h.channel.joinable()) h.channel.join();
    if (h.mod.handle && h.mod.shutdown) h.mod.shutdown();
    if (h.api.session != XR_NULL_HANDLE) xrDestroySession(h.api.session);
    if (h.api.instance != XR_NULL_HANDLE) xrDestroyInstance(h.api.instance);
    env->DeleteGlobalRef(h.api.activity);
    android->activity->vm->DetachCurrentThread();
    ANativeActivity_finish(android->activity);
}
