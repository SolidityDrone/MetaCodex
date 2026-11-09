// Hello Native: the smallest native Quest app MetaCodex builds and installs, and the template of `mcx-app new`.
// Pure C++ (no Java, no Gradle, no Unity): OpenXR for the headset, OpenGL ES 3 for drawing, the NDK's native app
// glue for the Android activity. It shows a dark space with three slowly turning coloured cubes in front of you.
// Build: ../build.sh device (in MetaCodex's Linux; `mcx-app build`) or ../build.sh pc (WSL, Unity's Android tools).
#include <android/log.h>
#include <android_native_app_glue.h>
#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <jni.h>
#define XR_USE_PLATFORM_ANDROID
#define XR_USE_GRAPHICS_API_OPENGL_ES
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>
#include <cerrno>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <vector>

// the package name, from AndroidManifest.xml (build.sh passes -DAPP_ID_RAW=<package>)
#define STR2_(x) #x
#define STR_(x) STR2_(x)
#ifdef APP_ID_RAW
#define APP_ID STR_(APP_ID_RAW)
#else
#define APP_ID "com.metacodex.hellonative"
#endif

namespace {

// Logs go to logcat and, when `mcx-app` listens in MetaCodex's Linux (127.0.0.1:47950), to the agent that built the
// app (logcat cannot be read from there). Best effort and never blocking: no listener, no socket. Main thread only.
int g_logFd = -2; // -2: not tried yet, -1: no listener
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
    if (g_logFd == -2)
    {
        g_logFd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_port = htons(47950);
        addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (g_logFd >= 0 && connect(g_logFd, (sockaddr*)&addr, sizeof addr) != 0) { close(g_logFd); g_logFd = -1; }
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

} // namespace

#define LOG(...) Log(__VA_ARGS__)
#define XR(call) do { XrResult r_ = (call); if (XR_FAILED(r_)) { LOG("%s failed: %d", #call, (int)r_); return false; } } while (0)

namespace {

struct Eye
{
    XrSwapchain swapchain = XR_NULL_HANDLE;
    int32_t width = 0, height = 0;
    std::vector<XrSwapchainImageOpenGLESKHR> images;
    GLuint depth = 0;
};

struct App
{
    android_app* android = nullptr;
    bool resumed = false;
    EGLDisplay display = EGL_NO_DISPLAY;
    EGLContext context = EGL_NO_CONTEXT;
    EGLSurface surface = EGL_NO_SURFACE;
    XrInstance instance = XR_NULL_HANDLE;
    XrSystemId system = XR_NULL_SYSTEM_ID;
    XrSession session = XR_NULL_HANDLE;
    XrSpace space = XR_NULL_HANDLE;
    XrSessionState state = XR_SESSION_STATE_UNKNOWN;
    bool running = false, exit = false;
    Eye eyes[2];
    GLuint program = 0, vao = 0, vbo = 0, fbo = 0;
    GLint mvp = -1, tint = -1;
    double t0 = -1.0;
};

// ---------------------------------------------------------------- maths (column-major, as OpenGL)

struct Mat { float m[16]; };

Mat Mul(const Mat& a, const Mat& b)
{
    Mat r{};
    for (int c = 0; c < 4; c++)
        for (int k = 0; k < 4; k++)
        {
            float s = 0.f;
            for (int i = 0; i < 4; i++) s += a.m[i * 4 + k] * b.m[c * 4 + i];
            r.m[c * 4 + k] = s;
        }
    return r;
}

// an OpenXR field of view (four angles, asymmetric) as a projection
Mat Projection(const XrFovf& f, float n, float far)
{
    float l = tanf(f.angleLeft), r = tanf(f.angleRight), d = tanf(f.angleDown), u = tanf(f.angleUp);
    float w = r - l, h = u - d;
    Mat p{};
    p.m[0] = 2.f / w;
    p.m[5] = 2.f / h;
    p.m[8] = (r + l) / w;
    p.m[9] = (u + d) / h;
    p.m[10] = -(far + n) / (far - n);
    p.m[11] = -1.f;
    p.m[14] = -2.f * far * n / (far - n);
    return p;
}

Mat Rotation(const XrQuaternionf& q)
{
    float x = q.x, y = q.y, z = q.z, w = q.w;
    Mat r{};
    r.m[0] = 1 - 2 * (y * y + z * z); r.m[1] = 2 * (x * y + z * w);     r.m[2] = 2 * (x * z - y * w);
    r.m[4] = 2 * (x * y - z * w);     r.m[5] = 1 - 2 * (x * x + z * z); r.m[6] = 2 * (y * z + x * w);
    r.m[8] = 2 * (x * z + y * w);     r.m[9] = 2 * (y * z - x * w);     r.m[10] = 1 - 2 * (x * x + y * y);
    r.m[15] = 1.f;
    return r;
}

// the inverse of a pose (rotation, then position): world -> eye
Mat View(const XrPosef& pose)
{
    Mat r = Rotation(pose.orientation), v{};
    for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++) v.m[j * 4 + i] = r.m[i * 4 + j]; // transpose
    const XrVector3f& t = pose.position;
    for (int i = 0; i < 3; i++) v.m[12 + i] = -(v.m[i] * t.x + v.m[4 + i] * t.y + v.m[8 + i] * t.z);
    v.m[15] = 1.f;
    return v;
}

Mat Model(float x, float y, float z, float size, float yaw, float pitch)
{
    float cy = cosf(yaw), sy = sinf(yaw), cp = cosf(pitch), sp = sinf(pitch);
    // yaw (around y) then pitch (around x), uniform scale, then the position
    Mat m{};
    m.m[0] = cy * size;  m.m[1] = 0.f;        m.m[2] = -sy * size;
    m.m[4] = sy * sp * size; m.m[5] = cp * size; m.m[6] = cy * sp * size;
    m.m[8] = sy * cp * size; m.m[9] = -sp * size; m.m[10] = cy * cp * size;
    m.m[12] = x; m.m[13] = y; m.m[14] = z; m.m[15] = 1.f;
    return m;
}

// ---------------------------------------------------------------- drawing

const char* kVertex = R"(#version 300 es
layout(location = 0) in vec3 position;
layout(location = 1) in vec3 color;
uniform mat4 mvp;
out vec3 vColor;
void main() { vColor = color; gl_Position = mvp * vec4(position, 1.0); }
)";

const char* kFragment = R"(#version 300 es
precision mediump float;
in vec3 vColor;
uniform vec3 tint;
out vec4 outColor;
void main() { outColor = vec4(vColor * tint, 1.0); }
)";

GLuint Compile(GLenum type, const char* src)
{
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint ok = 0;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) { char log[512]; glGetShaderInfoLog(s, sizeof log, nullptr, log); LOG("shader: %s", log); }
    return s;
}

void BuildScene(App& a)
{
    a.program = glCreateProgram();
    glAttachShader(a.program, Compile(GL_VERTEX_SHADER, kVertex));
    glAttachShader(a.program, Compile(GL_FRAGMENT_SHADER, kFragment));
    glLinkProgram(a.program);
    a.mvp = glGetUniformLocation(a.program, "mvp");
    a.tint = glGetUniformLocation(a.program, "tint");
    // a unit cube, one grey shade per face (position xyz, shade rgb); each cube gets its colour from `tint`
    static const float faces[6][4][3] = {
        {{-1, -1, 1}, {1, -1, 1}, {1, 1, 1}, {-1, 1, 1}},      {{1, -1, -1}, {-1, -1, -1}, {-1, 1, -1}, {1, 1, -1}},
        {{-1, -1, -1}, {-1, -1, 1}, {-1, 1, 1}, {-1, 1, -1}},  {{1, -1, 1}, {1, -1, -1}, {1, 1, -1}, {1, 1, 1}},
        {{-1, 1, 1}, {1, 1, 1}, {1, 1, -1}, {-1, 1, -1}},      {{-1, -1, -1}, {1, -1, -1}, {1, -1, 1}, {-1, -1, 1}},
    };
    static const float shade[6] = {1.0f, 0.55f, 0.75f, 0.8f, 0.95f, 0.45f};
    std::vector<float> v;
    for (int f = 0; f < 6; f++)
    {
        const int idx[6] = {0, 1, 2, 0, 2, 3};
        for (int k : idx)
        {
            v.insert(v.end(), {faces[f][k][0], faces[f][k][1], faces[f][k][2]});
            v.insert(v.end(), {shade[f], shade[f], shade[f]});
        }
    }
    glGenVertexArrays(1, &a.vao);
    glBindVertexArray(a.vao);
    glGenBuffers(1, &a.vbo);
    glBindBuffer(GL_ARRAY_BUFFER, a.vbo);
    glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)(v.size() * sizeof(float)), v.data(), GL_STATIC_DRAW);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 6 * sizeof(float), (void*)0);
    glEnableVertexAttribArray(1);
    glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, 6 * sizeof(float), (void*)(3 * sizeof(float)));
    glBindVertexArray(0);
    glGenFramebuffers(1, &a.fbo);
}

void DrawEye(App& a, const XrView& view, double t)
{
    glClearColor(0.015f, 0.025f, 0.05f, 1.f);
    glClearDepthf(1.f);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
    glEnable(GL_DEPTH_TEST);
    glEnable(GL_CULL_FACE);
    glUseProgram(a.program);
    glBindVertexArray(a.vao);
    Mat vp = Mul(Projection(view.fov, 0.05f, 50.f), View(view.pose));
    // three cubes ahead of where the app started: cyan, magenta, amber
    struct Cube { float x, y, z, size, speed, r, g, b; } cubes[3] = {
        {0.0f, 0.0f, -1.5f, 0.12f, 0.6f, 0.21f, 0.82f, 1.0f},
        {-0.55f, 0.18f, -1.9f, 0.09f, -0.9f, 0.85f, 0.35f, 1.0f},
        {0.6f, -0.12f, -1.8f, 0.10f, 0.75f, 1.0f, 0.72f, 0.28f},
    };
    for (const Cube& c : cubes)
    {
        Mat mvp = Mul(vp, Model(c.x, c.y, c.z, c.size, (float)(t * c.speed), (float)(t * c.speed * 0.6)));
        glUniformMatrix4fv(a.mvp, 1, GL_FALSE, mvp.m);
        glUniform3f(a.tint, c.r, c.g, c.b);
        glDrawArrays(GL_TRIANGLES, 0, 36);
    }
    glBindVertexArray(0);
}

// ---------------------------------------------------------------- OpenXR

bool InitEgl(App& a)
{
    a.display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    eglInitialize(a.display, nullptr, nullptr);
    const EGLint cfg[] = {EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_RENDERABLE_TYPE, 0x40 /* ES3 */, EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE};
    EGLConfig config;
    EGLint n = 0;
    if (!eglChooseConfig(a.display, cfg, &config, 1, &n) || n == 0) { LOG("no EGL config"); return false; }
    const EGLint ctx[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
    a.context = eglCreateContext(a.display, config, EGL_NO_CONTEXT, ctx);
    const EGLint pb[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE};
    a.surface = eglCreatePbufferSurface(a.display, config, pb);
    if (a.context == EGL_NO_CONTEXT || !eglMakeCurrent(a.display, a.surface, a.surface, a.context)) { LOG("EGL context failed"); return false; }
    return true;
}

bool InitXr(App& a)
{
    PFN_xrInitializeLoaderKHR initLoader = nullptr;
    XR(xrGetInstanceProcAddr(XR_NULL_HANDLE, "xrInitializeLoaderKHR", (PFN_xrVoidFunction*)&initLoader));
    XrLoaderInitInfoAndroidKHR li{XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR};
    li.applicationVM = a.android->activity->vm;
    li.applicationContext = a.android->activity->clazz;
    XR(initLoader((const XrLoaderInitInfoBaseHeaderKHR*)&li));

    const char* exts[] = {XR_KHR_ANDROID_CREATE_INSTANCE_EXTENSION_NAME, XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME};
    XrInstanceCreateInfoAndroidKHR android{XR_TYPE_INSTANCE_CREATE_INFO_ANDROID_KHR};
    android.applicationVM = a.android->activity->vm;
    android.applicationActivity = a.android->activity->clazz;
    XrInstanceCreateInfo ci{XR_TYPE_INSTANCE_CREATE_INFO};
    ci.next = &android;
    strcpy(ci.applicationInfo.applicationName, "Hello Native");
    ci.applicationInfo.applicationVersion = 1;
    strcpy(ci.applicationInfo.engineName, "MetaCodex native");
    ci.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ci.enabledExtensionCount = 2;
    ci.enabledExtensionNames = exts;
    XR(xrCreateInstance(&ci, &a.instance));

    XrSystemGetInfo si{XR_TYPE_SYSTEM_GET_INFO};
    si.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XR(xrGetSystem(a.instance, &si, &a.system));

    PFN_xrGetOpenGLESGraphicsRequirementsKHR req = nullptr;
    XR(xrGetInstanceProcAddr(a.instance, "xrGetOpenGLESGraphicsRequirementsKHR", (PFN_xrVoidFunction*)&req));
    XrGraphicsRequirementsOpenGLESKHR gr{XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR};
    XR(req(a.instance, a.system, &gr));

    if (!InitEgl(a)) return false;
    XrGraphicsBindingOpenGLESAndroidKHR gb{XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR};
    gb.display = a.display;
    gb.config = (EGLConfig)0;
    gb.context = a.context;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &gb;
    sci.systemId = a.system;
    XR(xrCreateSession(a.instance, &sci, &a.session));

    XrReferenceSpaceCreateInfo rs{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rs.poseInReferenceSpace.orientation.w = 1.f;
    XR(xrCreateReferenceSpace(a.session, &rs, &a.space));

    uint32_t nv = 0;
    XR(xrEnumerateViewConfigurationViews(a.instance, a.system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &nv, nullptr));
    std::vector<XrViewConfigurationView> views(nv, {XR_TYPE_VIEW_CONFIGURATION_VIEW});
    XR(xrEnumerateViewConfigurationViews(a.instance, a.system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, nv, &nv, views.data()));
    uint32_t nf = 0;
    XR(xrEnumerateSwapchainFormats(a.session, 0, &nf, nullptr));
    std::vector<int64_t> formats(nf);
    XR(xrEnumerateSwapchainFormats(a.session, nf, &nf, formats.data()));
    int64_t format = formats.empty() ? GL_RGBA8 : formats[0];
    for (int64_t f : formats) if (f == GL_SRGB8_ALPHA8) format = f;
    for (uint32_t e = 0; e < 2 && e < nv; e++)
    {
        Eye& eye = a.eyes[e];
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
        XR(xrCreateSwapchain(a.session, &sc, &eye.swapchain));
        uint32_t ni = 0;
        XR(xrEnumerateSwapchainImages(eye.swapchain, 0, &ni, nullptr));
        eye.images.assign(ni, {XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_ES_KHR});
        XR(xrEnumerateSwapchainImages(eye.swapchain, ni, &ni, (XrSwapchainImageBaseHeader*)eye.images.data()));
        glGenRenderbuffers(1, &eye.depth);
        glBindRenderbuffer(GL_RENDERBUFFER, eye.depth);
        glRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, eye.width, eye.height);
    }
    BuildScene(a);
    LOG("OpenXR ready: %dx%d per eye, format 0x%llx", a.eyes[0].width, a.eyes[0].height, (long long)format);
    return true;
}

void PollXr(App& a)
{
    XrEventDataBuffer ev{XR_TYPE_EVENT_DATA_BUFFER};
    while (xrPollEvent(a.instance, &ev) == XR_SUCCESS)
    {
        if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED)
        {
            auto* sc = (XrEventDataSessionStateChanged*)&ev;
            a.state = sc->state;
            LOG("session state %d", (int)a.state);
            if (a.state == XR_SESSION_STATE_READY)
            {
                XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO};
                bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                a.running = XR_SUCCEEDED(xrBeginSession(a.session, &bi));
            }
            else if (a.state == XR_SESSION_STATE_STOPPING) { xrEndSession(a.session); a.running = false; }
            else if (a.state == XR_SESSION_STATE_EXITING || a.state == XR_SESSION_STATE_LOSS_PENDING) a.exit = true;
        }
        else if (ev.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) a.exit = true;
        ev = {XR_TYPE_EVENT_DATA_BUFFER};
    }
}

void RenderFrame(App& a)
{
    XrFrameState fs{XR_TYPE_FRAME_STATE};
    if (XR_FAILED(xrWaitFrame(a.session, nullptr, &fs))) return;
    xrBeginFrame(a.session, nullptr);
    XrCompositionLayerProjectionView pv[2] = {{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}, {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}};
    XrCompositionLayerProjection layer{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    const XrCompositionLayerBaseHeader* layers[1] = {(XrCompositionLayerBaseHeader*)&layer};
    uint32_t layerCount = 0;
    if (fs.shouldRender)
    {
        XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO};
        li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
        li.displayTime = fs.predictedDisplayTime;
        li.space = a.space;
        XrViewState vs{XR_TYPE_VIEW_STATE};
        XrView views[2] = {{XR_TYPE_VIEW}, {XR_TYPE_VIEW}};
        uint32_t nv = 0;
        if (XR_SUCCEEDED(xrLocateViews(a.session, &li, &vs, 2, &nv, views)) && nv == 2)
        {
            double t = (double)fs.predictedDisplayTime * 1e-9;
            if (a.t0 < 0) a.t0 = t;
            for (int e = 0; e < 2; e++)
            {
                Eye& eye = a.eyes[e];
                uint32_t idx = 0;
                XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
                xrAcquireSwapchainImage(eye.swapchain, &ai, &idx);
                XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
                wi.timeout = XR_INFINITE_DURATION;
                xrWaitSwapchainImage(eye.swapchain, &wi);
                glBindFramebuffer(GL_FRAMEBUFFER, a.fbo);
                glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, eye.images[idx].image, 0);
                glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, eye.depth);
                glViewport(0, 0, eye.width, eye.height);
                DrawEye(a, views[e], t - a.t0);
                glBindFramebuffer(GL_FRAMEBUFFER, 0);
                XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
                xrReleaseSwapchainImage(eye.swapchain, &ri);
                pv[e].pose = views[e].pose;
                pv[e].fov = views[e].fov;
                pv[e].subImage.swapchain = eye.swapchain;
                pv[e].subImage.imageRect = {{0, 0}, {eye.width, eye.height}};
            }
            layer.space = a.space;
            layer.viewCount = 2;
            layer.views = pv;
            layerCount = 1;
        }
    }
    XrFrameEndInfo ei{XR_TYPE_FRAME_END_INFO};
    ei.displayTime = fs.predictedDisplayTime;
    ei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    ei.layerCount = layerCount;
    ei.layers = layers;
    xrEndFrame(a.session, &ei);
}

void OnCmd(android_app* app, int32_t cmd)
{
    auto* a = (App*)app->userData;
    if (cmd == APP_CMD_RESUME) a->resumed = true;
    else if (cmd == APP_CMD_PAUSE) a->resumed = false;
    else if (cmd == APP_CMD_DESTROY) a->exit = true;
}

} // namespace

void android_main(android_app* android)
{
    App a;
    a.android = android;
    android->userData = &a;
    android->onAppCmd = OnCmd;
    JNIEnv* env = nullptr;
    android->activity->vm->AttachCurrentThread(&env, nullptr);
    LOG("starting %s (built %s %s)", APP_ID, __DATE__, __TIME__);
    bool ok = InitXr(a);
    if (!ok) LOG("OpenXR init failed: see the lines above");
    while (!android->destroyRequested && !a.exit)
    {
        int events = 0;
        android_poll_source* source = nullptr;
        // poll without waiting while the session runs; otherwise sleep until something happens
        while (ALooper_pollOnce((ok && (a.running || a.resumed)) ? 0 : 100, nullptr, &events, (void**)&source) >= 0)
        {
            if (source) source->process(android, source);
            if (android->destroyRequested) break;
        }
        if (!ok) continue;
        PollXr(a);
        if (a.running) RenderFrame(a);
    }
    LOG("exiting");
    if (a.session != XR_NULL_HANDLE) xrDestroySession(a.session);
    if (a.instance != XR_NULL_HANDLE) xrDestroyInstance(a.instance);
    if (g_logFd >= 0) close(g_logFd);
    android->activity->vm->DetachCurrentThread();
    ANativeActivity_finish(android->activity);
}
