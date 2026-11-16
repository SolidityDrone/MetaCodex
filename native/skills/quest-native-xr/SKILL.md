---
name: quest-native-xr
description: The Quest features of native apps built in MetaCodex, through its native XR layer mcx_xr.h (C++, OpenXR + Meta extensions, OpenGL ES 3) - passthrough and mixed reality, environment depth (the depth map of the room), hand tracking with pinch and aim rays, Touch / Touch Plus controllers with haptics, the room (walls, floor, furniture from Space Setup), spatial anchors that persist, refresh rate, foveation, CPU/GPU performance levels, runtime permissions, the app lifecycle and system menu, text panels, the Library icon. Use when writing or changing the code or the AndroidManifest.xml of a native Quest app (after quest-native-apps), or when a feature does not work. Each feature lists its calls, the exact manifest entries and whether it ran on the headset.
---

# Quest features in native apps: `#include <mcx_xr.h>`

`mcx-build` links the layer into every app (built once per toolchain); there is nothing to configure. Header:
`/opt/metacodex/quest-native/xr/mcx_xr.h` (read it for exact signatures). Worked example using every feature:
`/opt/metacodex/quest-native/examples/nightvision/src/main.cpp` (quest-native-recipes).

## The app loop (always this shape)

```cpp
#include <mcx_xr.h>
void android_main(android_app* a) {
    mcx::Config cfg;
    cfg.name = "My App";
    cfg.passthrough = true;                       // features: see the table; hands and controllers default to on
    mcx::App app;
    if (!app.Init(a, cfg)) { while (app.Pump()) {} return; }   // stay alive so the log can be read
    while (app.Pump()) {                          // Android + OpenXR events, session lifecycle; false = exit
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;         // waits for the frame; views, controllers, hands, depth
        if (f.focused) { /* read input here only */ }
        for (int e = 0; e < 2; e++) {
            mcx::Eye eye;
            app.BeginEye(e, eye);                 // binds the eye's framebuffer (sRGB colour + depth), viewport
            glClearColor(0.f, 0.f, 0.f, app.clearAlpha());     // alpha 0 where passthrough shows
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            /* draw with eye.viewProj (mcx::Mat4, column-major) */
            app.EndEye(e);
        }
        app.EndFrame();                           // passthrough underlay + your projection layer
    }
    app.Shutdown();
}
```

Space: everything is in `app.local` (LOCAL: origin where the app started, metres, +Y up, -Z ahead). `f.views[0/1]`
are the eyes; the head = their midpoint. `mcx::Mat4::FromPose(pose)` places things; `mcx::Forward(pose)` is a pose's
-Z. Logging: `mcx::Log("x = %d", x)` -> logcat and `mcx-app logs`.

## Features (Quest 3 / 3S; manifest entries are exact, from Meta's native docs)

| Feature | Turn on / use | AndroidManifest.xml needs | Status |
|---|---|---|---|
| App menu, way back, report (**always, never removed**) | built in: the left controller's menu button, or a hand's menu gesture (left hand: palm towards your face, then pinch; it needs the hand aim extension) opens it. A pinch of either hand picks an item. Opened from MetaCodex (`app.FromMetaCodex()`): Back to MetaCodex, Report to MetaCodex, your items, Quit; otherwise your items and Quit. Back and Quit close the app. Add items with `app.menuItems.push_back({"Label", [&] { ... }})`; `app.ReturnToCaller(report)`, `app.Quit()`, `app.MenuOpen()` | nothing | Back ran on headset with 0.7.15 (it then froze the app: fixed, see quest-native-debug) |
| Lifecycle, system menu | `app.Pump()`, `f.focused` (false while a system menu has focus: pause, take no input), `app.Visible()`, `app.Resumed()` | NativeActivity block as in the template | ran on headset |
| Passthrough (mixed reality) | `cfg.passthrough = true`; `app.passthrough.Start()/Pause()`, styles; clear alpha 0 | `<uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />` | builds on headset; extension offered |
| Environment depth | `cfg.depth = true`; `f.depth` each frame (`valid`, `texture`, `viewProj[eye]`, `Linear(d)`) | `<uses-permission android:name="com.oculus.permission.USE_SCENE" />`; supportedDevices `quest3\|quest3s` | builds on headset; extension offered; runtime permission |
| Hands, pinch, aim | `cfg.hands` (default on); `app.hands.hand[0/1]` | `<uses-permission android:name="com.oculus.permission.HAND_TRACKING" />` + `<uses-feature android:name="oculus.software.handtracking" android:required="false" />` | builds on headset; extension offered. Pinch of EITHER hand (`pinchStarted && !systemGesture`); the rules, the triage and the permission check are in quest-native-hands-first |
| Controllers | `cfg.controllers` (default on); `app.controllers.ctl[0/1]`, `app.controllers.Haptic(hand, amplitude, seconds)` | nothing | builds on headset |
| Room (scene) | `cfg.scene = true`; `app.spatial.QueryScene()` once the permission is granted; `app.spatial.scene` | `USE_SCENE` (as depth) | builds on headset; extension offered |
| Anchors | `cfg.anchors = true`; `app.spatial.CreateAnchor(...)`, `LoadAnchors()`, `app.spatial.anchors` | `<uses-permission android:name="com.oculus.permission.USE_ANCHOR_API" />` | builds on headset; extension offered |
| Refresh rate | `cfg.refreshRate = 90.f` or `app.SetRefreshRate(hz)`; `app.refreshRates` lists what the display offers | nothing | ran on headset (72 Hz read) |
| Foveation | `cfg.foveation = 0..3` or `app.SetFoveation(level)` | nothing | builds on headset |
| CPU/GPU levels | `cfg.cpuLevel`, `cfg.gpuLevel` (0 power savings, 1 sustained low, 2 sustained high, 3 boost) or `app.SetPerformance(cpu, gpu)` | nothing | builds on headset |
| Text panels | `app.text.Draw(eye.viewProj, model, "text", x, y, height, color)`, `app.text.Rect(...)` | nothing | builds on headset |
| Icon + label | `res/drawable/icon.png` and `android:icon="@drawable/icon"` on `<application>`; `android:label` | that | verified in the APK on the headset |

"builds on headset" = compiled, linked, verified and signed on the Quest 3S; "extension offered" = the runtime offered
it to an app with these entries; not yet observed running there: tell the user it is new when they test.

**After writing the manifest: `mcx-app verify <dir>`.** It reads the APK and reports each Meta API the code uses
whose permission or feature is missing (passthrough, depth, scene, anchors, hand/body/face/eye tracking), unresolved
symbols, a missing icon, and depth apps listing Quest 2 / Pro. Fix every `PROBLEM:`.

## Permissions at runtime

- On Horizon OS only **USE_SCENE** (spatial data: depth, the room) is a runtime permission. `app.Init` asks for it
  when `cfg.depth` or `cfg.scene` is set (`cfg.askPermissions`); the system dialog shows in the app; depth starts by
  itself within a second of the grant. `HAND_TRACKING` and `USE_ANCHOR_API` are granted at install. The app logs its
  HAND_TRACKING state at each start, and it makes the hand trackers when that grant comes later (quest-native-hands-first).
- Check: `app.HasPermission(mcx::kPermScene)`. Denied: the app must still run (no depth, no room); show a line such as
  "allow spatial data: Settings > Apps > <Label> > Permissions" on a text panel and tell the user the same.

## Pitfalls

- Never take the left controller's menu button or a hand's menu gesture for your own controls: they open the app menu.
  The headset's system gestures (the palm towards your face) are the system's, not yours (quest-native-hands-first,
  rules 2 and 3). While the menu is open `f.focused` is false: your app gets no input.
- Never your own exit or OpenXR loop: `mcx::App` ends the session and finishes the activity (Back, Quit); it renders
  no frame while leaving or paused, so the app never freezes in the background (`mcx-app verify` checks it).

- Output colour is premultiplied when passthrough is on: write `vec4(rgb * a, a)`; where nothing is drawn, alpha 0.
- Never put text or panels on the head (head-locked): place them in the room, 1-1.5 m away, following lazily
  (recipe in quest-native-recipes).
- Input only while `f.focused`; a pinch while the palm faces you (`hand.systemGesture`) does not count: the pinch code
  checks `pinchStarted`, which is false then (quest-native-hands-first, rule 4).
- `f.depth.valid` is false for the first frames and whenever the provider has no new map: draw without it.
- Depth texture: `GL_TEXTURE_2D_ARRAY`, layer = eye, values in [0, 1] (OpenGL convention, far plane may be
  infinite): metres with `f.depth.Linear(d)` on the CPU or the same formula in the shader (recipes).
- The swapchains are sRGB: write linear colours.
- Do not create OpenXR action sets yourself when `cfg.controllers` is on (only one attach per session is allowed).

Full API, feature by feature: `references/api.md`.
