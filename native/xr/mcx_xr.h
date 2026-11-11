// mcx_xr.h: MetaCodex's native XR layer for Quest apps built in MetaCodex (C++17, OpenXR 1.0 + Meta extensions,
// OpenGL ES 3). mcx-build links it (libmcxxr.a, built once per toolchain): include it and call what you need.
//
//   mcx::Config cfg; cfg.passthrough = cfg.depth = cfg.hands = cfg.controllers = true;
//   mcx::App app;
//   if (!app.Init(android, cfg)) ...           // instance, session, spaces, swapchains, extensions, permissions
//   while (app.Pump()) {                       // Android + OpenXR events, session lifecycle
//       mcx::Frame f;
//       if (!app.BeginFrame(f)) continue;      // xrWaitFrame/xrBeginFrame, views, input, hands, depth
//       for (int e = 0; e < 2; e++) { mcx::Eye eye; app.BeginEye(e, eye); /* draw */ app.EndEye(e); }
//       app.EndFrame();                        // passthrough underlay + projection layer
//   }
//   app.Shutdown();
//
// Features (each also works alone on an XrInstance/XrSession: see the classes):
//   Passthrough  XR_FB_passthrough: start/pause, opacity, edges, mono->RGBA colour map, brightness/contrast/saturation
//   Depth        XR_META_environment_depth: the depth map as a GL texture array per frame (needs USE_SCENE granted)
//   Hands        XR_EXT_hand_tracking (+ XR_FB_hand_tracking_aim: pinch strengths, aim ray, menu gesture)
//   Controllers  OpenXR actions, Touch / Touch Plus / Touch Pro / simple profiles: trigger, grip, buttons, stick, haptics
//   Scene        XR_FB_scene + XR_FB_spatial_entity(_query): the room's walls, floor, furniture with labels and planes
//   Anchors      XR_FB_spatial_entity + _storage: create, persist, load
//   Display/perf XR_FB_display_refresh_rate, XR_FB_foveation(_configuration), XR_EXT_performance_settings
//   Permissions  Activity.checkSelfPermission / requestPermissions through JNI (polled: NativeActivity gets no result)
//   Text         mcx::Text: a monospaced font (DejaVu Sans Mono) and filled rectangles drawn in 3D
//   App menu     built in, always there: the left controller's menu button or the left hand's menu gesture opens
//                Back to MetaCodex / Report to MetaCodex / Restart / Quit (+ your items: App::menuItems).
//                "Back" starts MetaCodex again (warm: nothing reloads) and ends this app; "Report" sends MetaCodex
//                a picture of this app, its last log lines, name and version, which open as a draft in the thread
//                that launched it.
#pragma once
#include <android_native_app_glue.h>
#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <jni.h>
#ifndef XR_USE_PLATFORM_ANDROID
#define XR_USE_PLATFORM_ANDROID
#endif
#ifndef XR_USE_GRAPHICS_API_OPENGL_ES
#define XR_USE_GRAPHICS_API_OPENGL_ES
#endif
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <functional>
#include <string>
#include <vector>

namespace mcx {

// ---------------------------------------------------------------- maths (column-major, like OpenGL)
struct Mat4
{
    float m[16];
    static Mat4 Identity();
    static Mat4 Translation(float x, float y, float z);
    static Mat4 Scale(float x, float y, float z);
    static Mat4 FromPose(const XrPosef& p);              // pose -> matrix (local -> world)
    static Mat4 ViewFromPose(const XrPosef& p);          // inverse of a rigid pose (world -> eye)
    static Mat4 Projection(const XrFovf& fov, float nearZ, float farZ); // farZ <= 0 or inf: infinite far plane
    Mat4 operator*(const Mat4& b) const;
};
XrVector3f Rotate(const XrQuaternionf& q, const XrVector3f& v);
XrVector3f Forward(const XrPosef& p);                    // -Z of the pose

// logging: logcat (tag = package) and, when MetaCodex listens (mcx-app logs), 127.0.0.1:47950
void Log(const char* fmt, ...) __attribute__((format(printf, 1, 2)));
void SetLogTag(const char* tag);
std::string RecentLog(int maxLines = 40);               // the last lines Log() wrote (for reports)

// ---------------------------------------------------------------- permissions (JNI on the NativeActivity)
struct Permissions
{
    static bool Granted(ANativeActivity* act, const char* permission);
    // asks once per call; the system dialog shows while the app is in front. Poll Granted() afterwards.
    static void Request(ANativeActivity* act, const std::vector<const char*>& permissions);
};
constexpr const char* kPermScene = "com.oculus.permission.USE_SCENE";          // depth, scene (spatial data)
constexpr const char* kPermHands = "com.oculus.permission.HAND_TRACKING";
constexpr const char* kPermAnchors = "com.oculus.permission.USE_ANCHOR_API";

// ---------------------------------------------------------------- extensions
struct Extensions
{
    std::vector<std::string> available, enabled;
    bool Has(const char* name) const;                    // enabled
    bool Offered(const char* name) const;                // available in the runtime
};
// a function pointer from the instance (null when the extension is off)
template <class F> F Proc(XrInstance inst, const char* name)
{
    PFN_xrVoidFunction f = nullptr;
    if (XR_FAILED(xrGetInstanceProcAddr(inst, name, &f))) return nullptr;
    return (F)f;
}

// ---------------------------------------------------------------- passthrough
class Passthrough
{
public:
    bool Init(XrInstance inst, XrSession session);       // creates the feature + a reconstruction layer, paused
    void Start();
    void Pause();
    bool Running() const { return running_; }
    bool Available() const { return layer_ != XR_NULL_HANDLE; }
    // styles (one colour map at a time; opacity and edges combine with any)
    void SetStyle(float opacity, XrColor4f edge = {0, 0, 0, 0});
    void SetMonoToRgba(const std::function<XrColor4f(int)>& map, float opacity = 1.f, XrColor4f edge = {0, 0, 0, 0}); // 256 entries
    void SetBrightnessContrastSaturation(float brightness, float contrast, float saturation, float opacity = 1.f);
    const XrCompositionLayerBaseHeader* Layer();         // submit BEFORE the projection layer
    void Destroy();
private:
    XrInstance inst_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;
    XrPassthroughFB pt_ = XR_NULL_HANDLE;
    XrPassthroughLayerFB layer_ = XR_NULL_HANDLE;
    XrCompositionLayerPassthroughFB comp_{XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB};
    bool running_ = false;
    PFN_xrPassthroughStartFB start_ = nullptr;
    PFN_xrPassthroughPauseFB pause_ = nullptr;
    PFN_xrPassthroughLayerResumeFB resume_ = nullptr;
    PFN_xrPassthroughLayerPauseFB lpause_ = nullptr;
    PFN_xrPassthroughLayerSetStyleFB style_ = nullptr;
};

// ---------------------------------------------------------------- environment depth
struct DepthFrame
{
    bool valid = false;
    GLuint texture = 0;                                  // GL_TEXTURE_2D_ARRAY, 2 layers (left, right), depth in [0, 1]
    uint32_t width = 0, height = 0;
    float nearZ = 0.f, farZ = 0.f;                       // farZ may be +inf
    XrEnvironmentDepthImageViewMETA views[2];            // pose + fov of each depth view in the base space
    Mat4 viewProj[2];                                    // base space -> depth clip space, per layer
    XrTime captureTime = 0;
    // metres from a depth sample: OpenGL convention, infinite far plane when farZ is inf
    float Linear(float d) const;
};
class Depth
{
public:
    bool Init(XrInstance inst, XrSession session);       // needs USE_SCENE granted; false otherwise (retry later)
    bool Start();
    void Stop();
    bool Running() const { return running_; }
    void SetHandRemoval(bool on);                        // hides your own hands from the depth map
    bool Acquire(XrSpace base, XrTime displayTime, DepthFrame& out); // once per frame, between BeginFrame and EndFrame
    void Destroy();
    bool supportsHandRemoval = false;
private:
    XrInstance inst_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;
    XrEnvironmentDepthProviderMETA provider_ = XR_NULL_HANDLE;
    XrEnvironmentDepthSwapchainMETA swapchain_ = XR_NULL_HANDLE;
    std::vector<XrSwapchainImageOpenGLESKHR> images_;
    uint32_t w_ = 0, h_ = 0;
    bool running_ = false;
    PFN_xrAcquireEnvironmentDepthImageMETA acquire_ = nullptr;
};

// ---------------------------------------------------------------- hands
struct Hand
{
    bool tracked = false;
    XrHandJointLocationEXT joints[XR_HAND_JOINT_COUNT_EXT];
    // XR_FB_hand_tracking_aim (when enabled)
    bool aimValid = false;
    XrPosef aim{{0, 0, 0, 1}, {0, 0, 0}};
    float pinch[4] = {0, 0, 0, 0};                       // index, middle, ring, little: 0..1
    bool pinching = false, pinchStarted = false;         // index pinch (edge: this frame)
    bool menuGesture = false, systemGesture = false;
    XrPosef Joint(XrHandJointEXT j) const { return joints[j].pose; }
};
class Hands
{
public:
    bool Init(XrInstance inst, XrSession session, bool aim);
    void Update(XrSpace base, XrTime time);
    Hand hand[2];                                        // 0 left, 1 right
    bool Available() const { return trackers_[0] != XR_NULL_HANDLE; }
    void Destroy();
private:
    XrHandTrackerEXT trackers_[2] = {XR_NULL_HANDLE, XR_NULL_HANDLE};
    bool aim_ = false;
    PFN_xrLocateHandJointsEXT locate_ = nullptr;
    PFN_xrDestroyHandTrackerEXT destroy_ = nullptr;
};

// ---------------------------------------------------------------- controllers
struct Controller
{
    bool active = false;                                 // a controller pose is tracked
    XrPosef aim{{0, 0, 0, 1}, {0, 0, 0}}, grip{{0, 0, 0, 1}, {0, 0, 0}};
    float trigger = 0.f, squeeze = 0.f;
    XrVector2f stick{0, 0};
    bool primary = false, secondary = false, stickClick = false, menu = false; // A/X, B/Y, stick press, menu (left)
    bool primaryPressed = false, secondaryPressed = false, triggerPressed = false; // edges: this frame
};
class Controllers
{
public:
    // creates the action set and suggests bindings for the profiles the runtime knows; call before Attach
    bool Init(XrInstance inst, XrSession session, const Extensions& ext);
    XrActionSet actionSet = XR_NULL_HANDLE;              // attach it (App does) before the first Sync
    void Sync(XrSpace base, XrTime time, bool focused);
    void Haptic(int hand, float amplitude, float seconds);
    Controller ctl[2];
    std::string profile[2];                              // current interaction profile path per hand
private:
    XrInstance inst_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;
    XrPath hands_[2] = {XR_NULL_PATH, XR_NULL_PATH};
    XrAction trigger_ = XR_NULL_HANDLE, squeeze_ = XR_NULL_HANDLE, primary_ = XR_NULL_HANDLE, secondary_ = XR_NULL_HANDLE,
             stick_ = XR_NULL_HANDLE, stickClick_ = XR_NULL_HANDLE, menu_ = XR_NULL_HANDLE, aim_ = XR_NULL_HANDLE,
             grip_ = XR_NULL_HANDLE, haptic_ = XR_NULL_HANDLE;
    XrSpace aimSpace_[2] = {XR_NULL_HANDLE, XR_NULL_HANDLE}, gripSpace_[2] = {XR_NULL_HANDLE, XR_NULL_HANDLE};
    bool profilesDirty_ = true;
    friend class App;
};

// ---------------------------------------------------------------- scene (room) and anchors
struct SceneObject
{
    XrSpace space = XR_NULL_HANDLE;
    XrUuidEXT uuid{};
    std::string labels;                                  // e.g. "FLOOR", "WALL_FACE", "TABLE", comma-separated
    bool hasPlane = false;
    XrRect2Df plane{};                                   // in the object's space (x right, y up)
    bool hasVolume = false;
    XrRect3DfFB volume{};
    XrPosef pose{{0, 0, 0, 1}, {0, 0, 0}};               // in the base space at the last Locate
    bool located = false;
};
struct Anchor
{
    XrSpace space = XR_NULL_HANDLE;
    XrUuidEXT uuid{};
    bool saved = false;
    XrPosef pose{{0, 0, 0, 1}, {0, 0, 0}};
    bool located = false;
};
class Spatial
{
public:
    bool Init(XrInstance inst, XrSession session, const Extensions& ext);
    // the room: async; HandleEvent fills `scene`, `sceneDone` says the query finished (needs USE_SCENE granted and
    // a room set up in the headset's Space Setup)
    bool QueryScene();
    // anchors: async create + persist (XR_FB_spatial_entity_storage); LoadAnchors brings back saved ones
    bool CreateAnchor(XrSpace base, const XrPosef& pose, XrTime time, bool persist);
    bool LoadAnchors();
    bool EraseAnchor(const Anchor& a);
    void HandleEvent(const XrEventDataBuffer& ev);       // App forwards every event
    void Locate(XrSpace base, XrTime time);
    std::vector<SceneObject> scene;
    std::vector<Anchor> anchors;
    bool sceneDone = false, sceneAvailable = false, anchorsAvailable = false;
    std::string lastError;
private:
    void Fill(SceneObject& o);
    XrInstance inst_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;
    XrAsyncRequestIdFB sceneReq_ = 0, loadReq_ = 0;
    std::vector<std::pair<XrAsyncRequestIdFB, bool>> creating_; // request, persist
    std::vector<XrAsyncRequestIdFB> storable_, saving_;
    std::vector<XrSpace> storableSpaces_;
    PFN_xrQuerySpacesFB query_ = nullptr;
    PFN_xrRetrieveSpaceQueryResultsFB retrieve_ = nullptr;
    PFN_xrCreateSpatialAnchorFB create_ = nullptr;
    PFN_xrSetSpaceComponentStatusFB setStatus_ = nullptr;
    PFN_xrGetSpaceComponentStatusFB getStatus_ = nullptr;
    PFN_xrSaveSpaceFB save_ = nullptr;
    PFN_xrEraseSpaceFB erase_ = nullptr;
    PFN_xrGetSpaceUuidFB uuid_ = nullptr;
    PFN_xrGetSpaceSemanticLabelsFB labels_ = nullptr;
    PFN_xrGetSpaceBoundingBox2DFB box2d_ = nullptr;
    PFN_xrGetSpaceBoundingBox3DFB box3d_ = nullptr;
};
std::string UuidString(const XrUuidEXT& u);

// ---------------------------------------------------------------- text and rectangles (GL ES 3)
class Text
{
public:
    bool Init();
    // draws `s` (ASCII; '\n' breaks lines) on the XY plane of `model` (1 unit = 1 metre), top-left at (x, y),
    // characters `height` metres tall; returns the width drawn
    float Draw(const Mat4& viewProj, const Mat4& model, const char* s, float x, float y, float height, XrColor4f color);
    void Rect(const Mat4& viewProj, const Mat4& model, float x, float y, float w, float h, XrColor4f color);
    void Destroy();
private:
    GLuint prog_ = 0, tex_ = 0, vao_ = 0, vbo_ = 0;
    GLint mvp_ = -1, color_ = -1, mode_ = -1;
    std::vector<float> verts_;
};

// ---------------------------------------------------------------- the app
struct Config
{
    const char* name = "MetaCodex native app";
    bool passthrough = false;     // XR_FB_passthrough (manifest: com.oculus.feature.PASSTHROUGH)
    bool depth = false;           // XR_META_environment_depth (USE_SCENE, asked at start)
    bool hands = true;            // XR_EXT_hand_tracking + aim (manifest: HAND_TRACKING + oculus.software.handtracking)
    bool controllers = true;      // actions with the Touch profiles
    bool scene = false;           // XR_FB_scene + spatial entities (USE_SCENE)
    bool anchors = false;         // spatial anchors + storage (USE_ANCHOR_API)
    float refreshRate = 0.f;      // 0: the runtime's default; e.g. 90 or 120 (XR_FB_display_refresh_rate)
    int foveation = -1;           // -1 off; 0 none, 1 low, 2 medium, 3 high (XR_FB_foveation)
    int cpuLevel = -1, gpuLevel = -1; // -1 leave; 0 power savings, 1 sustained low, 2 sustained high, 3 boost
    bool askPermissions = true;   // request USE_SCENE / HAND_TRACKING when needed and not granted
    std::vector<const char*> extraExtensions;
};

struct Frame
{
    XrTime time = 0;              // predicted display time
    double seconds = 0.0;         // since Init
    float dt = 0.f;
    XrView views[2];
    bool focused = false;         // input focus (false while a system menu is open: pause gameplay, hide pointers)
    DepthFrame depth;             // valid when depth runs and a map was acquired
};

struct MenuItem
{
    std::string label;
    std::function<void()> action;
};

struct Eye
{
    int index = 0, width = 0, height = 0;
    Mat4 projection, view, viewProj;
    XrPosef pose;
    GLuint framebuffer = 0;
};

class App
{
public:
    bool Init(android_app* android, const Config& cfg);
    bool Pump();                                         // false when the app must exit
    bool BeginFrame(Frame& f);                           // false: nothing to render this frame (still call Pump)
    void BeginEye(int index, Eye& eye);                  // binds the eye's framebuffer (sRGB + depth), sets viewport
    void EndEye(int index);
    void EndFrame();
    void Shutdown();

    // state
    bool Focused() const { return state_ == XR_SESSION_STATE_FOCUSED; }
    bool Visible() const { return state_ == XR_SESSION_STATE_VISIBLE || state_ == XR_SESSION_STATE_FOCUSED; }
    bool Resumed() const { return resumed_; }
    XrSessionState State() const { return state_; }
    bool HasPermission(const char* p) const { return Permissions::Granted(android_->activity, p); }
    void RequestPermissions(const std::vector<const char*>& p) { Permissions::Request(android_->activity, p); }

    // projection layer over passthrough: the alpha you write shows the room where it is < 1 (set by App when
    // passthrough runs; set it yourself for other layer setups)
    XrCompositionLayerFlags projectionFlags = 0;
    float clearAlpha() const { return passthrough.Running() ? 0.f : 1.f; }

    // ---- the app menu and the way back (always there; apps add items, never remove the standard ones)
    std::vector<MenuItem> menuItems;                     // yours, listed above Back / Report / Restart / Quit
    bool MenuOpen() const { return menuOpen_; }
    void OpenMenu(bool open);
    // back to the app that launched this one (MetaCodex) or, started from the Library, open MetaCodex; then this app
    // ends. report = also send a picture of this app, its last log lines, label and version (taken next frame).
    bool ReturnToCaller(bool report = false);
    void Restart();                                      // start this app again (a fresh activity)
    void Quit();                                         // end the session: the app closes
    std::string caller;                                  // package that launched this app ("" from the Library)
    std::string launchId;                                // the launch's id (MetaCodex matches reports with it)
    std::string packageName, label, versionName;
    long long versionCode = 0;

    // display / performance
    std::vector<float> refreshRates;                     // offered by the display
    float refreshRate = 72.f;
    bool SetRefreshRate(float hz);
    bool SetFoveation(int level);                        // 0..3
    bool SetPerformance(int cpuLevel, int gpuLevel);

    XrInstance instance = XR_NULL_HANDLE;
    XrSystemId system = XR_NULL_SYSTEM_ID;
    XrSession session = XR_NULL_HANDLE;
    XrSpace local = XR_NULL_HANDLE, stage = XR_NULL_HANDLE;
    Extensions ext;
    Passthrough passthrough;
    Depth depth;
    Hands hands;
    Controllers controllers;
    Spatial spatial;
    Text text;
    android_app* android_ = nullptr;
    std::function<void(const XrEventDataBuffer&)> onEvent; // every OpenXR event, after App handled it

private:
    bool InitEgl();
    bool InitSwapchains();
    void HandleEvent(const XrEventDataBuffer& ev);
    void TryStartDepth();
    void ReadLaunch();
    void MenuInput(const Frame& f);
    void DrawMenu(int eye);
    bool CaptureEye(int eye, std::vector<unsigned char>& png, int& w, int& h);
    bool StartCaller(const std::vector<unsigned char>* png);
    Config cfg_;
    bool menuOpen_ = false, menuPrevL_ = false, menuPrevH_ = false, returned_ = false, metacodexInstalled_ = false;
    int menuHover_ = -1, reportFrames_ = -1;
    double testReturnAt_ = -1.0;
    bool testReport_ = false;
    XrPosef menuPose_{{0, 0, 0, 1}, {0, 0, -0.9f}};
    bool menuRayHit_ = false;
    XrVector3f menuRayPoint_{0, 0, 0};
    std::vector<MenuItem> menuShown_;
    Mat4 eyeViewProj_[2];
    EGLDisplay display_ = EGL_NO_DISPLAY;
    EGLContext context_ = EGL_NO_CONTEXT;
    EGLSurface surface_ = EGL_NO_SURFACE;
    XrSessionState state_ = XR_SESSION_STATE_UNKNOWN;
    bool running_ = false, exit_ = false, resumed_ = false, frameBegun_ = false, depthAsked_ = false;
    double t0_ = -1.0, last_ = 0.0, permCheck_ = 0.0;
    struct Swap
    {
        XrSwapchain chain = XR_NULL_HANDLE;
        int32_t w = 0, h = 0;
        std::vector<XrSwapchainImageOpenGLESKHR> images;
        GLuint depth = 0;
    } eyes_[2];
    GLuint fbo_ = 0;
    XrView views_[2];
    XrTime time_ = 0;
    bool shouldRender_ = false;
    XrCompositionLayerProjectionView pv_[2];
    XrFoveationProfileFB foveation_ = XR_NULL_HANDLE;
    static void OnCmd(android_app* app, int32_t cmd);
};

} // namespace mcx
