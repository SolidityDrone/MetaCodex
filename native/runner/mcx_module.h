// mcx_module.h: the interface between MetaCodex's native host (libmcxhost.so) and an app module (libapp.so).
//
// The host owns the Android activity, EGL (OpenGL ES 3.2), the OpenXR instance, session, spaces and swapchains,
// and the frame loop. The module owns the scene: it gets the instance, the session and xrGetInstanceProcAddr, so it
// can use any OpenXR extension the host enabled (see McxHost::hasExtension), and it draws each eye into a bound
// framebuffer. The same module runs in two ways (identical code):
//   * shipped: `mcx-app build` packs libmcxhost.so + libapp.so into the app's own APK (Library icon, no MetaCodex);
//   * dev:     `mcx-app dev` compiles libapp.so and the Runner app loads it while it runs (no APK, no install):
//              the old module's mcx_app_shutdown runs, the new one's mcx_app_init, the OpenXR session stays.
// A module keeps no state across a reload except what it saves itself (e.g. into host->dataPath).
// C linkage, plain structs: bump MCX_MODULE_ABI on any change; the host refuses a module built for another ABI.
#pragma once
#include <stdint.h>
#include <jni.h>
#include <android/asset_manager.h>
#include <EGL/egl.h>
#include <GLES3/gl32.h>
#include <openxr/openxr.h>

#define MCX_MODULE_ABI 1

#ifdef __cplusplus
extern "C" {
#endif

typedef struct McxHost
{
    uint32_t abi;            // MCX_MODULE_ABI of the host
    int dev;                 // 1 in the Runner (dev loop), 0 in a shipped APK
    JavaVM* vm;              // the activity's VM (attach your own threads)
    jobject activity;        // the NativeActivity (a global reference owned by the host)
    AAssetManager* assets;   // the APK's assets/ (the Runner's in dev mode)
    const char* dataPath;    // the app's private files folder (survives reloads and restarts)
    XrInstance instance;
    XrSystemId system;
    XrSession session;
    XrSpace localSpace;      // XR_REFERENCE_SPACE_TYPE_LOCAL (views are located in it)
    XrSpace stageSpace;      // XR_REFERENCE_SPACE_TYPE_STAGE, or XR_NULL_HANDLE
    PFN_xrGetInstanceProcAddr getProcAddr;
    int (*hasExtension)(const char* name);           // enabled at instance creation?
    void (*log)(const char* fmt, ...);               // logcat + `mcx-app logs`
    EGLDisplay eglDisplay;
    EGLContext eglContext;                           // current on the thread that calls the module
    float refreshRate;                               // Hz (XR_FB_display_refresh_rate when present)
} McxHost;

typedef struct McxFrame
{
    XrTime displayTime;      // predicted display time of this frame
    double seconds;          // since the module was initialised
    float dt;                // since the previous frame, seconds
    XrSessionState state;    // VISIBLE or FOCUSED
    XrView views[2];         // located in localSpace
} McxFrame;

// what the module may change about the frame it is in (filled with the defaults before mcx_app_frame)
typedef struct McxFrameOut
{
    // layers submitted BEFORE the projection layer (e.g. an XrCompositionLayerPassthroughFB); valid until mcx_app_draw ends
    const XrCompositionLayerBaseHeader* underlays[4];
    uint32_t underlayCount;
    XrCompositionLayerFlags projectionFlags;   // e.g. XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT for passthrough
    int skipProjection;                        // 1: submit no projection layer this frame
} McxFrameOut;

typedef struct McxEye
{
    int index;               // 0 left, 1 right
    int width, height;       // viewport (already set)
    uint32_t framebuffer;    // bound GL framebuffer: sRGB colour + 24-bit depth; not cleared: clear it yourself
    const XrView* view;      // pose and field of view of this eye
    float projection[16];    // column-major, near 0.05 m, far 100 m (OpenGL clip space)
    float viewMatrix[16];    // column-major, localSpace -> eye
} McxEye;

// the module's entry points (export them with MCX_EXPORT; mcx_app_draw is the only one required)
#define MCX_EXPORT __attribute__((visibility("default")))
typedef int (*McxInitFn)(const McxHost* host);                 // 0 = ok; GL context current
typedef void (*McxFrameFn)(const McxFrame* frame, McxFrameOut* out);
typedef void (*McxDrawFn)(const McxEye* eye);
typedef void (*McxEventFn)(const XrEventDataBuffer* event);    // every OpenXR event the host polled
typedef void (*McxShutdownFn)(void);                           // free GL objects, stop threads (before a reload)
typedef uint32_t (*McxAbiFn)(void);
// optional, shipped APK only: extra extensions to enable at instance creation (the host enables its default
// list too: the Runner's list, so dev and ship see the same extensions)
typedef const char* const* (*McxExtensionsFn)(uint32_t* count);

#ifdef __cplusplus
}
#endif

// in the module: `MCX_MODULE_ABI_EXPORT` once, so the host can check the ABI
#define MCX_MODULE_ABI_EXPORT extern "C" MCX_EXPORT uint32_t mcx_app_abi(void) { return MCX_MODULE_ABI; }
