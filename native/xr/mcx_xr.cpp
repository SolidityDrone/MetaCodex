// mcx_xr.cpp: see mcx_xr.h. Built once per toolchain by mcx-build into ~/.cache/mcx-build/libmcxxr-*.a.
#include "mcx_xr.h"
#include "mcx_font.h"
#include <android/log.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>
#include <cerrno>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <deque>
#include <mutex>
#include <zlib.h>

namespace mcx {

// ================================================================ log
static char g_tag[128] = "mcx";
static std::mutex g_logMutex;
static int g_logFd = -2;

static std::deque<std::string> g_ring; // the last lines, for reports

void SetLogTag(const char* tag) { snprintf(g_tag, sizeof g_tag, "%s", tag); }

std::string RecentLog(int maxLines)
{
    std::lock_guard<std::mutex> lock(g_logMutex);
    std::string out;
    int start = (int)g_ring.size() > maxLines ? (int)g_ring.size() - maxLines : 0;
    for (int i = start; i < (int)g_ring.size(); i++) out += g_ring[i] + "\n";
    return out;
}

void Log(const char* fmt, ...)
{
    char line[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(line, sizeof line - 1, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > (int)sizeof line - 2) n = (int)sizeof line - 2;
    __android_log_print(ANDROID_LOG_INFO, g_tag, "%s", line);
    std::lock_guard<std::mutex> lock(g_logMutex);
    {
        char stamp[16];
        time_t now = time(nullptr);
        tm lt{};
        localtime_r(&now, &lt);
        strftime(stamp, sizeof stamp, "%H:%M:%S ", &lt);
        g_ring.push_back(std::string(stamp) + line);
        if (g_ring.size() > 200) g_ring.pop_front();
    }
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
            int h = snprintf(hello, sizeof hello, "== %s connected\n", g_tag);
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

static double Now()
{
    timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec * 1e-9;
}

#define XRCHECK(call) ([&]() { XrResult r_ = (call); if (XR_FAILED(r_)) { Log("%s failed: %d", #call, (int)r_); return false; } return true; }())

// ================================================================ maths
Mat4 Mat4::Identity()
{
    Mat4 r{};
    r.m[0] = r.m[5] = r.m[10] = r.m[15] = 1.f;
    return r;
}
Mat4 Mat4::Translation(float x, float y, float z)
{
    Mat4 r = Identity();
    r.m[12] = x; r.m[13] = y; r.m[14] = z;
    return r;
}
Mat4 Mat4::Scale(float x, float y, float z)
{
    Mat4 r = Identity();
    r.m[0] = x; r.m[5] = y; r.m[10] = z;
    return r;
}
Mat4 Mat4::operator*(const Mat4& b) const
{
    Mat4 r{};
    for (int c = 0; c < 4; c++)
        for (int k = 0; k < 4; k++)
        {
            float s = 0.f;
            for (int i = 0; i < 4; i++) s += m[i * 4 + k] * b.m[c * 4 + i];
            r.m[c * 4 + k] = s;
        }
    return r;
}
static void RotMat(const XrQuaternionf& q, float* r) // 3x3 into a 4x4 column-major
{
    float x = q.x, y = q.y, z = q.z, w = q.w;
    r[0] = 1 - 2 * (y * y + z * z); r[1] = 2 * (x * y + z * w);     r[2] = 2 * (x * z - y * w);
    r[4] = 2 * (x * y - z * w);     r[5] = 1 - 2 * (x * x + z * z); r[6] = 2 * (y * z + x * w);
    r[8] = 2 * (x * z + y * w);     r[9] = 2 * (y * z - x * w);     r[10] = 1 - 2 * (x * x + y * y);
}
Mat4 Mat4::FromPose(const XrPosef& p)
{
    Mat4 r = Identity();
    RotMat(p.orientation, r.m);
    r.m[12] = p.position.x; r.m[13] = p.position.y; r.m[14] = p.position.z;
    return r;
}
Mat4 Mat4::ViewFromPose(const XrPosef& p)
{
    Mat4 rot = Identity(), v = Identity();
    RotMat(p.orientation, rot.m);
    for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++) v.m[j * 4 + i] = rot.m[i * 4 + j];
    const XrVector3f& t = p.position;
    for (int i = 0; i < 3; i++) v.m[12 + i] = -(v.m[i] * t.x + v.m[4 + i] * t.y + v.m[8 + i] * t.z);
    return v;
}
Mat4 Mat4::Projection(const XrFovf& f, float n, float fr)
{
    float l = tanf(f.angleLeft), r = tanf(f.angleRight), d = tanf(f.angleDown), u = tanf(f.angleUp);
    float w = r - l, h = u - d;
    Mat4 p{};
    p.m[0] = 2.f / w; p.m[5] = 2.f / h; p.m[8] = (r + l) / w; p.m[9] = (u + d) / h; p.m[11] = -1.f;
    if (fr <= n || std::isinf(fr)) { p.m[10] = -1.f; p.m[14] = -2.f * n; } // infinite far plane (Meta's depth convention)
    else { p.m[10] = -(fr + n) / (fr - n); p.m[14] = -2.f * fr * n / (fr - n); }
    return p;
}
XrVector3f Rotate(const XrQuaternionf& q, const XrVector3f& v)
{
    float r[16];
    RotMat(q, r);
    return {r[0] * v.x + r[4] * v.y + r[8] * v.z, r[1] * v.x + r[5] * v.y + r[9] * v.z, r[2] * v.x + r[6] * v.y + r[10] * v.z};
}
XrVector3f Forward(const XrPosef& p) { return Rotate(p.orientation, {0, 0, -1}); }

// ================================================================ permissions
static JNIEnv* Env(ANativeActivity* act)
{
    JNIEnv* env = nullptr;
    if (act->vm->GetEnv((void**)&env, JNI_VERSION_1_6) != JNI_OK) act->vm->AttachCurrentThread(&env, nullptr);
    return env;
}

bool Permissions::Granted(ANativeActivity* act, const char* permission)
{
    if (!act) return false;
    JNIEnv* env = Env(act);
    jclass cls = env->GetObjectClass(act->clazz);
    jmethodID check = env->GetMethodID(cls, "checkSelfPermission", "(Ljava/lang/String;)I");
    if (!check) { env->ExceptionClear(); env->DeleteLocalRef(cls); return false; }
    jstring p = env->NewStringUTF(permission);
    jint r = env->CallIntMethod(act->clazz, check, p);
    if (env->ExceptionCheck()) { env->ExceptionClear(); r = -1; }
    env->DeleteLocalRef(p);
    env->DeleteLocalRef(cls);
    return r == 0; // PackageManager.PERMISSION_GRANTED
}

void Permissions::Request(ANativeActivity* act, const std::vector<const char*>& permissions)
{
    if (!act || permissions.empty()) return;
    JNIEnv* env = Env(act);
    jclass str = env->FindClass("java/lang/String");
    jobjectArray arr = env->NewObjectArray((jsize)permissions.size(), str, nullptr);
    for (size_t i = 0; i < permissions.size(); i++)
    {
        jstring s = env->NewStringUTF(permissions[i]);
        env->SetObjectArrayElement(arr, (jsize)i, s);
        env->DeleteLocalRef(s);
    }
    jclass cls = env->GetObjectClass(act->clazz);
    jmethodID req = env->GetMethodID(cls, "requestPermissions", "([Ljava/lang/String;I)V");
    if (req) env->CallVoidMethod(act->clazz, req, arr, 4711);
    if (env->ExceptionCheck()) { env->ExceptionClear(); Log("requestPermissions failed"); }
    env->DeleteLocalRef(cls);
    env->DeleteLocalRef(arr);
    env->DeleteLocalRef(str);
    for (const char* p : permissions) Log("permission %s requested", p);
}

// ================================================================ extensions
bool Extensions::Has(const char* name) const
{
    for (auto& e : enabled)
        if (e == name) return true;
    return false;
}
bool Extensions::Offered(const char* name) const
{
    for (auto& e : available)
        if (e == name) return true;
    return false;
}

// ================================================================ passthrough
bool Passthrough::Init(XrInstance inst, XrSession session)
{
    inst_ = inst;
    session_ = session;
    auto createPt = Proc<PFN_xrCreatePassthroughFB>(inst, "xrCreatePassthroughFB");
    auto createLayer = Proc<PFN_xrCreatePassthroughLayerFB>(inst, "xrCreatePassthroughLayerFB");
    start_ = Proc<PFN_xrPassthroughStartFB>(inst, "xrPassthroughStartFB");
    pause_ = Proc<PFN_xrPassthroughPauseFB>(inst, "xrPassthroughPauseFB");
    resume_ = Proc<PFN_xrPassthroughLayerResumeFB>(inst, "xrPassthroughLayerResumeFB");
    lpause_ = Proc<PFN_xrPassthroughLayerPauseFB>(inst, "xrPassthroughLayerPauseFB");
    style_ = Proc<PFN_xrPassthroughLayerSetStyleFB>(inst, "xrPassthroughLayerSetStyleFB");
    if (!createPt || !createLayer || !start_) { Log("passthrough: XR_FB_passthrough not enabled"); return false; }
    XrPassthroughCreateInfoFB ci{XR_TYPE_PASSTHROUGH_CREATE_INFO_FB};
    if (!XRCHECK(createPt(session, &ci, &pt_))) return false;
    XrPassthroughLayerCreateInfoFB li{XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB};
    li.passthrough = pt_;
    li.purpose = XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB;
    if (!XRCHECK(createLayer(session, &li, &layer_))) return false;
    comp_.flags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
    comp_.space = XR_NULL_HANDLE;
    comp_.layerHandle = layer_;
    Log("passthrough: ready (paused)");
    return true;
}
void Passthrough::Start()
{
    if (!layer_ || running_) return;
    if (XR_SUCCEEDED(start_(pt_)) && XR_SUCCEEDED(resume_(layer_))) { running_ = true; Log("passthrough: started"); }
}
void Passthrough::Pause()
{
    if (!layer_ || !running_) return;
    lpause_(layer_);
    pause_(pt_);
    running_ = false;
    Log("passthrough: paused");
}
void Passthrough::SetStyle(float opacity, XrColor4f edge)
{
    if (!layer_) return;
    XrPassthroughStyleFB s{XR_TYPE_PASSTHROUGH_STYLE_FB};
    s.textureOpacityFactor = opacity;
    s.edgeColor = edge;
    style_(layer_, &s);
}
void Passthrough::SetMonoToRgba(const std::function<XrColor4f(int)>& map, float opacity, XrColor4f edge)
{
    if (!layer_) return;
    XrPassthroughColorMapMonoToRgbaFB cm{XR_TYPE_PASSTHROUGH_COLOR_MAP_MONO_TO_RGBA_FB};
    for (int i = 0; i < XR_PASSTHROUGH_COLOR_MAP_MONO_SIZE_FB; i++) cm.textureColorMap[i] = map(i);
    XrPassthroughStyleFB s{XR_TYPE_PASSTHROUGH_STYLE_FB};
    s.next = &cm;
    s.textureOpacityFactor = opacity;
    s.edgeColor = edge;
    style_(layer_, &s);
}
void Passthrough::SetBrightnessContrastSaturation(float b, float c, float sat, float opacity)
{
    if (!layer_) return;
    XrPassthroughBrightnessContrastSaturationFB bcs{XR_TYPE_PASSTHROUGH_BRIGHTNESS_CONTRAST_SATURATION_FB};
    bcs.brightness = b;
    bcs.contrast = c;
    bcs.saturation = sat;
    XrPassthroughStyleFB s{XR_TYPE_PASSTHROUGH_STYLE_FB};
    s.next = &bcs;
    s.textureOpacityFactor = opacity;
    style_(layer_, &s);
}
const XrCompositionLayerBaseHeader* Passthrough::Layer() { return (const XrCompositionLayerBaseHeader*)&comp_; }
void Passthrough::Destroy()
{
    auto dl = Proc<PFN_xrDestroyPassthroughLayerFB>(inst_, "xrDestroyPassthroughLayerFB");
    auto dp = Proc<PFN_xrDestroyPassthroughFB>(inst_, "xrDestroyPassthroughFB");
    if (layer_ && dl) dl(layer_);
    if (pt_ && dp) dp(pt_);
    layer_ = XR_NULL_HANDLE;
    pt_ = XR_NULL_HANDLE;
    running_ = false;
}

// ================================================================ environment depth
float DepthFrame::Linear(float d) const
{
    float ndc = d * 2.f - 1.f;
    if (std::isinf(farZ) || farZ <= nearZ) return 2.f * nearZ / (1.f - ndc);
    return 2.f * farZ * nearZ / (farZ + nearZ - ndc * (farZ - nearZ));
}

bool Depth::Init(XrInstance inst, XrSession session)
{
    inst_ = inst;
    session_ = session;
    auto create = Proc<PFN_xrCreateEnvironmentDepthProviderMETA>(inst, "xrCreateEnvironmentDepthProviderMETA");
    auto createSc = Proc<PFN_xrCreateEnvironmentDepthSwapchainMETA>(inst, "xrCreateEnvironmentDepthSwapchainMETA");
    auto state = Proc<PFN_xrGetEnvironmentDepthSwapchainStateMETA>(inst, "xrGetEnvironmentDepthSwapchainStateMETA");
    auto images = Proc<PFN_xrEnumerateEnvironmentDepthSwapchainImagesMETA>(inst, "xrEnumerateEnvironmentDepthSwapchainImagesMETA");
    acquire_ = Proc<PFN_xrAcquireEnvironmentDepthImageMETA>(inst, "xrAcquireEnvironmentDepthImageMETA");
    if (!create || !createSc || !state || !images || !acquire_) { Log("depth: XR_META_environment_depth not enabled"); return false; }
    XrEnvironmentDepthProviderCreateInfoMETA ci{XR_TYPE_ENVIRONMENT_DEPTH_PROVIDER_CREATE_INFO_META};
    XrResult r = create(session, &ci, &provider_);
    if (XR_FAILED(r)) { Log("depth: provider not created (%d): is USE_SCENE granted?", (int)r); provider_ = XR_NULL_HANDLE; return false; }
    XrEnvironmentDepthSwapchainCreateInfoMETA sci{XR_TYPE_ENVIRONMENT_DEPTH_SWAPCHAIN_CREATE_INFO_META};
    if (!XRCHECK(createSc(provider_, &sci, &swapchain_))) return false;
    XrEnvironmentDepthSwapchainStateMETA st{XR_TYPE_ENVIRONMENT_DEPTH_SWAPCHAIN_STATE_META};
    if (!XRCHECK(state(swapchain_, &st))) return false;
    w_ = st.width;
    h_ = st.height;
    uint32_t n = 0;
    if (!XRCHECK(images(swapchain_, 0, &n, nullptr))) return false;
    images_.assign(n, {XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_ES_KHR});
    if (!XRCHECK(images(swapchain_, n, &n, (XrSwapchainImageBaseHeader*)images_.data()))) return false;
    for (auto& im : images_) // depth textures: no comparison, nearest sampling
    {
        glBindTexture(GL_TEXTURE_2D_ARRAY, im.image);
        glTexParameteri(GL_TEXTURE_2D_ARRAY, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D_ARRAY, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D_ARRAY, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D_ARRAY, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D_ARRAY, GL_TEXTURE_COMPARE_MODE, GL_NONE);
    }
    glBindTexture(GL_TEXTURE_2D_ARRAY, 0);
    Log("depth: provider ready, %ux%u, %u images", w_, h_, n);
    return true;
}
bool Depth::Start()
{
    auto start = Proc<PFN_xrStartEnvironmentDepthProviderMETA>(inst_, "xrStartEnvironmentDepthProviderMETA");
    if (!provider_ || !start || running_) return running_;
    running_ = XRCHECK(start(provider_));
    if (running_) Log("depth: started");
    return running_;
}
void Depth::Stop()
{
    auto stop = Proc<PFN_xrStopEnvironmentDepthProviderMETA>(inst_, "xrStopEnvironmentDepthProviderMETA");
    if (provider_ && stop && running_) stop(provider_);
    running_ = false;
}
void Depth::SetHandRemoval(bool on)
{
    auto set = Proc<PFN_xrSetEnvironmentDepthHandRemovalMETA>(inst_, "xrSetEnvironmentDepthHandRemovalMETA");
    if (!provider_ || !set) return;
    XrEnvironmentDepthHandRemovalSetInfoMETA i{XR_TYPE_ENVIRONMENT_DEPTH_HAND_REMOVAL_SET_INFO_META};
    i.enabled = on ? XR_TRUE : XR_FALSE;
    set(provider_, &i);
}
bool Depth::Acquire(XrSpace base, XrTime time, DepthFrame& out)
{
    out.valid = false;
    if (!running_) return false;
    XrEnvironmentDepthImageAcquireInfoMETA ai{XR_TYPE_ENVIRONMENT_DEPTH_IMAGE_ACQUIRE_INFO_META};
    ai.space = base;
    ai.displayTime = time;
    XrEnvironmentDepthImageTimestampMETA ts{XR_TYPE_ENVIRONMENT_DEPTH_IMAGE_TIMESTAMP_META};
    XrEnvironmentDepthImageMETA im{XR_TYPE_ENVIRONMENT_DEPTH_IMAGE_META};
    im.next = &ts;
    im.views[0].type = im.views[1].type = XR_TYPE_ENVIRONMENT_DEPTH_IMAGE_VIEW_META;
    XrResult r = acquire_(provider_, &ai, &im);
    if (r != XR_SUCCESS) return false; // XR_ENVIRONMENT_DEPTH_NOT_AVAILABLE_META at the start
    if (im.swapchainIndex >= images_.size()) return false;
    out.valid = true;
    out.texture = images_[im.swapchainIndex].image;
    out.width = w_;
    out.height = h_;
    out.nearZ = im.nearZ;
    out.farZ = im.farZ;
    out.captureTime = ts.captureTime;
    for (int e = 0; e < 2; e++)
    {
        out.views[e] = im.views[e];
        out.viewProj[e] = Mat4::Projection(im.views[e].fov, im.nearZ, im.farZ) * Mat4::ViewFromPose(im.views[e].pose);
    }
    return true;
}
void Depth::Destroy()
{
    Stop();
    auto ds = Proc<PFN_xrDestroyEnvironmentDepthSwapchainMETA>(inst_, "xrDestroyEnvironmentDepthSwapchainMETA");
    auto dp = Proc<PFN_xrDestroyEnvironmentDepthProviderMETA>(inst_, "xrDestroyEnvironmentDepthProviderMETA");
    if (swapchain_ && ds) ds(swapchain_);
    if (provider_ && dp) dp(provider_);
    swapchain_ = XR_NULL_HANDLE;
    provider_ = XR_NULL_HANDLE;
}

// ================================================================ hands
bool Hands::Init(XrInstance inst, XrSession session, bool aim)
{
    auto create = Proc<PFN_xrCreateHandTrackerEXT>(inst, "xrCreateHandTrackerEXT");
    locate_ = Proc<PFN_xrLocateHandJointsEXT>(inst, "xrLocateHandJointsEXT");
    destroy_ = Proc<PFN_xrDestroyHandTrackerEXT>(inst, "xrDestroyHandTrackerEXT");
    if (!create || !locate_) { Log("hands: XR_EXT_hand_tracking not enabled"); return false; }
    for (int h = 0; h < 2; h++)
    {
        XrHandTrackerCreateInfoEXT ci{XR_TYPE_HAND_TRACKER_CREATE_INFO_EXT};
        ci.hand = h == 0 ? XR_HAND_LEFT_EXT : XR_HAND_RIGHT_EXT;
        ci.handJointSet = XR_HAND_JOINT_SET_DEFAULT_EXT;
        if (!XRCHECK(create(session, &ci, &trackers_[h]))) return false;
    }
    aim_ = aim;
    Log("hands: trackers ready (aim %s)", aim ? "on" : "off");
    return true;
}
void Hands::Update(XrSpace base, XrTime time)
{
    for (int h = 0; h < 2; h++)
    {
        Hand& hd = hand[h];
        bool was = hd.pinching;
        hd.tracked = false;
        hd.pinching = false; // a hand that is not located is not pinching (it would stay stuck otherwise)
        hd.pinchStarted = false;
        if (!trackers_[h]) continue;
        XrHandTrackingAimStateFB aim{XR_TYPE_HAND_TRACKING_AIM_STATE_FB};
        XrHandJointLocationsEXT locs{XR_TYPE_HAND_JOINT_LOCATIONS_EXT};
        locs.next = aim_ ? &aim : nullptr;
        locs.jointCount = XR_HAND_JOINT_COUNT_EXT;
        locs.jointLocations = hd.joints;
        XrHandJointsLocateInfoEXT li{XR_TYPE_HAND_JOINTS_LOCATE_INFO_EXT};
        li.baseSpace = base;
        li.time = time;
        if (XR_FAILED(locate_(trackers_[h], &li, &locs))) continue;
        hd.tracked = locs.isActive == XR_TRUE;
        if (aim_)
        {
            hd.aimValid = hd.tracked && (aim.status & XR_HAND_TRACKING_AIM_VALID_BIT_FB);
            hd.aim = aim.aimPose;
            hd.pinch[0] = aim.pinchStrengthIndex;
            hd.pinch[1] = aim.pinchStrengthMiddle;
            hd.pinch[2] = aim.pinchStrengthRing;
            hd.pinch[3] = aim.pinchStrengthLittle;
            hd.pinching = hd.tracked && (aim.status & XR_HAND_TRACKING_AIM_INDEX_PINCHING_BIT_FB);
            hd.menuGesture = (aim.status & XR_HAND_TRACKING_AIM_MENU_PRESSED_BIT_FB) != 0;
            hd.systemGesture = (aim.status & XR_HAND_TRACKING_AIM_SYSTEM_GESTURE_BIT_FB) != 0;
        }
        else
        {
            // pinch from the joints: thumb tip to index tip under 2 cm
            const XrVector3f& a = hd.joints[XR_HAND_JOINT_THUMB_TIP_EXT].pose.position;
            const XrVector3f& b = hd.joints[XR_HAND_JOINT_INDEX_TIP_EXT].pose.position;
            float d = sqrtf((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
            hd.pinching = hd.tracked && d < 0.02f;
            hd.pinch[0] = hd.tracked ? fmaxf(0.f, fminf(1.f, (0.06f - d) / 0.04f)) : 0.f;
        }
        hd.pinchStarted = hd.pinching && !was && !hd.systemGesture;
    }
}
void Hands::Destroy()
{
    for (auto& t : trackers_)
        if (t && destroy_) destroy_(t), t = XR_NULL_HANDLE;
}

// ================================================================ controllers
static XrPath P(XrInstance inst, const char* s)
{
    XrPath p = XR_NULL_PATH;
    xrStringToPath(inst, s, &p);
    return p;
}

bool Controllers::Init(XrInstance inst, XrSession session, const Extensions& ext)
{
    inst_ = inst;
    session_ = session;
    XrActionSetCreateInfo si{XR_TYPE_ACTION_SET_CREATE_INFO};
    strcpy(si.actionSetName, "main");
    strcpy(si.localizedActionSetName, "Main");
    if (!XRCHECK(xrCreateActionSet(inst, &si, &actionSet))) return false;
    hands_[0] = P(inst, "/user/hand/left");
    hands_[1] = P(inst, "/user/hand/right");
    auto make = [&](XrAction& a, const char* name, XrActionType type) {
        XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO};
        ai.actionType = type;
        strcpy(ai.actionName, name);
        strcpy(ai.localizedActionName, name);
        ai.countSubactionPaths = 2;
        ai.subactionPaths = hands_;
        return XRCHECK(xrCreateAction(actionSet, &ai, &a));
    };
    bool ok = make(trigger_, "trigger", XR_ACTION_TYPE_FLOAT_INPUT) && make(squeeze_, "squeeze", XR_ACTION_TYPE_FLOAT_INPUT) &&
              make(primary_, "primary", XR_ACTION_TYPE_BOOLEAN_INPUT) && make(secondary_, "secondary", XR_ACTION_TYPE_BOOLEAN_INPUT) &&
              make(stick_, "thumbstick", XR_ACTION_TYPE_VECTOR2F_INPUT) && make(stickClick_, "thumbstick_click", XR_ACTION_TYPE_BOOLEAN_INPUT) &&
              make(menu_, "menu", XR_ACTION_TYPE_BOOLEAN_INPUT) && make(aim_, "aim", XR_ACTION_TYPE_POSE_INPUT) &&
              make(grip_, "grip", XR_ACTION_TYPE_POSE_INPUT) && make(haptic_, "haptic", XR_ACTION_TYPE_VIBRATION_OUTPUT);
    if (!ok) return false;
    struct B { XrAction a; const char* path; };
    auto suggest = [&](const char* profile, const std::vector<B>& bs) {
        std::vector<XrActionSuggestedBinding> sb;
        for (auto& b : bs) sb.push_back({b.a, P(inst, b.path)});
        XrInteractionProfileSuggestedBinding s{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
        s.interactionProfile = P(inst, profile);
        s.countSuggestedBindings = (uint32_t)sb.size();
        s.suggestedBindings = sb.data();
        XrResult r = xrSuggestInteractionProfileBindings(inst, &s);
        Log("controllers: %s %s", profile, XR_SUCCEEDED(r) ? "bound" : "not supported here");
    };
    std::vector<B> touch = {
        {trigger_, "/user/hand/left/input/trigger/value"}, {trigger_, "/user/hand/right/input/trigger/value"},
        {squeeze_, "/user/hand/left/input/squeeze/value"}, {squeeze_, "/user/hand/right/input/squeeze/value"},
        {primary_, "/user/hand/left/input/x/click"}, {primary_, "/user/hand/right/input/a/click"},
        {secondary_, "/user/hand/left/input/y/click"}, {secondary_, "/user/hand/right/input/b/click"},
        {stick_, "/user/hand/left/input/thumbstick"}, {stick_, "/user/hand/right/input/thumbstick"},
        {stickClick_, "/user/hand/left/input/thumbstick/click"}, {stickClick_, "/user/hand/right/input/thumbstick/click"},
        {menu_, "/user/hand/left/input/menu/click"},
        {aim_, "/user/hand/left/input/aim/pose"}, {aim_, "/user/hand/right/input/aim/pose"},
        {grip_, "/user/hand/left/input/grip/pose"}, {grip_, "/user/hand/right/input/grip/pose"},
        {haptic_, "/user/hand/left/output/haptic"}, {haptic_, "/user/hand/right/output/haptic"}};
    suggest("/interaction_profiles/oculus/touch_controller", touch);
    if (ext.Has("XR_META_touch_controller_plus")) suggest("/interaction_profiles/meta/touch_controller_plus", touch);
    if (ext.Has("XR_FB_touch_controller_pro")) suggest("/interaction_profiles/facebook/touch_controller_pro", touch);
    suggest("/interaction_profiles/khr/simple_controller",
            {{trigger_, "/user/hand/left/input/select/click"}, {trigger_, "/user/hand/right/input/select/click"},
             {menu_, "/user/hand/left/input/menu/click"},
             {aim_, "/user/hand/left/input/aim/pose"}, {aim_, "/user/hand/right/input/aim/pose"},
             {grip_, "/user/hand/left/input/grip/pose"}, {grip_, "/user/hand/right/input/grip/pose"},
             {haptic_, "/user/hand/left/output/haptic"}, {haptic_, "/user/hand/right/output/haptic"}});
    for (int h = 0; h < 2; h++)
    {
        XrActionSpaceCreateInfo ci{XR_TYPE_ACTION_SPACE_CREATE_INFO};
        ci.subactionPath = hands_[h];
        ci.poseInActionSpace.orientation.w = 1.f;
        ci.action = aim_;
        xrCreateActionSpace(session, &ci, &aimSpace_[h]);
        ci.action = grip_;
        xrCreateActionSpace(session, &ci, &gripSpace_[h]);
    }
    return true;
}

void Controllers::Sync(XrSpace base, XrTime time, bool focused)
{
    for (auto& c : ctl) c.primaryPressed = c.secondaryPressed = c.triggerPressed = false;
    if (!actionSet || !focused) { for (auto& c : ctl) c.active = false; return; }
    XrActiveActionSet as{actionSet, XR_NULL_PATH};
    XrActionsSyncInfo si{XR_TYPE_ACTIONS_SYNC_INFO};
    si.countActiveActionSets = 1;
    si.activeActionSets = &as;
    if (XR_FAILED(xrSyncActions(session_, &si))) return;
    if (profilesDirty_)
    {
        for (int h = 0; h < 2; h++)
        {
            XrInteractionProfileState ps{XR_TYPE_INTERACTION_PROFILE_STATE};
            profile[h].clear();
            if (XR_SUCCEEDED(xrGetCurrentInteractionProfile(session_, hands_[h], &ps)) && ps.interactionProfile != XR_NULL_PATH)
            {
                char buf[XR_MAX_PATH_LENGTH];
                uint32_t n = 0;
                if (XR_SUCCEEDED(xrPathToString(inst_, ps.interactionProfile, sizeof buf, &n, buf))) profile[h] = buf;
            }
        }
        profilesDirty_ = false;
    }
    for (int h = 0; h < 2; h++)
    {
        Controller& c = ctl[h];
        XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
        gi.subactionPath = hands_[h];
        XrActionStateFloat f{XR_TYPE_ACTION_STATE_FLOAT};
        XrActionStateBoolean b{XR_TYPE_ACTION_STATE_BOOLEAN};
        XrActionStateVector2f v{XR_TYPE_ACTION_STATE_VECTOR2F};
        float oldTrigger = c.trigger;
        gi.action = trigger_; c.trigger = XR_SUCCEEDED(xrGetActionStateFloat(session_, &gi, &f)) && f.isActive ? f.currentState : 0.f;
        gi.action = squeeze_; c.squeeze = XR_SUCCEEDED(xrGetActionStateFloat(session_, &gi, &f)) && f.isActive ? f.currentState : 0.f;
        gi.action = primary_;
        if (XR_SUCCEEDED(xrGetActionStateBoolean(session_, &gi, &b))) { c.primaryPressed = b.currentState && !c.primary; c.primary = b.currentState; }
        gi.action = secondary_;
        if (XR_SUCCEEDED(xrGetActionStateBoolean(session_, &gi, &b))) { c.secondaryPressed = b.currentState && !c.secondary; c.secondary = b.currentState; }
        gi.action = stickClick_; c.stickClick = XR_SUCCEEDED(xrGetActionStateBoolean(session_, &gi, &b)) && b.currentState;
        gi.action = menu_; c.menu = XR_SUCCEEDED(xrGetActionStateBoolean(session_, &gi, &b)) && b.currentState;
        gi.action = stick_; c.stick = XR_SUCCEEDED(xrGetActionStateVector2f(session_, &gi, &v)) && v.isActive ? v.currentState : XrVector2f{0, 0};
        c.triggerPressed = c.trigger > 0.6f && oldTrigger <= 0.6f;
        XrActionStatePose p{XR_TYPE_ACTION_STATE_POSE};
        gi.action = aim_;
        c.active = XR_SUCCEEDED(xrGetActionStatePose(session_, &gi, &p)) && p.isActive;
        if (c.active)
        {
            XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
            if (XR_SUCCEEDED(xrLocateSpace(aimSpace_[h], base, time, &loc)) && (loc.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT)) c.aim = loc.pose;
            else c.active = false;
            loc = {XR_TYPE_SPACE_LOCATION};
            if (XR_SUCCEEDED(xrLocateSpace(gripSpace_[h], base, time, &loc)) && (loc.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT)) c.grip = loc.pose;
        }
    }
}

void Controllers::Haptic(int hand, float amplitude, float seconds)
{
    if (!haptic_) return;
    XrHapticVibration v{XR_TYPE_HAPTIC_VIBRATION};
    v.amplitude = amplitude;
    v.duration = (XrDuration)(seconds * 1e9);
    v.frequency = XR_FREQUENCY_UNSPECIFIED;
    XrHapticActionInfo hi{XR_TYPE_HAPTIC_ACTION_INFO};
    hi.action = haptic_;
    hi.subactionPath = hands_[hand & 1];
    xrApplyHapticFeedback(session_, &hi, (const XrHapticBaseHeader*)&v);
}

// ================================================================ scene and anchors
std::string UuidString(const XrUuidEXT& u)
{
    char s[40];
    for (int i = 0; i < 16; i++) snprintf(s + i * 2, 3, "%02x", u.data[i]);
    return s;
}

bool Spatial::Init(XrInstance inst, XrSession session, const Extensions& ext)
{
    inst_ = inst;
    session_ = session;
    query_ = Proc<PFN_xrQuerySpacesFB>(inst, "xrQuerySpacesFB");
    retrieve_ = Proc<PFN_xrRetrieveSpaceQueryResultsFB>(inst, "xrRetrieveSpaceQueryResultsFB");
    create_ = Proc<PFN_xrCreateSpatialAnchorFB>(inst, "xrCreateSpatialAnchorFB");
    setStatus_ = Proc<PFN_xrSetSpaceComponentStatusFB>(inst, "xrSetSpaceComponentStatusFB");
    getStatus_ = Proc<PFN_xrGetSpaceComponentStatusFB>(inst, "xrGetSpaceComponentStatusFB");
    save_ = Proc<PFN_xrSaveSpaceFB>(inst, "xrSaveSpaceFB");
    erase_ = Proc<PFN_xrEraseSpaceFB>(inst, "xrEraseSpaceFB");
    uuid_ = Proc<PFN_xrGetSpaceUuidFB>(inst, "xrGetSpaceUuidFB");
    labels_ = Proc<PFN_xrGetSpaceSemanticLabelsFB>(inst, "xrGetSpaceSemanticLabelsFB");
    box2d_ = Proc<PFN_xrGetSpaceBoundingBox2DFB>(inst, "xrGetSpaceBoundingBox2DFB");
    box3d_ = Proc<PFN_xrGetSpaceBoundingBox3DFB>(inst, "xrGetSpaceBoundingBox3DFB");
    sceneAvailable = query_ && retrieve_ && labels_ && ext.Has("XR_FB_scene");
    anchorsAvailable = create_ && setStatus_ && save_ && ext.Has("XR_FB_spatial_entity_storage");
    Log("spatial: scene %s, anchors %s", sceneAvailable ? "available" : "off", anchorsAvailable ? "available" : "off");
    return sceneAvailable || anchorsAvailable;
}

bool Spatial::QueryScene()
{
    if (!sceneAvailable) { lastError = "scene extensions not enabled"; return false; }
    XrSpaceComponentFilterInfoFB filter{XR_TYPE_SPACE_COMPONENT_FILTER_INFO_FB};
    filter.componentType = XR_SPACE_COMPONENT_TYPE_SEMANTIC_LABELS_FB;
    XrSpaceQueryInfoFB qi{XR_TYPE_SPACE_QUERY_INFO_FB};
    qi.queryAction = XR_SPACE_QUERY_ACTION_LOAD_FB;
    qi.maxResultCount = 256;
    qi.timeout = 0;
    qi.filter = (const XrSpaceFilterInfoBaseHeaderFB*)&filter;
    sceneDone = false;
    XrResult r = query_(session_, (const XrSpaceQueryInfoBaseHeaderFB*)&qi, &sceneReq_);
    if (XR_FAILED(r)) { lastError = "xrQuerySpacesFB " + std::to_string((int)r) + " (USE_SCENE granted?)"; Log("scene: %s", lastError.c_str()); return false; }
    Log("scene: query sent");
    return true;
}

bool Spatial::LoadAnchors()
{
    if (!anchorsAvailable || !query_) return false;
    XrSpaceStorageLocationFilterInfoFB filter{XR_TYPE_SPACE_STORAGE_LOCATION_FILTER_INFO_FB};
    filter.location = XR_SPACE_STORAGE_LOCATION_LOCAL_FB;
    XrSpaceQueryInfoFB qi{XR_TYPE_SPACE_QUERY_INFO_FB};
    qi.queryAction = XR_SPACE_QUERY_ACTION_LOAD_FB;
    qi.maxResultCount = 64;
    qi.filter = (const XrSpaceFilterInfoBaseHeaderFB*)&filter;
    return XR_SUCCEEDED(query_(session_, (const XrSpaceQueryInfoBaseHeaderFB*)&qi, &loadReq_));
}

bool Spatial::CreateAnchor(XrSpace base, const XrPosef& pose, XrTime time, bool persist)
{
    if (!create_) return false;
    XrSpatialAnchorCreateInfoFB ci{XR_TYPE_SPATIAL_ANCHOR_CREATE_INFO_FB};
    ci.space = base;
    ci.poseInSpace = pose;
    ci.time = time;
    XrAsyncRequestIdFB req = 0;
    if (XR_FAILED(create_(session_, &ci, &req))) return false;
    creating_.push_back({req, persist});
    return true;
}

bool Spatial::EraseAnchor(const Anchor& a)
{
    if (!erase_) return false;
    XrSpaceEraseInfoFB ei{XR_TYPE_SPACE_ERASE_INFO_FB};
    ei.space = a.space;
    ei.location = XR_SPACE_STORAGE_LOCATION_LOCAL_FB;
    XrAsyncRequestIdFB req = 0;
    return XR_SUCCEEDED(erase_(session_, &ei, &req));
}

static bool ComponentOn(PFN_xrGetSpaceComponentStatusFB get, XrSpace s, XrSpaceComponentTypeFB t)
{
    if (!get) return false;
    XrSpaceComponentStatusFB st{XR_TYPE_SPACE_COMPONENT_STATUS_FB};
    return XR_SUCCEEDED(get(s, t, &st)) && st.enabled && !st.changePending;
}

void Spatial::Fill(SceneObject& o)
{
    if (labels_)
    {
        XrSemanticLabelsFB sl{XR_TYPE_SEMANTIC_LABELS_FB};
        if (XR_SUCCEEDED(labels_(session_, o.space, &sl)) && sl.bufferCountOutput > 0)
        {
            std::string buf(sl.bufferCountOutput, '\0');
            sl.bufferCapacityInput = sl.bufferCountOutput;
            sl.buffer = &buf[0];
            if (XR_SUCCEEDED(labels_(session_, o.space, &sl))) o.labels = buf.c_str();
        }
    }
    if (box2d_ && ComponentOn(getStatus_, o.space, XR_SPACE_COMPONENT_TYPE_BOUNDED_2D_FB))
        o.hasPlane = XR_SUCCEEDED(box2d_(session_, o.space, &o.plane));
    if (box3d_ && ComponentOn(getStatus_, o.space, XR_SPACE_COMPONENT_TYPE_BOUNDED_3D_FB))
        o.hasVolume = XR_SUCCEEDED(box3d_(session_, o.space, &o.volume));
}

void Spatial::HandleEvent(const XrEventDataBuffer& ev)
{
    if (ev.type == XR_TYPE_EVENT_DATA_SPACE_QUERY_RESULTS_AVAILABLE_FB)
    {
        auto* e = (const XrEventDataSpaceQueryResultsAvailableFB*)&ev;
        XrSpaceQueryResultsFB res{XR_TYPE_SPACE_QUERY_RESULTS_FB};
        if (XR_FAILED(retrieve_(session_, e->requestId, &res))) return;
        std::vector<XrSpaceQueryResultFB> list(res.resultCountOutput);
        res.resultCapacityInput = (uint32_t)list.size();
        res.results = list.data();
        if (XR_FAILED(retrieve_(session_, e->requestId, &res))) return;
        if (e->requestId == sceneReq_)
        {
            for (auto& r : list)
            {
                SceneObject o;
                o.space = r.space;
                o.uuid = r.uuid;
                Fill(o);
                scene.push_back(o);
            }
            Log("scene: %zu objects", scene.size());
        }
        else if (e->requestId == loadReq_)
        {
            for (auto& r : list)
            {
                Anchor a;
                a.space = r.space;
                a.uuid = r.uuid;
                a.saved = true;
                anchors.push_back(a);
            }
            Log("anchors: %zu loaded", list.size());
        }
    }
    else if (ev.type == XR_TYPE_EVENT_DATA_SPACE_QUERY_COMPLETE_FB)
    {
        auto* e = (const XrEventDataSpaceQueryCompleteFB*)&ev;
        if (e->requestId == sceneReq_)
        {
            sceneDone = true;
            if (XR_FAILED(e->result)) lastError = "scene query " + std::to_string((int)e->result);
            Log("scene: query complete (%d), %zu objects", (int)e->result, scene.size());
        }
    }
    else if (ev.type == XR_TYPE_EVENT_DATA_SPATIAL_ANCHOR_CREATE_COMPLETE_FB)
    {
        auto* e = (const XrEventDataSpatialAnchorCreateCompleteFB*)&ev;
        for (size_t i = 0; i < creating_.size(); i++)
        {
            if (creating_[i].first != e->requestId) continue;
            bool persist = creating_[i].second;
            creating_.erase(creating_.begin() + (long)i);
            if (XR_FAILED(e->result)) { lastError = "anchor create " + std::to_string((int)e->result); Log("anchors: %s", lastError.c_str()); return; }
            Anchor a;
            a.space = e->space;
            a.uuid = e->uuid;
            anchors.push_back(a);
            Log("anchors: created %s", UuidString(a.uuid).c_str());
            if (persist && setStatus_)
            {
                XrSpaceComponentStatusSetInfoFB si{XR_TYPE_SPACE_COMPONENT_STATUS_SET_INFO_FB};
                si.componentType = XR_SPACE_COMPONENT_TYPE_STORABLE_FB;
                si.enabled = XR_TRUE;
                XrAsyncRequestIdFB req = 0;
                XrResult r = setStatus_(e->space, &si, &req);
                if (r == XR_ERROR_SPACE_COMPONENT_STATUS_ALREADY_SET_FB) r = XR_SUCCESS, req = 0;
                if (XR_SUCCEEDED(r) && req) { storable_.push_back(req); storableSpaces_.push_back(e->space); }
                else if (XR_SUCCEEDED(r))
                {
                    XrSpaceSaveInfoFB sv{XR_TYPE_SPACE_SAVE_INFO_FB};
                    sv.space = e->space;
                    sv.location = XR_SPACE_STORAGE_LOCATION_LOCAL_FB;
                    sv.persistenceMode = XR_SPACE_PERSISTENCE_MODE_INDEFINITE_FB;
                    XrAsyncRequestIdFB s2 = 0;
                    if (XR_SUCCEEDED(save_(session_, &sv, &s2))) saving_.push_back(s2);
                }
            }
            return;
        }
    }
    else if (ev.type == XR_TYPE_EVENT_DATA_SPACE_SET_STATUS_COMPLETE_FB)
    {
        auto* e = (const XrEventDataSpaceSetStatusCompleteFB*)&ev;
        for (size_t i = 0; i < storable_.size(); i++)
        {
            if (storable_[i] != e->requestId) continue;
            storable_.erase(storable_.begin() + (long)i);
            storableSpaces_.erase(storableSpaces_.begin() + (long)i);
            if (XR_FAILED(e->result)) { lastError = "storable " + std::to_string((int)e->result); return; }
            XrSpaceSaveInfoFB sv{XR_TYPE_SPACE_SAVE_INFO_FB};
            sv.space = e->space;
            sv.location = XR_SPACE_STORAGE_LOCATION_LOCAL_FB;
            sv.persistenceMode = XR_SPACE_PERSISTENCE_MODE_INDEFINITE_FB;
            XrAsyncRequestIdFB req = 0;
            if (XR_SUCCEEDED(save_(session_, &sv, &req))) saving_.push_back(req);
            return;
        }
    }
    else if (ev.type == XR_TYPE_EVENT_DATA_SPACE_SAVE_COMPLETE_FB)
    {
        auto* e = (const XrEventDataSpaceSaveCompleteFB*)&ev;
        for (auto& a : anchors)
            if (a.space == e->space) a.saved = XR_SUCCEEDED(e->result);
        Log("anchors: saved %s (%d)", UuidString(e->uuid).c_str(), (int)e->result);
    }
}

void Spatial::Locate(XrSpace base, XrTime time)
{
    for (auto& o : scene)
    {
        XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
        o.located = XR_SUCCEEDED(xrLocateSpace(o.space, base, time, &loc)) && (loc.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT);
        if (o.located) o.pose = loc.pose;
    }
    for (auto& a : anchors)
    {
        XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
        a.located = XR_SUCCEEDED(xrLocateSpace(a.space, base, time, &loc)) && (loc.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT);
        if (a.located) a.pose = loc.pose;
    }
}

// ================================================================ text
static const char* kTextVs = R"(#version 300 es
layout(location = 0) in vec2 pos;
layout(location = 1) in vec2 uv;
uniform mat4 mvp;
out vec2 vUv;
void main() { vUv = uv; gl_Position = mvp * vec4(pos, 0.0, 1.0); }
)";
static const char* kTextFs = R"(#version 300 es
precision mediump float;
in vec2 vUv;
uniform sampler2D atlas;
uniform vec4 color;
uniform int mode;
out vec4 o;
void main() { float a = mode == 1 ? texture(atlas, vUv).r : 1.0; o = vec4(color.rgb, color.a * a); }
)";

static GLuint Shader(GLenum t, const char* src)
{
    GLuint s = glCreateShader(t);
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint ok = 0;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) { char l[512]; glGetShaderInfoLog(s, sizeof l, nullptr, l); Log("text shader: %s", l); }
    return s;
}

bool Text::Init()
{
    prog_ = glCreateProgram();
    GLuint vs = Shader(GL_VERTEX_SHADER, kTextVs), fs = Shader(GL_FRAGMENT_SHADER, kTextFs);
    glAttachShader(prog_, vs);
    glAttachShader(prog_, fs);
    glLinkProgram(prog_);
    glDeleteShader(vs);
    glDeleteShader(fs);
    mvp_ = glGetUniformLocation(prog_, "mvp");
    color_ = glGetUniformLocation(prog_, "color");
    mode_ = glGetUniformLocation(prog_, "mode");
    glGenTextures(1, &tex_);
    glBindTexture(GL_TEXTURE_2D, tex_);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, MCX_FONT_W * MCX_FONT_COUNT, MCX_FONT_H, 0, GL_RED, GL_UNSIGNED_BYTE, kMcxFont);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glBindTexture(GL_TEXTURE_2D, 0);
    glGenVertexArrays(1, &vao_);
    glGenBuffers(1, &vbo_);
    glBindVertexArray(vao_);
    glBindBuffer(GL_ARRAY_BUFFER, vbo_);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 4 * sizeof(float), (void*)0);
    glEnableVertexAttribArray(1);
    glVertexAttribPointer(1, 2, GL_FLOAT, GL_FALSE, 4 * sizeof(float), (void*)(2 * sizeof(float)));
    glBindVertexArray(0);
    return true;
}

static void Quad(std::vector<float>& v, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1)
{
    float q[6][4] = {{x0, y0, u0, v1}, {x1, y0, u1, v1}, {x1, y1, u1, v0}, {x0, y0, u0, v1}, {x1, y1, u1, v0}, {x0, y1, u0, v0}};
    for (auto& p : q) v.insert(v.end(), p, p + 4);
}

static void DrawVerts(GLuint prog, GLint mvpLoc, GLint colorLoc, GLint modeLoc, GLuint vao, GLuint vbo, GLuint tex,
                      const std::vector<float>& v, const Mat4& mvp, XrColor4f c, int mode)
{
    if (v.empty()) return;
    glUseProgram(prog);
    glUniformMatrix4fv(mvpLoc, 1, GL_FALSE, mvp.m);
    glUniform4f(colorLoc, c.r, c.g, c.b, c.a);
    glUniform1i(modeLoc, mode);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, tex);
    glBindVertexArray(vao);
    glBindBuffer(GL_ARRAY_BUFFER, vbo);
    glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)(v.size() * sizeof(float)), v.data(), GL_STREAM_DRAW);
    glEnable(GL_BLEND);
    glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
    glDepthMask(GL_FALSE);
    glDrawArrays(GL_TRIANGLES, 0, (GLsizei)(v.size() / 4));
    glDepthMask(GL_TRUE);
    glDisable(GL_BLEND);
    glBindVertexArray(0);
}

float Text::Draw(const Mat4& viewProj, const Mat4& model, const char* s, float x, float y, float height, XrColor4f color)
{
    verts_.clear();
    float cw = height * MCX_FONT_W / MCX_FONT_H, cx = x, cy = y, maxX = x;
    const float atlasW = (float)(MCX_FONT_W * MCX_FONT_COUNT);
    for (const char* p = s; *p; p++)
    {
        if (*p == '\n') { cy -= height * 1.1f; cx = x; continue; }
        int c = (unsigned char)*p;
        if (c < MCX_FONT_FIRST || c >= MCX_FONT_FIRST + MCX_FONT_COUNT) c = '?';
        float u0 = (c - MCX_FONT_FIRST) * MCX_FONT_W / atlasW, u1 = u0 + MCX_FONT_W / atlasW;
        if (c != ' ') Quad(verts_, cx, cy - height, cx + cw, cy, u0, 0.f, u1, 1.f);
        cx += cw;
        if (cx > maxX) maxX = cx;
    }
    DrawVerts(prog_, mvp_, color_, mode_, vao_, vbo_, tex_, verts_, viewProj * model, color, 1);
    return maxX - x;
}

void Text::Rect(const Mat4& viewProj, const Mat4& model, float x, float y, float w, float h, XrColor4f color)
{
    verts_.clear();
    Quad(verts_, x, y - h, x + w, y, 0, 0, 0, 0);
    DrawVerts(prog_, mvp_, color_, mode_, vao_, vbo_, tex_, verts_, viewProj * model, color, 0);
}

void Text::Destroy()
{
    glDeleteBuffers(1, &vbo_);
    glDeleteVertexArrays(1, &vao_);
    glDeleteTextures(1, &tex_);
    glDeleteProgram(prog_);
}

// ================================================================ the app
static const char* kWanted[] = {
    "XR_EXT_hand_tracking", "XR_FB_hand_tracking_aim", "XR_FB_passthrough", "XR_FB_triangle_mesh",
    "XR_META_environment_depth", "XR_FB_display_refresh_rate", "XR_FB_color_space", "XR_FB_foveation",
    "XR_FB_foveation_configuration", "XR_FB_swapchain_update_state", "XR_EXT_performance_settings",
    "XR_FB_spatial_entity", "XR_FB_spatial_entity_query", "XR_FB_spatial_entity_storage", "XR_FB_spatial_entity_container",
    "XR_FB_scene", "XR_META_touch_controller_plus", "XR_FB_touch_controller_pro", "XR_KHR_composition_layer_depth"};

void App::OnCmd(android_app* a, int32_t cmd)
{
    auto* app = (App*)a->userData;
    if (cmd == APP_CMD_RESUME)
    {
        app->resumed_ = true;
        // reopened after a pause (Library, Home, the dock): NativeActivity does not pass the new intent to native code,
        // and MetaCodex's own launches always start a fresh activity (Back closes the app): so it is not MetaCodex's
        if (app->pausedOnce_ && app->fromMetaCodex_ && !app->leaving_) { app->fromMetaCodex_ = false; Log("reopened from outside MetaCodex: menu without MetaCodex"); }
    }
    else if (cmd == APP_CMD_PAUSE) { app->resumed_ = false; app->pausedOnce_ = true; }
    else if (cmd == APP_CMD_DESTROY) app->exit_ = true;
}

bool App::InitEgl()
{
    display_ = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    eglInitialize(display_, nullptr, nullptr);
    const EGLint cfg[] = {EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_RENDERABLE_TYPE, 0x40, EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE};
    EGLConfig config;
    EGLint n = 0;
    if (!eglChooseConfig(display_, cfg, &config, 1, &n) || n == 0) { Log("no EGL config"); return false; }
    const EGLint ctx32[] = {EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 2, EGL_NONE};
    context_ = eglCreateContext(display_, config, EGL_NO_CONTEXT, ctx32);
    if (context_ == EGL_NO_CONTEXT)
    {
        const EGLint ctx3[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
        context_ = eglCreateContext(display_, config, EGL_NO_CONTEXT, ctx3);
    }
    const EGLint pb[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE};
    surface_ = eglCreatePbufferSurface(display_, config, pb);
    if (context_ == EGL_NO_CONTEXT || !eglMakeCurrent(display_, surface_, surface_, context_)) { Log("EGL context failed"); return false; }
    return true;
}

bool App::Init(android_app* android, const Config& cfg)
{
    android_ = android;
    cfg_ = cfg;
    android->userData = this;
    android->onAppCmd = OnCmd;
    ANativeActivity* act = android->activity;
    JNIEnv* env = Env(act);
    {
        jclass cls = env->GetObjectClass(act->clazz);
        jmethodID m = env->GetMethodID(cls, "getPackageName", "()Ljava/lang/String;");
        jstring js = m ? (jstring)env->CallObjectMethod(act->clazz, m) : nullptr;
        if (js) { const char* c = env->GetStringUTFChars(js, nullptr); SetLogTag(c); env->ReleaseStringUTFChars(js, c); }
        if (env->ExceptionCheck()) env->ExceptionClear();
        env->DeleteLocalRef(cls);
    }
    Log("starting %s", cfg.name);
    ReadLaunch();
    // runtime permissions first: the dialog shows while the app starts; depth and scene start once granted.
    // HAND_TRACKING is install-time on Horizon OS (granted at install when the manifest declares it; docs/
    // native-build-and-install.md): the request is the fallback, and the log says which state this start had.
    std::vector<const char*> ask;
    bool handsOk = !cfg.hands || Permissions::Granted(act, kPermHands);
    if (cfg.askPermissions)
    {
        if ((cfg.depth || cfg.scene) && !Permissions::Granted(act, kPermScene)) ask.push_back(kPermScene);
        if (!handsOk) ask.push_back(kPermHands);
        if (!ask.empty()) Permissions::Request(act, ask);
    }
    if (cfg.hands) Log("permission %s: %s", kPermHands, handsOk ? "granted" : "not granted at start (hands wait for it)");

    PFN_xrInitializeLoaderKHR initLoader = Proc<PFN_xrInitializeLoaderKHR>(XR_NULL_HANDLE, "xrInitializeLoaderKHR");
    if (!initLoader) { Log("no OpenXR loader"); return false; }
    XrLoaderInitInfoAndroidKHR li{XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR};
    li.applicationVM = act->vm;
    li.applicationContext = act->clazz;
    if (!XRCHECK(initLoader((const XrLoaderInitInfoBaseHeaderKHR*)&li))) return false;
    uint32_t ne = 0;
    xrEnumerateInstanceExtensionProperties(nullptr, 0, &ne, nullptr);
    std::vector<XrExtensionProperties> props(ne, {XR_TYPE_EXTENSION_PROPERTIES});
    xrEnumerateInstanceExtensionProperties(nullptr, ne, &ne, props.data());
    for (auto& p : props) ext.available.push_back(p.extensionName);
    std::vector<std::string> want = {XR_KHR_ANDROID_CREATE_INSTANCE_EXTENSION_NAME, XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME};
    for (const char* e : kWanted) want.push_back(e);
    for (const char* e : cfg.extraExtensions) want.push_back(e);
    for (auto& e : want)
    {
        if (ext.Has(e.c_str())) continue;
        if (ext.Offered(e.c_str())) ext.enabled.push_back(e);
        else Log("extension %s: not offered (manifest entry or permission missing, or not on this device)", e.c_str());
    }
    std::vector<const char*> names;
    for (auto& e : ext.enabled) names.push_back(e.c_str());
    XrInstanceCreateInfoAndroidKHR aci{XR_TYPE_INSTANCE_CREATE_INFO_ANDROID_KHR};
    aci.applicationVM = act->vm;
    aci.applicationActivity = act->clazz;
    XrInstanceCreateInfo ci{XR_TYPE_INSTANCE_CREATE_INFO};
    ci.next = &aci;
    snprintf(ci.applicationInfo.applicationName, sizeof ci.applicationInfo.applicationName, "%s", cfg.name);
    ci.applicationInfo.applicationVersion = 1;
    snprintf(ci.applicationInfo.engineName, sizeof ci.applicationInfo.engineName, "MetaCodex mcx_xr");
    ci.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ci.enabledExtensionCount = (uint32_t)names.size();
    ci.enabledExtensionNames = names.data();
    if (!XRCHECK(xrCreateInstance(&ci, &instance))) return false;
    XrSystemGetInfo si{XR_TYPE_SYSTEM_GET_INFO};
    si.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    if (!XRCHECK(xrGetSystem(instance, &si, &system))) return false;
    auto req = Proc<PFN_xrGetOpenGLESGraphicsRequirementsKHR>(instance, "xrGetOpenGLESGraphicsRequirementsKHR");
    XrGraphicsRequirementsOpenGLESKHR gr{XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR};
    if (!req || !XRCHECK(req(instance, system, &gr))) return false;
    if (!InitEgl()) return false;
    XrGraphicsBindingOpenGLESAndroidKHR gb{XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR};
    gb.display = display_;
    gb.config = (EGLConfig)0;
    gb.context = context_;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &gb;
    sci.systemId = system;
    if (!XRCHECK(xrCreateSession(instance, &sci, &session))) return false;
    XrReferenceSpaceCreateInfo rs{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rs.poseInReferenceSpace.orientation.w = 1.f;
    if (!XRCHECK(xrCreateReferenceSpace(session, &rs, &local))) return false;
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;
    if (XR_FAILED(xrCreateReferenceSpace(session, &rs, &stage))) stage = XR_NULL_HANDLE;
    if (!InitSwapchains()) return false;
    text.Init();

    // features
    if (cfg.passthrough && ext.Has("XR_FB_passthrough") && passthrough.Init(instance, session)) { passthrough.Start(); projectionFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT; }
    if (cfg.hands && ext.Has("XR_EXT_hand_tracking")) hands.Init(instance, session, ext.Has("XR_FB_hand_tracking_aim"));
    if (cfg.hands && !ext.Has("XR_EXT_hand_tracking"))
        Log("hands: XR_EXT_hand_tracking not enabled: its manifest entries or the hand tracking permission were missing at start");
    if (cfg.hands && hands.Available() && !ext.Has("XR_FB_hand_tracking_aim"))
        Log("menu: the menu gesture needs XR_FB_hand_tracking_aim, not enabled here: the app menu opens with the left controller's button only");
    if (cfg.controllers && controllers.Init(instance, session, ext))
    {
        XrSessionActionSetsAttachInfo ai{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
        ai.countActionSets = 1;
        ai.actionSets = &controllers.actionSet;
        XRCHECK(xrAttachSessionActionSets(session, &ai));
    }
    if (cfg.scene || cfg.anchors) spatial.Init(instance, session, ext);
    if (cfg.depth) TryStartDepth();
    // display and performance
    if (ext.Has("XR_FB_display_refresh_rate"))
    {
        auto en = Proc<PFN_xrEnumerateDisplayRefreshRatesFB>(instance, "xrEnumerateDisplayRefreshRatesFB");
        auto get = Proc<PFN_xrGetDisplayRefreshRateFB>(instance, "xrGetDisplayRefreshRateFB");
        uint32_t n = 0;
        if (en && XR_SUCCEEDED(en(session, 0, &n, nullptr)) && n) { refreshRates.resize(n); en(session, n, &n, refreshRates.data()); }
        if (get) get(session, &refreshRate);
        if (cfg.refreshRate > 0) SetRefreshRate(cfg.refreshRate);
    }
    if (cfg.foveation >= 0) SetFoveation(cfg.foveation);
    if (cfg.cpuLevel >= 0 || cfg.gpuLevel >= 0) SetPerformance(cfg.cpuLevel, cfg.gpuLevel);
    std::string rates;
    for (float r : refreshRates) rates += std::to_string((int)r) + " ";
    Log("OpenXR ready: %dx%d per eye, %zu extensions, refresh %.0f Hz (offered: %s)", eyes_[0].w, eyes_[0].h, ext.enabled.size(), refreshRate, rates.c_str());
    t0_ = Now();
    return true;
}

// ================================================================ launch info, the way back, the menu, reports
static const char* kMetaCodex = "com.pnncl.metacodex";
static const char* kExCaller = "com.metacodex.extra.CALLER";
static const char* kExLaunch = "com.metacodex.extra.LAUNCH_ID";

static std::string JStr(JNIEnv* env, jstring s)
{
    if (!s) return "";
    const char* c = env->GetStringUTFChars(s, nullptr);
    std::string r = c ? c : "";
    env->ReleaseStringUTFChars(s, c);
    env->DeleteLocalRef(s);
    return r;
}

static bool Clear(JNIEnv* env)
{
    if (!env->ExceptionCheck()) return false;
    env->ExceptionClear();
    return true;
}

static jobject LaunchIntentFor(JNIEnv* env, jobject act, const char* pkg)
{
    jclass ac = env->GetObjectClass(act);
    jobject pm = env->CallObjectMethod(act, env->GetMethodID(ac, "getPackageManager", "()Landroid/content/pm/PackageManager;"));
    env->DeleteLocalRef(ac);
    if (Clear(env) || !pm) return nullptr;
    jclass pmc = env->GetObjectClass(pm);
    jstring jp = env->NewStringUTF(pkg);
    jobject it = env->CallObjectMethod(pm, env->GetMethodID(pmc, "getLaunchIntentForPackage", "(Ljava/lang/String;)Landroid/content/Intent;"), jp);
    if (Clear(env)) it = nullptr;
    env->DeleteLocalRef(jp);
    env->DeleteLocalRef(pmc);
    env->DeleteLocalRef(pm);
    return it;
}

void App::ReadLaunch()
{
    ANativeActivity* act = android_->activity;
    JNIEnv* env = Env(act);
    jclass ac = env->GetObjectClass(act->clazz);
    packageName = JStr(env, (jstring)env->CallObjectMethod(act->clazz, env->GetMethodID(ac, "getPackageName", "()Ljava/lang/String;")));
    Clear(env);
    jobject intent = env->CallObjectMethod(act->clazz, env->GetMethodID(ac, "getIntent", "()Landroid/content/Intent;"));
    if (!Clear(env) && intent)
    {
        jclass ic = env->GetObjectClass(intent);
        jmethodID get = env->GetMethodID(ic, "getStringExtra", "(Ljava/lang/String;)Ljava/lang/String;");
        jstring k1 = env->NewStringUTF(kExCaller), k2 = env->NewStringUTF(kExLaunch);
        caller = JStr(env, (jstring)env->CallObjectMethod(intent, get, k1));
        launchId = JStr(env, (jstring)env->CallObjectMethod(intent, get, k2));
        // tests (tools/return-test.sh): take the Back (or Report) path by itself after N seconds
        jstring k3 = env->NewStringUTF("com.metacodex.extra.TEST_RETURN_AFTER"), k4 = env->NewStringUTF("com.metacodex.extra.TEST_REPORT");
        std::string after = JStr(env, (jstring)env->CallObjectMethod(intent, get, k3));
        testReport_ = JStr(env, (jstring)env->CallObjectMethod(intent, get, k4)) == "1";
        testReturnAt_ = after.empty() ? -1.0 : atof(after.c_str());
        env->DeleteLocalRef(k3);
        env->DeleteLocalRef(k4);
        Clear(env);
        env->DeleteLocalRef(k1);
        env->DeleteLocalRef(k2);
        env->DeleteLocalRef(ic);
        env->DeleteLocalRef(intent);
    }
    // label and version (for reports and the menu)
    jobject pm = env->CallObjectMethod(act->clazz, env->GetMethodID(ac, "getPackageManager", "()Landroid/content/pm/PackageManager;"));
    jobject info = env->CallObjectMethod(act->clazz, env->GetMethodID(ac, "getApplicationInfo", "()Landroid/content/pm/ApplicationInfo;"));
    if (!Clear(env) && pm && info)
    {
        jclass pmc = env->GetObjectClass(pm);
        jobject lab = env->CallObjectMethod(pm, env->GetMethodID(pmc, "getApplicationLabel", "(Landroid/content/pm/ApplicationInfo;)Ljava/lang/CharSequence;"), info);
        if (!Clear(env) && lab)
        {
            jclass cs = env->GetObjectClass(lab);
            label = JStr(env, (jstring)env->CallObjectMethod(lab, env->GetMethodID(cs, "toString", "()Ljava/lang/String;")));
            env->DeleteLocalRef(cs);
            env->DeleteLocalRef(lab);
        }
        jstring jp = env->NewStringUTF(packageName.c_str());
        jobject pi = env->CallObjectMethod(pm, env->GetMethodID(pmc, "getPackageInfo", "(Ljava/lang/String;I)Landroid/content/pm/PackageInfo;"), jp, 0);
        if (!Clear(env) && pi)
        {
            jclass pic = env->GetObjectClass(pi);
            versionName = JStr(env, (jstring)env->GetObjectField(pi, env->GetFieldID(pic, "versionName", "Ljava/lang/String;")));
            versionCode = env->GetIntField(pi, env->GetFieldID(pic, "versionCode", "I"));
            Clear(env);
            env->DeleteLocalRef(pic);
            env->DeleteLocalRef(pi);
        }
        env->DeleteLocalRef(jp);
        env->DeleteLocalRef(pmc);
    }
    Clear(env);
    if (pm) env->DeleteLocalRef(pm);
    if (info) env->DeleteLocalRef(info);
    env->DeleteLocalRef(ac);
    jobject mc = LaunchIntentFor(env, act->clazz, kMetaCodex);
    metacodexInstalled_ = mc != nullptr;
    if (mc) env->DeleteLocalRef(mc);
    fromMetaCodex_ = metacodexInstalled_ && caller == kMetaCodex && !launchId.empty();
    Log("launched %s: %s %s (%lld); menu: left menu button / left-hand menu gesture", fromMetaCodex_ ? "from MetaCodex" : "from outside MetaCodex",
        label.c_str(), versionName.c_str(), versionCode);
}

// starts the caller (MetaCodex) while this app is still in front (Android allows it then), with the launch id and,
// for a report, the picture and the log; Android 14+: shares this app's identity so the receiver can check it
bool App::StartCaller(const std::vector<unsigned char>* png)
{
    ANativeActivity* act = android_->activity;
    JNIEnv* env = Env(act);
    std::string target = caller.empty() ? kMetaCodex : caller;
    jobject it = LaunchIntentFor(env, act->clazz, target.c_str());
    if (!it) { Log("back: %s is not installed", target.c_str()); return false; }
    jclass ic = env->GetObjectClass(it);
    env->CallObjectMethod(it, env->GetMethodID(ic, "addFlags", "(I)Landroid/content/Intent;"), 0x10000000 | 0x00020000); // NEW_TASK | REORDER_TO_FRONT
    jmethodID putS = env->GetMethodID(ic, "putExtra", "(Ljava/lang/String;Ljava/lang/String;)Landroid/content/Intent;");
    auto put = [&](const char* k, const std::string& v) {
        jstring jk = env->NewStringUTF(k), jv = env->NewStringUTF(v.c_str());
        env->CallObjectMethod(it, putS, jk, jv);
        env->DeleteLocalRef(jk);
        env->DeleteLocalRef(jv);
    };
    put("com.metacodex.extra.RETURN_FROM", packageName);
    put(kExLaunch, launchId);
    put("com.metacodex.extra.APP_LABEL", label);
    put("com.metacodex.extra.APP_VERSION", versionName + " (" + std::to_string(versionCode) + ")");
    if (png)
    {
        put("com.metacodex.extra.REPORT", "1");
        put("com.metacodex.extra.REPORT_LOG", RecentLog(60));
        jbyteArray arr = env->NewByteArray((jsize)png->size());
        env->SetByteArrayRegion(arr, 0, (jsize)png->size(), (const jbyte*)png->data());
        jstring jk = env->NewStringUTF("com.metacodex.extra.REPORT_PNG");
        env->CallObjectMethod(it, env->GetMethodID(ic, "putExtra", "(Ljava/lang/String;[B)Landroid/content/Intent;"), jk, arr);
        env->DeleteLocalRef(jk);
        env->DeleteLocalRef(arr);
    }
    Clear(env);
    jobject bundle = nullptr;
    jclass ver = env->FindClass("android/os/Build$VERSION");
    int sdk = ver ? env->GetStaticIntField(ver, env->GetStaticFieldID(ver, "SDK_INT", "I")) : 0;
    Clear(env);
    if (sdk >= 34)
    {
        jclass ao = env->FindClass("android/app/ActivityOptions");
        jobject opts = ao ? env->CallStaticObjectMethod(ao, env->GetStaticMethodID(ao, "makeBasic", "()Landroid/app/ActivityOptions;")) : nullptr;
        if (!Clear(env) && opts)
        {
            jmethodID share = env->GetMethodID(ao, "setShareIdentityEnabled", "(Z)Landroid/app/ActivityOptions;");
            if (!Clear(env) && share) env->CallObjectMethod(opts, share, JNI_TRUE);
            bundle = env->CallObjectMethod(opts, env->GetMethodID(ao, "toBundle", "()Landroid/os/Bundle;"));
            Clear(env);
        }
    }
    jclass ac = env->GetObjectClass(act->clazz);
    if (bundle) env->CallVoidMethod(act->clazz, env->GetMethodID(ac, "startActivity", "(Landroid/content/Intent;Landroid/os/Bundle;)V"), it, bundle);
    else env->CallVoidMethod(act->clazz, env->GetMethodID(ac, "startActivity", "(Landroid/content/Intent;)V"), it);
    bool failed = Clear(env);
    env->DeleteLocalRef(ac);
    env->DeleteLocalRef(ic);
    env->DeleteLocalRef(it);
    Log("back: %s %s%s", target.c_str(), failed ? "could not be started" : "started", png ? (" with a report (" + std::to_string(png->size() / 1024) + " KB picture)").c_str() : "");
    return !failed;
}

// Leaving (Back, Report, Quit, Restart): end the session and finish the activity at once, and from now on never call
// xrWaitFrame again. Otherwise, once the runtime gives the display to the app just started, xrWaitFrame does not
// return, the app thread stops pumping its looper, the activity's onPause (which waits for that thread in
// native_app_glue) times out and Android reports "isn't responding" (0.7.15, owner's Passthrough app, 11-11).
void App::Leave(const char* why)
{
    if (leaving_) return;
    leaving_ = true;
    leavingSince_ = Now();
    menuOpen_ = false;
    if (session && running_) xrRequestExitSession(session);
    ANativeActivity_finish(android_->activity);
    Log("leaving (%s): no more frames, the activity finishes", why);
}

bool App::ReturnToCaller(bool report)
{
    if (leaving_ || !fromMetaCodex_) return false;
    if (report) { menuOpen_ = false; reportFrames_ = 2; return true; } // a clean frame without the menu, then the picture
    if (!StartCaller(nullptr)) return false;
    returned_ = true;
    Leave("back");
    return true;
}

void App::Restart()
{
    ANativeActivity* act = android_->activity;
    JNIEnv* env = Env(act);
    jobject it = LaunchIntentFor(env, act->clazz, packageName.c_str());
    if (!it) return;
    jclass ic = env->GetObjectClass(it);
    env->CallObjectMethod(it, env->GetMethodID(ic, "addFlags", "(I)Landroid/content/Intent;"), 0x10000000 | 0x00008000); // NEW_TASK | CLEAR_TASK
    jclass ac = env->GetObjectClass(act->clazz);
    env->CallVoidMethod(act->clazz, env->GetMethodID(ac, "startActivity", "(Landroid/content/Intent;)V"), it);
    Clear(env);
    env->DeleteLocalRef(ac);
    env->DeleteLocalRef(ic);
    env->DeleteLocalRef(it);
    Log("restart");
    leaving_ = true; // CLEAR_TASK ends this activity: no more frames
    leavingSince_ = Now();
}

void App::Quit()
{
    Leave("quit");
}

void App::OpenMenu(bool open)
{
    if (open == menuOpen_) return;
    menuOpen_ = open;
    menuHover_ = -1;
    menuHand_ = -1;
    if (!open) return;
    // the menu stays where it opened: 0.9 m ahead of the head, at its height, turned to it
    XrPosef head = views_[0].pose;
    head.position.x = (views_[0].pose.position.x + views_[1].pose.position.x) * 0.5f;
    head.position.y = (views_[0].pose.position.y + views_[1].pose.position.y) * 0.5f;
    head.position.z = (views_[0].pose.position.z + views_[1].pose.position.z) * 0.5f;
    XrVector3f fwd = Forward(head);
    float len = sqrtf(fwd.x * fwd.x + fwd.z * fwd.z);
    if (len < 1e-3f) { fwd = {0, 0, -1}; len = 1; }
    fwd.x /= len;
    fwd.z /= len;
    float yaw = atan2f(-fwd.x, -fwd.z);
    menuPose_.position = {head.position.x + fwd.x * 0.9f, head.position.y - 0.05f, head.position.z + fwd.z * 0.9f};
    menuPose_.orientation = {0.f, sinf(yaw * 0.5f), 0.f, cosf(yaw * 0.5f)};
    menuShown_.clear();
    if (fromMetaCodex_)
    {
        menuShown_.push_back({"Back to MetaCodex", [this] { ReturnToCaller(false); }});
        menuShown_.push_back({"Report to MetaCodex", [this] { ReturnToCaller(true); }});
    }
    for (auto& it : menuItems) menuShown_.push_back(it);
    menuShown_.push_back({"Quit", [this] { Quit(); }});
    menuShown_.push_back({"Close menu", [this] { OpenMenu(false); }});
    Log("menu opened");
}

static const float kRowH = 0.075f, kMenuW = 0.56f;

void App::MenuInput(const Frame& f)
{
    // the left controller's menu button, or the menu gesture of either hand (the aim state's MENU_PRESSED bit, one
    // frame; it exists only with XR_FB_hand_tracking_aim). The system's own gestures are not the app's.
    bool l = controllers.ctl[0].menu, h = hands.hand[0].menuGesture || hands.hand[1].menuGesture;
    bool press = (l && !menuPrevL_) || (h && !menuPrevH_);
    menuPrevL_ = l;
    menuPrevH_ = h;
    if (press && Focused())
    {
        OpenMenu(!menuOpen_);
        return; // a pinch that came with the gesture must not select on the same frame
    }
    if (!menuOpen_ || !Focused()) return;
    // pointer: the right controller; else the hand that points (the one pinching, else the one that pointed last, else
    // any hand with an aim); else the left controller
    int pointer = -1;
    for (int k = 1; k >= 0 && pointer < 0; k--)
        if (hands.hand[k].aimValid && hands.hand[k].pinching) pointer = k;
    if (pointer < 0 && menuHand_ >= 0 && hands.hand[menuHand_].aimValid) pointer = menuHand_;
    for (int k = 1; k >= 0 && pointer < 0; k--)
        if (hands.hand[k].aimValid) pointer = k;
    menuHand_ = pointer;
    XrPosef ray;
    bool have = true;
    if (controllers.ctl[1].active) ray = controllers.ctl[1].aim;
    else if (pointer >= 0) ray = hands.hand[pointer].aim;
    else if (controllers.ctl[0].active) ray = controllers.ctl[0].aim;
    else have = false;
    menuRayHit_ = false;
    int rows = (int)menuShown_.size();
    float top = rows * kRowH * 0.5f + 0.05f;
    if (have)
    {
        XrVector3f n = Rotate(menuPose_.orientation, {0, 0, 1}), d = Forward(ray);
        float den = n.x * d.x + n.y * d.y + n.z * d.z;
        XrVector3f o{menuPose_.position.x - ray.position.x, menuPose_.position.y - ray.position.y, menuPose_.position.z - ray.position.z};
        float t = fabsf(den) > 1e-4f ? (n.x * o.x + n.y * o.y + n.z * o.z) / den : -1.f;
        if (t > 0)
        {
            XrVector3f p{ray.position.x + d.x * t, ray.position.y + d.y * t, ray.position.z + d.z * t};
            XrVector3f rel{p.x - menuPose_.position.x, p.y - menuPose_.position.y, p.z - menuPose_.position.z};
            XrQuaternionf inv{-menuPose_.orientation.x, -menuPose_.orientation.y, -menuPose_.orientation.z, menuPose_.orientation.w};
            XrVector3f lp = Rotate(inv, rel);
            menuRayHit_ = true;
            menuRayPoint_ = lp;
            int row = (int)floorf((top - 0.06f - lp.y) / kRowH);
            menuHover_ = (fabsf(lp.x) <= kMenuW * 0.5f && row >= 0 && row < rows) ? row : -1;
        }
    }
    if (have && !menuRayHit_) menuHover_ = -1; // the ray points away from the menu: no row is under it any more
    // controllers without pointing: the stick moves, A selects
    const Controller& cr = controllers.ctl[1];
    static float stickHold = 0.f;
    if (fabsf(cr.stick.y) > 0.6f)
    {
        if (stickHold <= 0.f) { menuHover_ = (menuHover_ < 0 ? 0 : menuHover_ + (cr.stick.y < 0 ? 1 : -1) + rows) % rows; stickHold = 0.25f; }
        stickHold -= f.dt;
    }
    else stickHold = 0.f;
    // select: a fresh pinch of either hand (pinchStarted is the frame the pinch starts; never with the system gesture)
    auto pinch = [this](int k) { return hands.hand[k].pinchStarted && !hands.hand[k].systemGesture; };
    bool select = cr.triggerPressed || cr.primaryPressed || controllers.ctl[0].triggerPressed || pinch(0) || pinch(1);
    if (select && menuHover_ >= 0 && menuHover_ < rows)
    {
        auto action = menuShown_[menuHover_].action;
        Log("menu: %s", menuShown_[menuHover_].label.c_str());
        if (cr.active) controllers.Haptic(1, 0.3f, 0.03f);
        OpenMenu(false);
        if (action) action();
    }
}

void App::DrawMenu(int e)
{
    if (!menuOpen_) return;
    Mat4 model = Mat4::FromPose(menuPose_);
    int rows = (int)menuShown_.size();
    float top = rows * kRowH * 0.5f + 0.05f;
    glDisable(GL_DEPTH_TEST);
    text.Rect(eyeViewProj_[e], model, -kMenuW * 0.5f - 0.02f, top + 0.01f, kMenuW + 0.04f, rows * kRowH + 0.09f, XrColor4f{0.03f, 0.04f, 0.06f, 0.92f});
    std::string title = label.empty() ? "App" : label;
    if (!versionName.empty()) title += "  " + versionName;
    title += fromMetaCodex_ ? "  - opened from MetaCodex" : "  - opened outside MetaCodex";
    text.Draw(eyeViewProj_[e], model, title.c_str(), -kMenuW * 0.5f + 0.01f, top, 0.03f, XrColor4f{0.6f, 0.7f, 0.8f, 1.f});
    for (int i = 0; i < rows; i++)
    {
        float y = top - 0.06f - i * kRowH;
        bool hov = i == menuHover_;
        text.Rect(eyeViewProj_[e], model, -kMenuW * 0.5f, y, kMenuW, kRowH - 0.01f, hov ? XrColor4f{0.18f, 0.42f, 0.75f, 0.95f} : XrColor4f{0.1f, 0.12f, 0.16f, 0.9f});
        text.Draw(eyeViewProj_[e], model, menuShown_[i].label.c_str(), -kMenuW * 0.5f + 0.025f, y - 0.018f, 0.036f, XrColor4f{0.95f, 0.97f, 1.f, 1.f});
    }
    if (menuRayHit_) text.Rect(eyeViewProj_[e], model, menuRayPoint_.x - 0.006f, menuRayPoint_.y + 0.006f, 0.012f, 0.012f, XrColor4f{1.f, 1.f, 1.f, 1.f});
}

// the eye image as a PNG (RGB, downscaled until it is under ~450 KB: it travels inside an Intent)
bool App::CaptureEye(int e, std::vector<unsigned char>& png, int& ow, int& oh)
{
    const Swap& s = eyes_[e];
    std::vector<unsigned char> rgba((size_t)s.w * s.h * 4);
    glBindFramebuffer(GL_FRAMEBUFFER, fbo_);
    glPixelStorei(GL_PACK_ALIGNMENT, 1);
    glReadPixels(0, 0, s.w, s.h, GL_RGBA, GL_UNSIGNED_BYTE, rgba.data());
    if (glGetError() != GL_NO_ERROR) { Log("report: the picture could not be read"); return false; }
    for (int scale = 3; scale <= 8; scale++)
    {
        int w = s.w / scale, h = s.h / scale;
        std::vector<unsigned char> raw;
        raw.reserve((size_t)(w * 3 + 1) * h);
        for (int y = 0; y < h; y++)
        {
            raw.push_back(0); // filter: none
            int sy = (h - 1 - y) * scale; // GL rows go up
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    unsigned sum = 0;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++) sum += rgba[((size_t)(sy + dy) * s.w + (x * scale + dx)) * 4 + c];
                    raw.push_back((unsigned char)(sum / (scale * scale)));
                }
        }
        uLongf zlen = compressBound((uLong)raw.size());
        std::vector<unsigned char> z(zlen);
        if (compress2(z.data(), &zlen, raw.data(), (uLong)raw.size(), 6) != Z_OK) return false;
        z.resize(zlen);
        png.clear();
        auto u32 = [&](uint32_t v) { for (int i = 3; i >= 0; i--) png.push_back((unsigned char)(v >> (i * 8))); };
        auto chunk = [&](const char* type, const unsigned char* d, size_t n) {
            u32((uint32_t)n);
            size_t at = png.size();
            png.insert(png.end(), type, type + 4);
            png.insert(png.end(), d, d + n);
            u32((uint32_t)crc32(0, png.data() + at, (uInt)(n + 4)));
        };
        static const unsigned char sig[8] = {0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a};
        png.insert(png.end(), sig, sig + 8);
        unsigned char ihdr[13] = {(unsigned char)(w >> 24), (unsigned char)(w >> 16), (unsigned char)(w >> 8), (unsigned char)w,
                                  (unsigned char)(h >> 24), (unsigned char)(h >> 16), (unsigned char)(h >> 8), (unsigned char)h, 8, 2, 0, 0, 0};
        chunk("IHDR", ihdr, 13);
        chunk("IDAT", z.data(), z.size());
        chunk("IEND", nullptr, 0);
        ow = w;
        oh = h;
        if (png.size() <= 450 * 1024) return true;
    }
    return png.size() <= 900 * 1024;
}

void App::TryStartDepth()
{
    if (depth.Running() || !ext.Has("XR_META_environment_depth")) return;
    if (!Permissions::Granted(android_->activity, kPermScene)) { if (!depthAsked_) Log("depth: waiting for the spatial data permission (USE_SCENE)"); depthAsked_ = true; return; }
    if (depth.Init(instance, session)) { depth.SetHandRemoval(true); depth.Start(); }
}

bool App::InitSwapchains()
{
    uint32_t nv = 0;
    if (!XRCHECK(xrEnumerateViewConfigurationViews(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &nv, nullptr))) return false;
    std::vector<XrViewConfigurationView> views(nv, {XR_TYPE_VIEW_CONFIGURATION_VIEW});
    xrEnumerateViewConfigurationViews(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, nv, &nv, views.data());
    uint32_t nf = 0;
    xrEnumerateSwapchainFormats(session, 0, &nf, nullptr);
    std::vector<int64_t> formats(nf);
    xrEnumerateSwapchainFormats(session, nf, &nf, formats.data());
    int64_t format = formats.empty() ? GL_RGBA8 : formats[0];
    for (int64_t f : formats)
        if (f == GL_SRGB8_ALPHA8) format = f;
    for (uint32_t e = 0; e < 2 && e < nv; e++)
    {
        Swap& s = eyes_[e];
        s.w = (int32_t)views[e].recommendedImageRectWidth;
        s.h = (int32_t)views[e].recommendedImageRectHeight;
        XrSwapchainCreateInfoFoveationFB fov{XR_TYPE_SWAPCHAIN_CREATE_INFO_FOVEATION_FB};
        XrSwapchainCreateInfo sc{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        if (ext.Has("XR_FB_foveation")) sc.next = &fov;
        sc.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        sc.format = format;
        sc.sampleCount = 1;
        sc.width = (uint32_t)s.w;
        sc.height = (uint32_t)s.h;
        sc.faceCount = 1;
        sc.arraySize = 1;
        sc.mipCount = 1;
        if (!XRCHECK(xrCreateSwapchain(session, &sc, &s.chain))) return false;
        uint32_t ni = 0;
        xrEnumerateSwapchainImages(s.chain, 0, &ni, nullptr);
        s.images.assign(ni, {XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_ES_KHR});
        xrEnumerateSwapchainImages(s.chain, ni, &ni, (XrSwapchainImageBaseHeader*)s.images.data());
        glGenRenderbuffers(1, &s.depth);
        glBindRenderbuffer(GL_RENDERBUFFER, s.depth);
        glRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, s.w, s.h);
    }
    glGenFramebuffers(1, &fbo_);
    return true;
}

bool App::SetRefreshRate(float hz)
{
    auto set = Proc<PFN_xrRequestDisplayRefreshRateFB>(instance, "xrRequestDisplayRefreshRateFB");
    if (!set || XR_FAILED(set(session, hz))) return false;
    refreshRate = hz;
    Log("display: %.0f Hz requested", hz);
    return true;
}

bool App::SetFoveation(int level)
{
    auto create = Proc<PFN_xrCreateFoveationProfileFB>(instance, "xrCreateFoveationProfileFB");
    auto update = Proc<PFN_xrUpdateSwapchainFB>(instance, "xrUpdateSwapchainFB");
    if (!create || !update || !ext.Has("XR_FB_foveation_configuration")) return false;
    XrFoveationLevelProfileCreateInfoFB lp{XR_TYPE_FOVEATION_LEVEL_PROFILE_CREATE_INFO_FB};
    lp.level = (XrFoveationLevelFB)(level < 0 ? 0 : level > 3 ? 3 : level);
    lp.verticalOffset = 0.f;
    lp.dynamic = XR_FOVEATION_DYNAMIC_DISABLED_FB;
    XrFoveationProfileCreateInfoFB pc{XR_TYPE_FOVEATION_PROFILE_CREATE_INFO_FB};
    pc.next = &lp;
    if (foveation_) { auto d = Proc<PFN_xrDestroyFoveationProfileFB>(instance, "xrDestroyFoveationProfileFB"); if (d) d(foveation_); foveation_ = XR_NULL_HANDLE; }
    if (XR_FAILED(create(session, &pc, &foveation_))) return false;
    XrSwapchainStateFoveationFB st{XR_TYPE_SWAPCHAIN_STATE_FOVEATION_FB};
    st.profile = foveation_;
    bool ok = true;
    for (auto& s : eyes_) ok &= XR_SUCCEEDED(update(s.chain, (XrSwapchainStateBaseHeaderFB*)&st));
    Log("display: foveation level %d %s", level, ok ? "set" : "failed");
    return ok;
}

bool App::SetPerformance(int cpu, int gpu)
{
    auto set = Proc<PFN_xrPerfSettingsSetPerformanceLevelEXT>(instance, "xrPerfSettingsSetPerformanceLevelEXT");
    if (!set) return false;
    static const XrPerfSettingsLevelEXT lv[4] = {XR_PERF_SETTINGS_LEVEL_POWER_SAVINGS_EXT, XR_PERF_SETTINGS_LEVEL_SUSTAINED_LOW_EXT,
                                                 XR_PERF_SETTINGS_LEVEL_SUSTAINED_HIGH_EXT, XR_PERF_SETTINGS_LEVEL_BOOST_EXT};
    bool ok = true;
    if (cpu >= 0) ok &= XR_SUCCEEDED(set(session, XR_PERF_SETTINGS_DOMAIN_CPU_EXT, lv[cpu > 3 ? 3 : cpu]));
    if (gpu >= 0) ok &= XR_SUCCEEDED(set(session, XR_PERF_SETTINGS_DOMAIN_GPU_EXT, lv[gpu > 3 ? 3 : gpu]));
    Log("performance: cpu %d gpu %d %s", cpu, gpu, ok ? "set" : "failed");
    return ok;
}

void App::HandleEvent(const XrEventDataBuffer& ev)
{
    if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED)
    {
        auto* sc = (const XrEventDataSessionStateChanged*)&ev;
        state_ = sc->state;
        Log("session state %d", (int)state_);
        if (state_ == XR_SESSION_STATE_READY)
        {
            XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO};
            bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
            running_ = XR_SUCCEEDED(xrBeginSession(session, &bi));
            Log("session running");
        }
        else if (state_ == XR_SESSION_STATE_STOPPING) { xrEndSession(session); running_ = false; }
        else if (state_ == XR_SESSION_STATE_EXITING || state_ == XR_SESSION_STATE_LOSS_PENDING)
        {
            // quit from the system menu: try to bring the launching app back (Android may refuse a start now)
            if (!returned_ && !leaving_ && fromMetaCodex_) { returned_ = true; Log("quit: trying to return to %s", caller.c_str()); StartCaller(nullptr); }
            exit_ = true;
        }
    }
    else if (ev.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) exit_ = true;
    else if (ev.type == XR_TYPE_EVENT_DATA_INTERACTION_PROFILE_CHANGED) controllers.profilesDirty_ = true;
    else if (ev.type == XR_TYPE_EVENT_DATA_DISPLAY_REFRESH_RATE_CHANGED_FB)
        refreshRate = ((const XrEventDataDisplayRefreshRateChangedFB*)&ev)->toDisplayRefreshRate;
    spatial.HandleEvent(ev);
    if (onEvent) onEvent(ev);
}

bool App::Pump()
{
    android_app* a = android_;
    int events = 0;
    android_poll_source* source = nullptr;
    // always drain the looper (lifecycle commands and input, focus events included: an unconsumed one is an ANR);
    // wait a little only when no frame will be rendered
    bool frames = running_ && resumed_ && !leaving_;
    while (ALooper_pollOnce(frames ? 0 : 16, nullptr, &events, (void**)&source) >= 0)
    {
        if (source) source->process(a, source);
        if (a->destroyRequested) return false;
    }
    if (a->destroyRequested || exit_) return false;
    if (leaving_ && Now() - leavingSince_ > 3.0) { Log("leaving: the activity did not end in 3 s: closing"); return false; }
    XrEventDataBuffer ev{XR_TYPE_EVENT_DATA_BUFFER};
    while (xrPollEvent(instance, &ev) == XR_SUCCESS)
    {
        HandleEvent(ev);
        ev = {XR_TYPE_EVENT_DATA_BUFFER};
    }
    // a permission granted while running (the dialog, or Settings): start what waited for it
    double now = Now();
    if (cfg_.depth && !depth.Running() && now - permCheck_ > 1.0) { permCheck_ = now; TryStartDepth(); }
    // hands that waited for the permission: the trackers are made once it is granted (checked once a second, 10 tries)
    if (cfg_.hands && session != XR_NULL_HANDLE && !hands.Available() && ext.Has("XR_EXT_hand_tracking") && handsTries_ < 10 &&
        now - handsCheck_ > 1.0 &&
        Permissions::Granted(android_->activity, kPermHands))
    {
        handsCheck_ = now;
        handsTries_++;
        hands.Destroy();
        if (hands.Init(instance, session, ext.Has("XR_FB_hand_tracking_aim"))) Log("hands: started, the permission is granted now");
    }
    return !exit_;
}

bool App::BeginFrame(Frame& f)
{
    frameBegun_ = false;
    // no frame while leaving or while the activity is paused (dock, Home): xrWaitFrame may then not return, and a
    // blocked app thread blocks onPause/onStop on the main thread (native_app_glue waits for it)
    if (!running_ || leaving_ || !resumed_) return false;
    XrFrameState fs{XR_TYPE_FRAME_STATE};
    if (XR_FAILED(xrWaitFrame(session, nullptr, &fs))) return false;
    xrBeginFrame(session, nullptr);
    frameBegun_ = true;
    time_ = fs.predictedDisplayTime;
    shouldRender_ = fs.shouldRender == XR_TRUE;
    double now = Now();
    f.time = time_;
    f.seconds = now - t0_;
    f.dt = last_ > 0 ? (float)(now - last_) : 0.f;
    last_ = now;
    controllers.Sync(local, time_, Focused());
    if (hands.Available()) hands.Update(local, time_);
    if (testReturnAt_ > 0 && f.seconds > testReturnAt_ && Focused()) { testReturnAt_ = -1; Log("test: return%s", testReport_ ? " with a report" : ""); ReturnToCaller(testReport_); }
    MenuInput(f);
    f.focused = Focused() && !menuOpen_; // while the menu is open the app gets no input
    spatial.Locate(local, time_);
    if (depth.Running()) depth.Acquire(local, time_, f.depth);
    XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO};
    li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    li.displayTime = time_;
    li.space = local;
    XrViewState vs{XR_TYPE_VIEW_STATE};
    views_[0] = {XR_TYPE_VIEW};
    views_[1] = {XR_TYPE_VIEW};
    uint32_t nv = 0;
    if (!shouldRender_ || XR_FAILED(xrLocateViews(session, &li, &vs, 2, &nv, views_)) || nv != 2) { shouldRender_ = false; EndFrame(); return false; }
    f.views[0] = views_[0];
    f.views[1] = views_[1];
    return true;
}

void App::BeginEye(int e, Eye& eye)
{
    Swap& s = eyes_[e];
    uint32_t idx = 0;
    XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    xrAcquireSwapchainImage(s.chain, &ai, &idx);
    XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wi.timeout = XR_INFINITE_DURATION;
    xrWaitSwapchainImage(s.chain, &wi);
    glBindFramebuffer(GL_FRAMEBUFFER, fbo_);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, s.images[idx].image, 0);
    glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, s.depth);
    glViewport(0, 0, s.w, s.h);
    eye.index = e;
    eye.width = s.w;
    eye.height = s.h;
    eye.pose = views_[e].pose;
    eye.projection = Mat4::Projection(views_[e].fov, 0.05f, 100.f);
    eye.view = Mat4::ViewFromPose(views_[e].pose);
    eye.viewProj = eye.projection * eye.view;
    eyeViewProj_[e] = eye.viewProj;
    eye.framebuffer = fbo_;
    pv_[e] = {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW};
    pv_[e].pose = views_[e].pose;
    pv_[e].fov = views_[e].fov;
    pv_[e].subImage.swapchain = s.chain;
    pv_[e].subImage.imageRect = {{0, 0}, {s.w, s.h}};
}

void App::EndEye(int e)
{
    if (e == 0 && reportFrames_ == 0)
    {
        reportFrames_ = -1;
        std::vector<unsigned char> png;
        int w = 0, h = 0;
        double t0 = Now();
        bool ok = CaptureEye(0, png, w, h);
        Log("report: picture %dx%d, %zu KB in %.0f ms", w, h, png.size() / 1024, (Now() - t0) * 1000.0);
        if (StartCaller(ok ? &png : nullptr)) { returned_ = true; Leave("report"); }
    }
    else if (e == 1 && reportFrames_ > 0) reportFrames_--;
    glBindFramebuffer(GL_FRAMEBUFFER, fbo_);
    DrawMenu(e);
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    xrReleaseSwapchainImage(eyes_[e].chain, &ri);
}

void App::EndFrame()
{
    if (!frameBegun_) return;
    frameBegun_ = false;
    const XrCompositionLayerBaseHeader* layers[2];
    uint32_t n = 0;
    XrCompositionLayerProjection proj{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    if (passthrough.Running()) layers[n++] = passthrough.Layer();
    if (shouldRender_)
    {
        proj.layerFlags = projectionFlags;
        proj.space = local;
        proj.viewCount = 2;
        proj.views = pv_;
        layers[n++] = (const XrCompositionLayerBaseHeader*)&proj;
    }
    XrFrameEndInfo ei{XR_TYPE_FRAME_END_INFO};
    ei.displayTime = time_;
    ei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE; // Meta: opaque, passthrough comes from its layer
    ei.layerCount = n;
    ei.layers = layers;
    xrEndFrame(session, &ei);
}

void App::Shutdown()
{
    Log("exiting");
    depth.Destroy();
    passthrough.Destroy();
    hands.Destroy();
    text.Destroy();
    if (session && running_ && state_ == XR_SESSION_STATE_STOPPING) xrEndSession(session);
    if (session) xrDestroySession(session);
    if (instance) xrDestroyInstance(instance);
    session = XR_NULL_HANDLE;
    instance = XR_NULL_HANDLE;
    if (!leaving_) ANativeActivity_finish(android_->activity);
    // stay in the loop until Android destroys the activity (returning from android_main earlier leaves the glue's
    // main-thread callbacks waiting on a thread that is gone)
    double t0 = Now();
    int events = 0;
    android_poll_source* source = nullptr;
    while (!android_->destroyRequested && Now() - t0 < 3.0)
        if (ALooper_pollOnce(16, nullptr, &events, (void**)&source) >= 0 && source) source->process(android_, source);
}

} // namespace mcx
