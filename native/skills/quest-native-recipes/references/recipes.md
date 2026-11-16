# Recipes (mcx_xr.h, GLES 3)

Every block compiles as is (the suite's accuracy check builds them): recipe 1 is a whole `src/main.cpp`; recipes 2-5
are functions to paste above `android_main` and call from its loop.

## 1. A floating panel with text over passthrough: a clock

Request: "a floating clock over passthrough". Start: `mcx-app new /root/metacodex-projects/native/clock --example nightvision --label
"Floating Clock"`, replace `src/main.cpp` with this, and in `AndroidManifest.xml` drop what it no longer uses
(USE_SCENE, USE_ANCHOR_API) and set supportedDevices to `quest2|questpro|quest3|quest3s` (no depth needed). Keep
PASSTHROUGH, HAND_TRACKING + handtracking, the icon.

```cpp
#include <mcx_xr.h>
#include <cmath>
#include <cstdio>
#include <ctime>

namespace {

struct Panel
{
    XrPosef pose{{0, 0, 0, 1}, {0, 0, -1.2f}};
    bool placed = false;
};

// 1.2 m ahead at eye height, turned to the head's yaw, catching up slowly (never head-locked)
void Follow(Panel& p, const XrPosef& head, float dt, float distance = 1.2f, float drop = 0.1f)
{
    XrVector3f fwd = mcx::Forward(head);
    float len = sqrtf(fwd.x * fwd.x + fwd.z * fwd.z);
    if (len < 1e-3f) return;
    fwd.x /= len;
    fwd.z /= len;
    XrVector3f want{head.position.x + fwd.x * distance, head.position.y - drop, head.position.z + fwd.z * distance};
    float k = p.placed ? 1.f - expf(-dt * 1.5f) : 1.f;
    p.pose.position.x += (want.x - p.pose.position.x) * k;
    p.pose.position.y += (want.y - p.pose.position.y) * k;
    p.pose.position.z += (want.z - p.pose.position.z) * k;
    float yaw = atan2f(-fwd.x, -fwd.z);
    p.pose.orientation = {0.f, sinf(yaw * 0.5f), 0.f, cosf(yaw * 0.5f)};
    p.placed = true;
}

XrPosef Head(const mcx::Frame& f)
{
    XrPosef h = f.views[0].pose;
    h.position.x = (f.views[0].pose.position.x + f.views[1].pose.position.x) * 0.5f;
    h.position.y = (f.views[0].pose.position.y + f.views[1].pose.position.y) * 0.5f;
    h.position.z = (f.views[0].pose.position.z + f.views[1].pose.position.z) * 0.5f;
    return h;
}

} // namespace

void android_main(android_app* android)
{
    mcx::Config cfg;
    cfg.name = "Floating Clock";
    cfg.passthrough = true;
    mcx::App app;
    if (!app.Init(android, cfg)) { while (app.Pump()) {} return; }
    Panel panel;
    bool seconds = true;
    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        if (f.focused && ((app.hands.hand[0].pinchStarted || app.hands.hand[1].pinchStarted) || app.controllers.ctl[1].triggerPressed)) seconds = !seconds;
        Follow(panel, Head(f), f.dt);
        time_t now = time(nullptr);
        tm local{};
        localtime_r(&now, &local);
        char clock[32];
        strftime(clock, sizeof clock, seconds ? "%H:%M:%S" : "%H:%M", &local);
        mcx::Mat4 model = mcx::Mat4::FromPose(panel.pose);
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            glClearColor(0.f, 0.f, 0.f, app.clearAlpha()); // alpha 0: the room shows
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            glDisable(GL_DEPTH_TEST);
            float w = (seconds ? 8 : 5) * 0.12f * 0.5f; // characters x height x 0.5 (monospaced)
            app.text.Rect(eye.viewProj, model, -w / 2 - 0.04f, 0.10f, w + 0.08f, 0.20f, XrColor4f{0.02f, 0.03f, 0.05f, 0.7f});
            app.text.Draw(eye.viewProj, model, clock, -w / 2, 0.06f, 0.12f, XrColor4f{0.9f, 0.95f, 1.f, 1.f});
            app.text.Draw(eye.viewProj, model, "pinch: seconds on/off", -0.2f, -0.07f, 0.025f, XrColor4f{0.6f, 0.7f, 0.8f, 1.f});
            app.EndEye(e);
        }
        app.EndFrame();
    }
    app.Shutdown();
}
```

## 2. Pinch and controller buttons: one "select" for both

```cpp
// true once per press: an index pinch of either hand (the frame it starts, not with the palm towards you), the
// right trigger, or A
bool Select(const mcx::App& app, const mcx::Frame& f)
{
    if (!f.focused) return false;
    const mcx::Controller& c = app.controllers.ctl[1];
    bool pinch = false;
    for (int k = 0; k < 2; k++) pinch = pinch || (app.hands.hand[k].pinchStarted && !app.hands.hand[k].systemGesture);
    return pinch || c.triggerPressed || c.primaryPressed;
}

// the pointing ray: the right controller, else a hand with an aim (a pinching hand first); false when none is tracked
bool Pointer(const mcx::App& app, XrPosef& ray)
{
    const mcx::Controller& c = app.controllers.ctl[1];
    if (c.active) { ray = c.aim; return true; }
    const mcx::Hand* pick = nullptr;
    for (int k = 0; k < 2; k++)
    {
        const mcx::Hand& h = app.hands.hand[k];
        if (h.aimValid && (!pick || h.pinching)) pick = &h;
    }
    if (pick) { ray = pick->aim; return true; }
    return false;
}

// where the ray hits a panel whose XY plane is given by `pose` (panel units = metres); false = misses the plane
bool HitPanel(const XrPosef& ray, const XrPosef& pose, float& x, float& y)
{
    XrVector3f n = mcx::Rotate(pose.orientation, {0, 0, 1}), d = mcx::Forward(ray);
    float den = n.x * d.x + n.y * d.y + n.z * d.z;
    if (fabsf(den) < 1e-4f) return false;
    XrVector3f o{pose.position.x - ray.position.x, pose.position.y - ray.position.y, pose.position.z - ray.position.z};
    float t = (n.x * o.x + n.y * o.y + n.z * o.z) / den;
    if (t <= 0) return false;
    XrVector3f p{ray.position.x + d.x * t - pose.position.x, ray.position.y + d.y * t - pose.position.y, ray.position.z + d.z * t - pose.position.z};
    XrQuaternionf inv{-pose.orientation.x, -pose.orientation.y, -pose.orientation.z, pose.orientation.w};
    XrVector3f l = mcx::Rotate(inv, p);
    x = l.x;
    y = l.y;
    return true;
}
```

A button = a `Rect` at (bx, by, bw, bh) on the panel: hovered when `HitPanel` gives `bx <= x <= bx + bw` and
`by - bh <= y <= by` (Rect's y is its top); pressed when hovered and `Select(...)`. Haptic feedback on a controller:
`app.controllers.Haptic(1, 0.3f, 0.03f)`.

## 3. Things on the real table or wall (the room)

`cfg.scene = true` and USE_SCENE in the manifest. The room is there only after the user's Space Setup.

```cpp
// the first located object whose labels contain `label` (e.g. "TABLE", "WALL_FACE", "FLOOR"); nullptr if none
const mcx::SceneObject* Find(const mcx::App& app, const char* label)
{
    for (const mcx::SceneObject& o : app.spatial.scene)
        if (o.located && o.labels.find(label) != std::string::npos) return &o;
    return nullptr;
}

// call each frame: asks for the room once the permission is there
void RoomQuery(mcx::App& app, bool& asked)
{
    if (!asked && app.spatial.sceneAvailable && app.HasPermission(mcx::kPermScene)) asked = app.spatial.QueryScene();
}

// a model matrix lying on the object's plane (its XY plane: a table's top, a wall's face), at the plane's centre
mcx::Mat4 OnPlane(const mcx::SceneObject& o)
{
    float cx = o.hasPlane ? o.plane.offset.x + o.plane.extent.width * 0.5f : 0.f;
    float cy = o.hasPlane ? o.plane.offset.y + o.plane.extent.height * 0.5f : 0.f;
    return mcx::Mat4::FromPose(o.pose) * mcx::Mat4::Translation(cx, cy, 0.005f);
}
```

Text on the table: `app.text.Draw(eye.viewProj, OnPlane(*Find(app, "TABLE")), "hello", -0.1f, 0.03f, 0.04f, color)`
(check the pointer first). No room: say "run Space Setup in the headset's settings" on a panel.

## 4. Anchors that come back after a restart

`cfg.anchors = true` and USE_ANCHOR_API in the manifest. `app.spatial.LoadAnchors()` once at start brings back the
saved ones (their poses arrive in `anchor.pose` when `located`).

```cpp
// drop a persistent anchor where a hand or the right controller points, 1 m ahead (Pointer above)
void DropAnchor(mcx::App& app, const mcx::Frame& f, const XrPosef& ray)
{
    XrVector3f d = mcx::Forward(ray);
    XrPosef at{{0, 0, 0, 1}, {ray.position.x + d.x, ray.position.y + d.y, ray.position.z + d.z}};
    if (app.spatial.anchorsAvailable) app.spatial.CreateAnchor(app.local, at, f.time, true);
}

// a small marker at every located anchor
void DrawAnchors(mcx::App& app, const mcx::Eye& eye)
{
    for (const mcx::Anchor& a : app.spatial.anchors)
        if (a.located)
            app.text.Rect(eye.viewProj, mcx::Mat4::FromPose(a.pose), -0.03f, 0.03f, 0.06f, 0.06f,
                          a.saved ? XrColor4f{0.3f, 1.f, 0.5f, 0.9f} : XrColor4f{1.f, 0.8f, 0.3f, 0.9f});
}
```

`saved` turns true when the headset stored it (`mcx-app logs` shows `anchors: saved ...`).

## 5. Frame budget on the Quest 3S

```cpp
void Budget(mcx::Config& cfg)
{
    cfg.refreshRate = 72.f;  // 90 or 120 only for light scenes; app.refreshRates lists what the display offers
    cfg.foveation = 2;       // medium: free GPU time at the edges of the view
    cfg.cpuLevel = 2;        // sustained high; 3 (boost) only for short bursts: heat
    cfg.gpuLevel = 2;
}
```

- 72 fps = 13.9 ms per frame for both eyes. Keep draw calls in the low hundreds, one full-screen pass per eye at
  most (Night Vision's depth pass is one), no per-frame `glCompileShader` or buffer re-creation, upload once.
- Text: one `Draw` call per string (a buffer upload each): fine for a HUD, not for thousands of labels.
- Log once a second at most (`mcx::Log` writes to a socket).

## 6. The app's own Library icon (no image tools needed)

An app made from an example carries the example's icon: give it its own. Pure Python (no Pillow on the headset):
a rounded square in the app's colour with a simple ring, 192 x 192, into `res/drawable/icon.png`.

```python
import struct, sys, zlib
out, (r, g, b) = sys.argv[1], [int(sys.argv[2][i:i + 2], 16) for i in (0, 2, 4)]  # e.g. icon.png 3a7bd5
S = 192
rows = []
for y in range(S):
    row = bytearray([0])
    for x in range(S):
        dx, dy = abs(x - S / 2 + 0.5), abs(y - S / 2 + 0.5)
        corner = max(dx - 56, 0) ** 2 + max(dy - 56, 0) ** 2 <= 32 ** 2      # rounded square
        d = (dx * dx + dy * dy) ** 0.5
        ring = 40 <= d <= 54                                                  # the mark
        row += bytes((255, 255, 255, 255) if corner and ring else (r, g, b, 255) if corner else (0, 0, 0, 0))
    rows.append(bytes(row))
def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", S, S, 8, 6, 0, 0, 0)) + \
      chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b"")
open(out, "wb").write(png)
```

Save it as `/root/metacodex-projects/native/<name>/make-icon.py` (outside `src/`), run `python3 -I make-icon.py res/drawable/icon.png
3a7bd5`, keep `android:icon="@drawable/icon"`, build: `mcx-app verify` shows `icon res/drawable/icon.png`.
