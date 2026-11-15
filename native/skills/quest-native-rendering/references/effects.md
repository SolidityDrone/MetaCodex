# Effects and motion with mcx_gfx.h: recipes that compile

The blocks of this file form ONE C++ file that compiles with the NDK (the skills gate joins them). Copy what you
need into `scene.cpp`. The last section is a whole app (a solar system in the room) built from the blocks.
Labels: **ours** (a MetaCodex choice, compiled, not yet measured on the headset); check the look on the device and
tune the numbers in `config.h`.

| Want | Use | Draw calls |
|---|---|---|
| a glow round a bright object, no post-processing | `mcx::Sprites`: two sprites at its centre (section 1) | 1 for all sprites of a set |
| stars, a sky; the room fading to space | a fixed `mcx::Sprites` set + the clear alpha (section 2) | 1 |
| sparks on a hit, a celebration | `mcx::Particles` into a `mcx::Sprites` set (section 3) | 0 extra (shares the set) |
| the path of a moving thing | `mcx::Trail` into a `mcx::Sprites` set (section 3) | 0 extra |
| things that arrive, leave, react | `mcx::EaseOut`, `mcx::EaseOutBack`, `mcx::Progress`, `mcx::Damp`, `mcx::Pulse` (section 4) | 0 |
| many copies of one shape | `mcx::Instances` + `lit.DrawInstanced`, one per material (section 5) | 1 per material |
| a shape the primitives lack (vase, bowl, lamp, planet ring) | `MakeLathe` below (section 6) | 1 |
| a sun or a lamp as the light | set `lit.light.keyDir` per object, then `lit.Begin` again (section 7) | 0 extra |
| a gradient with no texture | the hemisphere light (`sky` above, `ground` below), the rim (`m.rim`), a large faint sprite behind the hero | 0-1 |

Rules for every effect:
- Sprites ADD light. Over passthrough they light the room; on a white wall they show less (the `dim` field, 0.3 by
  default, also darkens the room a little under each sprite so it still reads). Never use a sprite as a solid.
- Convert colours once at init (`mcx::GfxSrgbRgb(0x...)` gives linear), never per frame. Put the brightness in the
  colour (`mcx::Vec3Scale(c, 0.6f)`).
- Fill is the cost: a sprite 0.3 m wide at 1 m covers about 17 degrees. Keep big halos few (2-4); keep particles
  under about 300 and stars under about 1,000 (they are tiny).
- Order in each eye: clear, opaque (`lit`), contact shadows, translucent, sprites, UI.

## 1. Glow: a halo round a bright object

The body is lit with `mcx::Material::Glow`. Two sprites sit at its centre. The solid body hides their middle, so
what shows is light round its edge.

```cpp
#include <mcx_gfx.h>
#include <mcx_ui.h>
#include <cmath>
#include <cstdio>

// a tight glow and a wide faint bloom; `linear` from mcx::GfxSrgbRgb at init; strength about 1
void AddHalo(mcx::Sprites& glow, XrVector3f centre, float diameter, XrVector3f linear, float strength)
{
    glow.Add(centre, diameter * 2.2f, mcx::Vec3Scale(linear, 0.6f * strength));
    glow.Add(centre, diameter * 4.5f, mcx::Vec3Scale(linear, 0.15f * strength));
}
```

## 2. Stars, and the room fading to space

A starfield is a fixed set: build it once, never `Clear` it, fade it with `stars.intensity`. The room fades with
the clear colour: alpha 0 shows the room, alpha 1 hides it. The clear colour is premultiplied (rgb times alpha).
Fade only when the user asks for space or VR (quest-native-hands-first: mixed reality is the default).

```cpp
// 6-9 m round the start position; warm and cool stars; a few bright, many faint
bool BuildStars(mcx::Sprites& stars, int count)
{
    if (!stars.Init(count)) return false;
    const XrVector3f warm = mcx::GfxSrgbRgb(0xFFE6C4), cool = mcx::GfxSrgbRgb(0xC4DAFF);
    unsigned int seed = 20261115u;
    auto rnd = [&seed]() { seed = seed * 1664525u + 1013904223u; return (float)(seed >> 8) / 16777216.f; };
    for (int i = 0; i < count; i++)
    {
        const float z = rnd() * 2.f - 1.f, a = rnd() * 6.2831853f, r = std::sqrt(1.f - z * z), d = 6.f + 3.f * rnd();
        const float bright = 0.25f + 0.75f * rnd() * rnd();
        const XrVector3f tint = rnd() < 0.5f ? warm : cool;
        stars.Add({r * std::cos(a) * d, z * d, r * std::sin(a) * d}, 0.035f * d / 6.f, mcx::Vec3Scale(tint, bright), 1.f);
    }
    return true;
}

// space 0 = the room, 1 = a dark sky (move it with mcx::Damp, about 1 s). Without passthrough the sky is opaque.
void ClearRoomOrSpace(const mcx::App& app, float space, XrVector3f sky)
{
    const float a = app.passthrough.Running() ? mcx::EaseInOut(space) : 1.f;
    glClearColor(sky.x * a, sky.y * a, sky.z * a, a);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
}
```

## 3. Sparks and trails

One `mcx::Sprites` set for everything that moves: clear it once per frame, then add halos, particles and trails.
`Particles` and `Trail` have a fixed size (set in `Init`): nothing allocates per frame.

```cpp
struct Sparkle
{
    mcx::Sprites glow;      // cleared and refilled each frame
    mcx::Particles sparks;
    mcx::Trail trail;
    XrVector3f accent{0.f, 0.f, 0.f};
    bool Init(unsigned int accentSrgb)
    {
        accent = mcx::GfxSrgbRgb(accentSrgb);
        sparks.gravity = {0.f, -0.8f, 0.f};
        return glow.Init(512) && sparks.Init(256) && trail.Init(24, 0.012f);
    }
    // a hit: 28 sparks, up to 0.9 m/s, 1.8 cm, about 0.6 s
    void Hit(XrVector3f at) { sparks.Burst(at, 28, 0.9f, accent, 0.018f, 0.6f); }
    // once per frame, before the eyes: move, then fill the set (the ball: its centre and diameter)
    void Update(float dt, XrVector3f ball, float diameter)
    {
        sparks.Update(dt);
        trail.Push(ball);
        glow.Clear();
        sparks.AddTo(glow);
        trail.AddTo(glow, diameter * 1.4f, mcx::Vec3Scale(accent, 0.5f));
        AddHalo(glow, ball, diameter, accent, 0.8f);
    }
    void Destroy() { glow.Destroy(); }
};
```

## 4. Motion: arrive, react, breathe, leave

- Arrive: grow from 0 with `mcx::EaseOutBack` (game objects) or `mcx::EaseOut` (calm things), 0.3-0.45 s,
  each next object 0.06-0.08 s later. UI panels have their own 0.18 s ease-out in the kit.
- React: `mcx::Damp` towards the target every frame (hover lift 4 mm, glow up); the change starts in the frame of
  the pinch or the touch.
- Breathe: `mcx::Pulse(f.seconds, 0.25f)` gives 0..1..0 every 4 s: a bob of 5-10 mm or a glow of plus 10-15 %.
- Leave: shrink to 0 with `mcx::EaseIn` in 0.2 s, then stop drawing.
- Follow a hand or a pose: `mcx::Vec3Damp` and `mcx::QuatDamp` (rate 10-20): smooth at any frame rate, no jitter.

```cpp
// 0 before its delay, then 0..1 with a small overshoot (game objects only)
float GrowIn(double now, double start, float delay)
{
    return start < 0.0 ? 0.f : mcx::EaseOutBack(mcx::Progress(now, start + delay, 0.45f), 1.2f);
}
// 1..0 in 0.2 s after `leftAt`
float ShrinkOut(double now, double leftAt) { return 1.f - mcx::EaseIn(mcx::Progress(now, leftAt, 0.2f)); }

// hover feedback on a 3D object: lift and glow, smooth at any frame rate
struct Hover
{
    float lift = 0.f, shine = 0.f;
    void Update(bool on, float dt)
    {
        lift = mcx::Damp(lift, on ? 0.004f : 0.f, 18.f, dt);
        shine = mcx::Damp(shine, on ? 1.f : 0.f, 14.f, dt);
    }
};
```

## 5. Many copies: instancing

One `mcx::Instances` per material: one draw call for all its copies. Refill it each frame when they move (the
capacity is fixed in `Init`; nothing allocates). Vary scale and turn a little (seeded) so copies do not look cloned.

```cpp
// an asteroid belt round `centre`: rough rocks from a stretched low sphere
struct Belt
{
    mcx::Mesh rock;
    mcx::Instances inst;
    int count = 0;
    bool Init(int n)
    {
        count = n;
        return rock.Upload(mcx::MakeSphere(0.5f, 10, 6)) && inst.Init(rock, n);
    }
    void Fill(XrVector3f centre, float radius, double seconds)
    {
        inst.Clear();
        unsigned int seed = 99u; // the same layout every frame
        auto rnd = [&seed]() { seed = seed * 1664525u + 1013904223u; return (float)(seed >> 8) / 16777216.f; };
        for (int i = 0; i < count; i++)
        {
            const float a = 6.2831853f * (float)i / (float)count + 0.05f * rnd() + 0.05f * (float)seconds;
            const float r = radius * (0.92f + 0.16f * rnd()), size = 0.006f + 0.01f * rnd();
            const XrVector3f p{centre.x + r * std::cos(a), centre.y + 0.02f * (rnd() - 0.5f), centre.z + r * std::sin(a)};
            inst.Add(mcx::Trs(p, mcx::QuatYawPitch(6.28f * rnd(), 3.14f * rnd()), {size, 0.7f * size, 0.8f * size}));
        }
    }
    void Destroy() { inst.Destroy(); rock.Destroy(); }
};
```

Draw it between `lit.Begin` and `lit.End`: `lit.DrawInstanced(belt.inst, mcx::Material::Matte(0x8A7F74))`.

## 6. Procedural meshes: a lathe

A profile of (radius, height) points, bottom to top, turned round +Y: vases, bowls, lamps, chess pieces, a flat
ring. Normals come from the profile's slope; the winding is counter-clockwise seen from outside.

```cpp
mcx::MeshData MakeLathe(const XrVector2f* profile, int n, int segments)
{
    mcx::MeshData m;
    m.vertices.reserve((size_t)(n * (segments + 1)));
    m.indices.reserve((size_t)((n - 1) * segments * 6));
    for (int i = 0; i < n; i++)
    {
        const XrVector2f a = profile[i > 0 ? i - 1 : i], b = profile[i < n - 1 ? i + 1 : i];
        const float dr = b.x - a.x, dy = b.y - a.y;
        for (int k = 0; k <= segments; k++)
        {
            const float t = (float)k / (float)segments, ang = t * 6.2831853f, c = std::cos(ang), s = std::sin(ang);
            const XrVector3f normal = mcx::Vec3Normalize({dy * c, -dr, dy * s});
            m.vertices.push_back({{profile[i].x * c, profile[i].y, profile[i].x * s}, normal, {t, (float)i / (float)(n - 1)}});
        }
    }
    for (int i = 0; i + 1 < n; i++)
        for (int k = 0; k < segments; k++)
        {
            const uint32_t a = (uint32_t)(i * (segments + 1) + k), b = a + 1, c = a + (uint32_t)(segments + 1), d = c + 1;
            m.indices.insert(m.indices.end(), {a, c, b, b, c, d});
        }
    return m;
}

// a vase 24 cm tall: foot, belly, neck, lip, and a closed top (culling hides the inside of an open one)
mcx::MeshData MakeVase()
{
    static const XrVector2f profile[] = {{0.f, 0.f}, {0.045f, 0.f}, {0.05f, 0.01f}, {0.075f, 0.08f}, {0.07f, 0.15f},
                                         {0.035f, 0.205f}, {0.032f, 0.225f}, {0.042f, 0.24f}, {0.f, 0.235f}};
    return MakeLathe(profile, 9, 48);
}
```

Other shapes: `mcx::MeshData::Append` joins meshes with a transform (one mesh, one draw call), for example a lamp from
a lathe shade and a cylinder pole. A thin orbit line: dots of a fixed sprite set (section 8) look softer than a
thin torus, which flickers when it is under 2 pixels wide.

## 7. A sun or a lamp as the light

`lit.Begin` sends the light to the shader. For a point light, point the key light from each object to the light
and call `lit.Begin` again before that object. Keep the ambient low in space and higher over the room: a black
night side reads as a hole in the room.

```cpp
void DrawLitBySun(mcx::Lit& lit, const mcx::Eye& eye, const mcx::Mesh& unitSphere, XrVector3f sun, XrVector3f pos,
                  float diameter, const mcx::Material& mat)
{
    lit.light.keyDir = mcx::Vec3Sub(sun, pos);
    lit.Begin(eye);
    lit.Draw(unitSphere, mcx::Trs(pos, mcx::QuatYawPitch(0.f, 0.f), {diameter, diameter, diameter}), mat);
}
```

## 8. The whole app: a solar system in the room

What the user sees: the room, a start panel ("Orrery", Start). Pinch Start: the sun grows in with its halo, then
the planets one by one on dotted orbits, with short trails. A control panel to the right: Space (the room fades to a
starry sky in about 1 s, and back) and Speed. Walk round it. The look: Cosmic (quest-native-art-direction).

```cpp
struct Planet
{
    float orbit = 0.f;       // metres from the sun
    float diameter = 0.f;    // metres
    float speed = 0.f;       // radians per second
    float phase = 0.f;       // radians at the start
    unsigned int colour = 0; // sRGB
};
static const Planet kPlanets[] = {
    {0.16f, 0.030f, 0.90f, 0.0f, 0x9C8F86}, // a small grey rock
    {0.24f, 0.048f, 0.62f, 1.7f, 0xE8C38A}, // a cloudy amber world
    {0.33f, 0.052f, 0.48f, 3.9f, 0x4F8FD8}, // a blue water world
    {0.42f, 0.040f, 0.38f, 5.1f, 0xD0644A}, // a rust world
    {0.58f, 0.110f, 0.22f, 2.4f, 0xD9B48C}, // a banded giant
};
constexpr int kPlanetCount = 5;
constexpr float kSun = 0.28f;          // the sun's diameter, metres
constexpr float kAhead = 1.3f;         // the centre: metres ahead of the head at Start
constexpr float kBelowEyes = 0.35f;    // and this far below the eyes (chest height)

struct Orrery
{
    mcx::Lit lit;
    mcx::Mesh sphere;                  // diameter 1: scaled per body
    mcx::Sprites glow, stars, paths;   // moving glow (each frame), the sky (fixed), dotted orbits (at Start)
    mcx::Trail trails[kPlanetCount];
    mcx::Ui ui;
    mcx::UiPanel startPanel, controls;
    mcx::Material sunMat, planetMat[kPlanetCount];
    XrVector3f centre{0.f, 0.f, 0.f}, pos[kPlanetCount] = {}, sunLight{0.f, 0.f, 0.f}, sky{0.f, 0.f, 0.f};
    double started = -1.0;             // Frame::seconds of Start; -1 before
    double simTime = 0.0;
    float space = 0.f, speed = 1.f;
    bool spaceOn = false, placed = false;

    bool Init(mcx::App& app)
    {
        sunLight = mcx::GfxSrgbRgb(0xFFC46B);
        sky = mcx::GfxSrgbRgb(0x060914);
        sunMat = mcx::Material::Glow(0xFFB347, 1.f);
        sunMat.base = mcx::GfxSrgb(0xFFD08A);
        sunMat.emissive = mcx::Vec3Scale(sunLight, 0.95f);
        for (int i = 0; i < kPlanetCount; i++)
        {
            planetMat[i] = mcx::Material::Matte(kPlanets[i].colour);
            planetMat[i].rim = 0.3f; // a thin atmosphere on the edge
            if (!trails[i].Init(28, 0.01f)) return false;
        }
        lit.light.keyColor = {1.f, 0.86f, 0.66f}; // a warm sun
        return lit.Init() && ui.Init(app) && sphere.Upload(mcx::MakeSphere(0.5f, 48, 24)) && glow.Init(256) &&
               paths.Init(kPlanetCount * 72) && BuildStars(stars, 700);
    }

    // Start: the centre in front of the user, the dotted orbits, the control panel to the right
    void Place(const mcx::Frame& f)
    {
        const XrVector3f head = mcx::Vec3Lerp(f.views[0].pose.position, f.views[1].pose.position, 0.5f);
        XrVector3f fwd = mcx::Forward(f.views[0].pose);
        fwd.y = 0.f;
        if (mcx::Vec3Dot(fwd, fwd) < 1e-4f) fwd = {0.f, 0.f, -1.f};
        fwd = mcx::Vec3Normalize(fwd);
        centre = {head.x + fwd.x * kAhead, head.y - kBelowEyes, head.z + fwd.z * kAhead};
        const XrVector3f dot = mcx::Vec3Scale(mcx::GfxSrgbRgb(0x7C8DB5), 0.35f);
        paths.Clear();
        for (int i = 0; i < kPlanetCount; i++)
            for (int k = 0; k < 72; k++)
            {
                const float a = 6.2831853f * (float)k / 72.f;
                paths.Add({centre.x + kPlanets[i].orbit * std::cos(a), centre.y, centre.z + kPlanets[i].orbit * std::sin(a)}, 0.005f, dot);
            }
        controls.PlaceInFront(f, 1.0f, 0.15f);
        const XrVector3f right{-fwd.z, 0.f, fwd.x};
        controls.pose.position = mcx::Vec3Add(controls.pose.position, mcx::Vec3Scale(right, 0.8f)); // beside the stage
        XrVector3f away = mcx::Vec3Sub(controls.pose.position, head);
        away.y = 0.f;
        controls.pose.orientation = mcx::QuatLookAt(controls.pose.position, mcx::Vec3Add(controls.pose.position, away));
        started = f.seconds;
    }

    // once per frame, after BeginFrame: the UI, the motion, the glow set
    void Update(mcx::App& app, const mcx::Frame& f)
    {
        ui.Begin(app, f);
        if (started < 0.0)
        {
            if (!placed) { startPanel.PlaceInFront(f, 1.0f); placed = true; }
            startPanel.title = "Orrery";
            startPanel.width = 0.40f;
            startPanel.height = 0.25f;
            ui.Open(startPanel);
            ui.Label("A solar system in your room");
            ui.Space(0.012f);
            const bool go = ui.Button("Start", mcx::UiStyle::Primary);
            ui.Close();
            if (go) Place(f);
            return;
        }
        controls.title = "Orrery";
        controls.width = 0.36f;
        controls.height = 0.27f;
        ui.Open(controls);
        ui.Toggle("Space", spaceOn);
        ui.Slider("Speed", speed, 0.2f, 3.f);
        ui.Close();
        space = mcx::Damp(space, spaceOn ? 1.f : 0.f, 2.5f, f.dt);
        if (f.focused) simTime += (double)(f.dt * speed); // paused while a system menu is open
        glow.Clear();
        const float sunGrow = GrowIn(f.seconds, started, 0.f);
        AddHalo(glow, centre, kSun * sunGrow, sunLight, 1.f + 0.15f * mcx::Pulse(f.seconds, 0.25f));
        for (int i = 0; i < kPlanetCount; i++)
        {
            const float a = kPlanets[i].phase + kPlanets[i].speed * (float)simTime;
            pos[i] = {centre.x + kPlanets[i].orbit * std::cos(a), centre.y, centre.z + kPlanets[i].orbit * std::sin(a)};
            trails[i].Push(pos[i]);
            trails[i].AddTo(glow, kPlanets[i].diameter * 0.6f, mcx::Vec3Scale(mcx::GfxSrgbRgb(kPlanets[i].colour), 0.25f));
        }
    }

    // one eye: the room or space, the bodies, the sky, the glow, the UI
    void Draw(const mcx::App& app, const mcx::Frame& f, const mcx::Eye& eye)
    {
        ClearRoomOrSpace(app, space, sky);
        if (started >= 0.0)
        {
            lit.light.sky = mcx::Vec3Lerp({0.16f, 0.17f, 0.20f}, {0.025f, 0.03f, 0.045f}, space);
            lit.light.ground = mcx::Vec3Lerp({0.08f, 0.075f, 0.07f}, {0.01f, 0.01f, 0.015f}, space);
            const float s = kSun * GrowIn(f.seconds, started, 0.f);
            lit.Begin(eye);
            lit.Draw(sphere, mcx::Trs(centre, mcx::QuatYawPitch(0.f, 0.f), {s, s, s}), sunMat);
            for (int i = 0; i < kPlanetCount; i++)
                DrawLitBySun(lit, eye, sphere, centre, pos[i],
                             kPlanets[i].diameter * GrowIn(f.seconds, started, 0.3f + 0.08f * (float)i), planetMat[i]);
            lit.End();
        }
        stars.intensity = mcx::EaseInOut(space);
        stars.Draw(eye);
        paths.Draw(eye);
        glow.Draw(eye);
        ui.Draw(eye);
    }

    void End() { ui.End(); }
    void Destroy()
    {
        ui.Destroy();
        glow.Destroy();
        stars.Destroy();
        paths.Destroy();
        sphere.Destroy();
        lit.Destroy();
    }
};

void android_main(android_app* android)
{
    mcx::Config cfg;
    cfg.name = "Orrery";
    cfg.passthrough = true;  // mixed reality (quest-native-hands-first)
    cfg.hands = true;
    cfg.controllers = false;
    cfg.foveation = 2;
    cfg.cpuLevel = 2;
    cfg.gpuLevel = 2;
    mcx::App app;
    if (!app.Init(android, cfg)) { while (app.Pump()) {} return; }
    Orrery orrery;
    if (!orrery.Init(app)) { app.Shutdown(); return; }
    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        orrery.Update(app, f);
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            orrery.Draw(app, f, eye);
            app.EndEye(e);
        }
        orrery.End();
        app.EndFrame();
    }
    orrery.Destroy();
    app.Shutdown();
}
```

Budget of this app (ours, counted): 6 lit draws (about 14,000 triangles), 3 sprite draws (about 1,200 sprites,
most of them a few pixels), the UI; well inside the numbers of quest-native-performance. Split it into the files of
quest-native-quality section 6 when you adapt it.
