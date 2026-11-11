# mcx_xr API, feature by feature

All in `namespace mcx`, header `/opt/metacodex/quest-native/xr/mcx_xr.h`. Each class also works on its own on an
`XrInstance` + `XrSession` (`Init(instance, session)`), e.g. in a Runner module (quest-native-dev-loop).

## Config (passed to `App::Init`)

| Field | Default | Meaning |
|---|---|---|
| `name` | "MetaCodex native app" | application name for OpenXR and the log |
| `passthrough` | false | create and start the passthrough layer |
| `depth` | false | environment depth (asks USE_SCENE, starts once granted) |
| `hands` | true | hand trackers (+ aim when the runtime has it) |
| `controllers` | true | the action set with the Touch profiles |
| `scene` | false | the room queries (USE_SCENE) |
| `anchors` | false | spatial anchors + storage |
| `refreshRate` | 0 | 0 = the runtime's default, else e.g. 90.f or 120.f |
| `foveation` | -1 | -1 off, 0 none, 1 low, 2 medium, 3 high |
| `cpuLevel`, `gpuLevel` | -1 | -1 leave, 0 power savings, 1 sustained low, 2 sustained high, 3 boost |
| `askPermissions` | true | request USE_SCENE / HAND_TRACKING when needed and not granted |
| `extraExtensions` | {} | more OpenXR extension names to enable when offered |

## App

- `bool Init(android_app*, const Config&)`, `bool Pump()`, `bool BeginFrame(Frame&)`, `void BeginEye(int, Eye&)`,
  `void EndEye(int)`, `void EndFrame()`, `void Shutdown()`.
- State: `Focused()`, `Visible()`, `Resumed()`, `State()` (XrSessionState), `HasPermission(const char*)`,
  `RequestPermissions(std::vector<const char*>)`.
- Members: `instance`, `system`, `session`, `local`, `stage` (XR_NULL_HANDLE without a boundary), `ext`
  (`ext.Has(name)` enabled, `ext.Offered(name)` available), `passthrough`, `depth`, `hands`, `controllers`,
  `spatial`, `text`, `projectionFlags` (set to source-alpha blending while passthrough runs), `clearAlpha()`,
  `refreshRates`, `refreshRate`, `onEvent` (std::function called with every `XrEventDataBuffer`).
- `SetRefreshRate(float hz)`, `SetFoveation(int level)`, `SetPerformance(int cpuLevel, int gpuLevel)`: bool = done.
- `Frame`: `time` (XrTime, predicted display time), `seconds` (since Init), `dt`, `views[2]` (XrView),
  `focused`, `depth` (DepthFrame).
- `Eye`: `index`, `width`, `height`, `projection`, `view`, `viewProj` (Mat4), `pose` (XrPosef), `framebuffer`.
  Near plane 0.05 m, far 100 m.
- Lifecycle handled inside: session READY -> begin, STOPPING -> end, EXITING / LOSS_PENDING -> Pump() returns false;
  Android pause/resume/destroy. When `Pump()` returns false, call `app.Shutdown()` and return from `android_main`.

## The app menu and the way back (built into `App`)

- Opens with the left controller's menu button or the non-dominant hand's menu gesture (palm facing you, pinch);
  a panel 0.9 m ahead; point with the right controller or hand and pull the trigger / pinch, or move with the stick
  and press A. While it is open `Frame::focused` is false and `app.MenuOpen()` is true.
- Items: `app.menuItems` (yours, `MenuItem{label, action}`) then **Back to MetaCodex** (or **Open MetaCodex** when the
  app was started from the Library), **Report to MetaCodex**, **Restart**, **Quit**, **Close menu**.
- `app.ReturnToCaller(false)`: starts MetaCodex again (warm, nothing reloads) and ends this app. `app.ReturnToCaller(true)`:
  the same with a report: the next clean frame of the left eye (the app's own pixels, not the room) as a PNG under
  ~450 KB, the last 60 log lines (`mcx::RecentLog`), `app.label`, `app.versionName`, `app.versionCode`.
  `app.Restart()`, `app.Quit()`, `app.OpenMenu(bool)`.
- `app.caller` (the package that launched it, "" from the Library), `app.launchId`, `app.packageName`.
- Quitting from the system menu tries the way back too (best effort: Android may refuse a start from the background).

## Passthrough (`app.passthrough`, XR_FB_passthrough)

- `Start()`, `Pause()`, `Running()`, `Available()` (false: the feature flag is missing or no passthrough).
- `SetStyle(float opacity, XrColor4f edge = {0,0,0,0})`: opacity of the camera image, edge colour (alpha = strength).
- `SetMonoToRgba(std::function<XrColor4f(int)> map, float opacity = 1, XrColor4f edge = {})`: the camera's
  brightness 0..255 -> a colour (tints, night vision, thermal looks).
- `SetBrightnessContrastSaturation(float brightness, float contrast, float saturation, float opacity = 1)`:
  brightness -100..100, contrast and saturation 0..; one colour style at a time.
- App submits `Layer()` under the projection layer; your pixels with alpha < 1 show the room through.
- When you pause it yourself, also set `app.projectionFlags = 0` (opaque) and clear alpha to 1; when you start it,
  `app.projectionFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT` (Night Vision does this).

## Environment depth (`app.depth`, `f.depth`, XR_META_environment_depth)

- `f.depth` (`DepthFrame`): `valid`, `texture` (GLuint, `GL_TEXTURE_2D_ARRAY`, 2 layers: left, right; nearest
  sampling set up), `width`, `height`, `nearZ`, `farZ` (may be +inf), `views[2]` (pose + fov of each depth view),
  `viewProj[2]` (Mat4: local space -> depth clip space per layer), `captureTime`, `Linear(float d)` (metres).
- `app.depth`: `Running()`, `SetHandRemoval(bool)` (on by default: your hands are removed from the map), `Stop()`,
  `supportsHandRemoval`.
- Sampling a world point `p` in a shader: `vec4 c = depthViewProj * vec4(p, 1); vec2 uv = c.xy / c.w * 0.5 + 0.5;
  float d = texture(depthTex, vec3(uv, layer)).r;` metres: infinite far `2 * nearZ / (1 - (2d - 1))`, else
  `2 * far * near / (far + near - (2d - 1) * (far - near))`. Occlusion: compare with your fragment's distance.

## Hands (`app.hands.hand[0]` left, `[1]` right; XR_EXT_hand_tracking + XR_FB_hand_tracking_aim)

`Hand`: `tracked`, `joints[XR_HAND_JOINT_COUNT_EXT]` (XrHandJointLocationEXT; e.g.
`hand.Joint(XR_HAND_JOINT_INDEX_TIP_EXT)` returns the XrPosef), `aimValid`, `aim` (XrPosef: a pointing ray from the
hand, -Z), `pinch[4]` (index, middle, ring, little: 0..1), `pinching`, `pinchStarted` (this frame), `menuGesture`,
`systemGesture`. `app.hands.Available()`.

## Controllers (`app.controllers.ctl[0]` left, `[1]` right)

`Controller`: `active`, `aim`, `grip` (XrPosef), `trigger`, `squeeze` (0..1), `stick` (XrVector2f), `primary`
(A on the right, X on the left), `secondary` (B / Y), `stickClick`, `menu` (left), edges `primaryPressed`,
`secondaryPressed`, `triggerPressed`. `app.controllers.Haptic(int hand, float amplitude, float seconds)`.
`app.controllers.profile[h]`: the current interaction profile path. Bound profiles: oculus/touch_controller,
meta/touch_controller_plus, facebook/touch_controller_pro (when offered), khr/simple_controller.

## Room and anchors (`app.spatial`; XR_FB_scene, XR_FB_spatial_entity, _query, _storage)

- `QueryScene()` (once, after `app.HasPermission(mcx::kPermScene)`); results arrive by events into `scene`
  (`std::vector<SceneObject>`), `sceneDone` when finished, `lastError` on failure. `sceneAvailable`.
- `SceneObject`: `space`, `uuid`, `labels` (comma-separated: e.g. FLOOR, CEILING, WALL_FACE, TABLE, COUCH, BED,
  DOOR_FRAME, WINDOW_FRAME, SCREEN, LAMP, PLANT, STORAGE, OTHER), `hasPlane` + `plane` (XrRect2Df in the object's XY
  plane), `hasVolume` + `volume` (XrRect3DfFB), `pose` (located in local each frame), `located`.
- `CreateAnchor(app.local, pose, f.time, bool persist)`; `LoadAnchors()` (the saved ones, at start); `EraseAnchor(a)`;
  `anchors` (`Anchor`: `space`, `uuid`, `saved`, `pose`, `located`); `anchorsAvailable`. `mcx::UuidString(uuid)`.
- A room exists only after the user ran Space Setup; zero objects is normal otherwise.

## Text and panels (`app.text`)

- `float Draw(const Mat4& viewProj, const Mat4& model, const char* s, float x, float y, float height, XrColor4f color)`:
  ASCII, `\n` breaks lines, drawn on the XY plane of `model` (metres), top-left at (x, y); returns the width.
  Monospaced: a character is `height * 0.5` wide.
- `void Rect(const Mat4& viewProj, const Mat4& model, float x, float y, float w, float h, XrColor4f color)`: a
  filled rectangle (top-left at x, y), for panel backgrounds and buttons.
- Both blend and do not write depth; draw panels after the scene, `glDisable(GL_DEPTH_TEST)` to keep them on top.

## Maths and helpers

`Mat4::Identity()`, `Translation(x, y, z)`, `Scale(x, y, z)`, `FromPose(pose)`, `ViewFromPose(pose)`,
`Projection(fov, nearZ, farZ)` (farZ <= 0 or inf: infinite far plane), `operator*`; `Rotate(quaternion, vector)`,
`Forward(pose)`; `Log(fmt, ...)`, `SetLogTag(tag)`; `Permissions::Granted(activity, name)`,
`Permissions::Request(activity, names)`; `kPermScene`, `kPermHands`, `kPermAnchors`; `Proc<PFN_...>(instance, "xr...")`
for any other extension function (enable the extension with `cfg.extraExtensions`).
