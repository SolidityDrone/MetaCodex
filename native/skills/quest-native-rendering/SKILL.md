---
name: quest-native-rendering
description: Makes a native Quest app built in MetaCodex (mcx_xr.h) look good and run fast with mcx_gfx.h - meshes and primitives (box, rounded box, sphere, capsule, cylinder, cone, torus, plane), lighting and materials (matte, a sci-fi glow with emissive and rim, glass), contact shadows that ground an object on the floor, real scale and the floor height, the per-eye draw loop and draw order (opaque, shadows, translucent back to front, UI), instancing, and the cost of drawing on Quest 3 and 3S (frame budget, draw calls, triangles, overdraw and blending over passthrough, sRGB, foveation and refresh rate through mcx::Config, mediump). Use when the user asks for a native app that looks flat, plain or unfinished, when you draw or place meshes, primitives, lights, materials, glow, glass or shadows, when objects float, sink or look the wrong size, or when drawing is slow or hot.
---

# Rendering: a native app that looks good and runs fast

Read first: **quest-native-quality** (the brief and six passes) and **quest-native-hands-first** (mandatory).
The kit is `native/xr/mcx_gfx.h`, next to `mcx_xr.h`. Names and sources: `references/api.md`. Budgets and the frame
logger: **quest-native-performance**. Palette: **quest-native-ui-design**. Model files: **quest-native-3d-models**.
Status: compiles with the NDK; passes its GPU tests on Mesa (GLES 3.2, software). Not yet run on a headset: check the
look on the device. Labels: **Meta** (path in `references/api.md`), **verified** (measured: the owner on a Quest 3S,
or Mesa here), **ours** (a MetaCodex choice).

## 1. Materials and light

Copy these presets. Tune the numbers on the headset.

```cpp
#include <mcx_gfx.h>

// Materials for the look. Colours are sRGB hex; the kit converts them to linear once.
mcx::Material MatGlow() // sci-fi: a dark body, a cyan glow and a rim of light
{
    mcx::Material m = mcx::Material::Glow(0x35D0FF, 0.9f);
    m.rim = 0.8f;
    return m;
}
mcx::Material MatMatte() { return mcx::Material::Matte(0x9DBDD2); }      // soft, no shine
mcx::Material MatGlass() { return mcx::Material::Glass(0x8FEBFF, 0.2f); } // for DrawTranslucent only
```

- `Matte`: roughness 0.85, a soft highlight. Most surfaces.
- `Glow`: body at 25% of the colour; emissive 0.8 x strength, added after the light (no shadow falls on it); rim 0.6 x strength.
- `Glass`: alpha 0.1-0.4, rim 0.9, glossy; draw it only with `DrawTranslucent`. `m.unlit = true` gives flat labels.
- Keep dark bodies above about 13/255 sRGB: LCDs cannot tell brightness below 13 of 255 apart (design/display.md).
- Keep `lit.light` at its defaults until the headset says otherwise.

## 2. The per-eye loop

Build meshes once, in `Init`. Each eye: clear, opaque, shadows, translucent.

```cpp
// uses MatGlow, MatMatte and MatGlass from section 1
struct Stage
{
    mcx::Lit lit;
    mcx::ContactShadow shadow;
    mcx::Mesh pod, orb, glassOrb;
    bool asked = false; // the room was asked for

    bool Init()
    {
        return lit.Init() && shadow.Init() && pod.Upload(mcx::MakeRoundedBox({0.30f, 0.20f, 0.30f}, 0.04f, 3)) &&
               orb.Upload(mcx::MakeSphere(0.12f, 32, 16)) && glassOrb.Upload(mcx::MakeSphere(0.20f, 32, 16));
    }
    // one eye, after BeginEye: opaque, shadows, then translucent
    void Draw(const mcx::Eye& eye, float floorY)
    {
        const float podY = floorY + 0.10f; // half the pod's height: it stands on the floor
        const float orbY = floorY + 0.70f; // the orb floats, so its shadow is faint
        const XrVector3f one{1.f, 1.f, 1.f};
        lit.Begin(eye);
        lit.Draw(pod, mcx::Trs({0.f, podY, -1.2f}, mcx::QuatYawPitch(0.3f, 0.f), one), MatMatte());
        lit.Draw(orb, mcx::Trs({0.5f, orbY, -1.2f}, mcx::QuatYawPitch(0.f, 0.f), one), MatGlow());
        lit.End();
        shadow.Draw(eye, {0.f, podY, -1.2f}, {0.30f, 0.20f, 0.30f}, floorY);
        shadow.Draw(eye, {0.5f, orbY, -1.2f}, {0.24f, 0.24f, 0.24f}, floorY, 0.3f);
        lit.Begin(eye);
        lit.DrawTranslucent(glassOrb, mcx::Trs({-0.5f, floorY + 0.8f, -1.6f}, mcx::QuatYawPitch(0.f, 0.f), one), MatGlass());
        lit.End();
    }
    void Destroy() { pod.Destroy(); orb.Destroy(); glassOrb.Destroy(); lit.Destroy(); shadow.Destroy(); }
};

// the height of the room's FLOOR in the space the objects are drawn in (app.local); `fallback` until the room is known
float FloorOr(const mcx::App& app, float fallback)
{
    for (const mcx::SceneObject& o : app.spatial.scene)
        if (o.located && o.labels.find("FLOOR") != std::string::npos) return o.pose.position.y;
    return fallback;
}

void android_main(android_app* android)
{
    mcx::Config cfg;
    cfg.passthrough = true; // the room is the backdrop: alpha 0 shows it
    cfg.scene = true;       // the room (USE_SCENE in the manifest, quest-native-xr)
    cfg.foveation = 2;      // medium: the objects sit in the centre of the view
    cfg.cpuLevel = 2;
    cfg.gpuLevel = 2;       // with passthrough the GPU tops out at level 2 (verified)
    mcx::App app;
    if (!app.Init(android, cfg)) { while (app.Pump()) {} return; }
    Stage stage;
    if (!stage.Init()) { app.Shutdown(); return; }
    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        if (!stage.asked && app.spatial.sceneAvailable && app.HasPermission(mcx::kPermScene))
            stage.asked = app.spatial.QueryScene();
        const float floorY = FloorOr(app, -1.5f); // ours: a stand-in until the room gives the real height
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            glClearColor(0.f, 0.f, 0.f, app.clearAlpha());
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            stage.Draw(eye, floorY);
            app.EndEye(e);
        }
        app.EndFrame();
    }
    stage.Destroy();
    app.Shutdown();
}
```

- `Begin` sets the lit state (depth test on, back faces culled, blending off, depth writes on). `End` restores the App
  defaults. Every `lit.Draw` and `lit.DrawTranslucent` sits between them.
- A negative scale mirrors the model, and it looks inside out. Bake mirrors with `MeshData::Append`.
- Many copies of one mesh: `mcx::Instances` with `lit.DrawInstanced`, one draw call.

## 3. Ground every object in the room

- Each object that rests on the floor gets one `shadow.Draw` call: centre, size in metres, floor height. The shadow fades
  with height (`fadeHeight`, 1.2 m). Use a `strength` of 0.3-0.6 (ours). A model from quest-native-3d-models gets the same call.
- Real scale: 1 unit is 1 metre; a table top is about 0.75 m high (ours). Meta: "Virtual objects should be arranged to
  match the size and depth of reality" (design/mr-design-guideline.md).
- Use the room's FLOOR. Never assume y = 0 is the floor. Check once: a cube at `floorY` must sit on the real floor. If it
  does not, fix the height in `config.h` and tell the user.
- Meta says a halo or a drop shadow on the floor pins an object in passthrough (design/mr-design-scene.md). Dynamic
  shadow maps cost a lot (documentation/native/android/po-art-direction.md).

## 4. Draw order

1. Opaque: `lit.Begin`, `lit.Draw`, `lit.End`. Writes depth.
2. Shadows: `shadow.Draw`. Darkens the floor. No depth writes.
3. Translucent: `lit.DrawTranslucent`, farthest first (sort by distance to `eye.pose.position`, ours). No depth writes.
4. UI last (quest-native-ui-design).

Meta: blended content writes no depth, so it can draw in the wrong order (essentials/compositor-layers.md).
`shadow.Draw` restores the GL state it changes, so it is safe between `Begin` and `End` (verified on Mesa).

## 5. Quest rules for drawing

| Rule | Value | Source |
|---|---|---|
| Frame budget at 72 / 90 / 120 Hz | 13.9 / 11.1 / 8.3 ms | Meta: documentation/native/android/os-missed-frames.md |
| Draw calls | 300 or fewer per frame | ours; Meta Quest 3/3S: 200-300 busy, 400-600 medium (documentation/unity/unity-perf.md) |
| Triangles | 1.3 M or fewer per frame | ours; Meta Quest 3/3S: 1.3 M-1.8 M (documentation/unity/unity-perf.md) |

- Each `Draw`, `DrawTranslucent`, `DrawInstanced` and `shadow.Draw` is one call. Triangles that cover many tiles cost
  more (Meta: documentation/unity/unity-perf.md). Prefer fewer, larger triangles.
- Each overlapping blended pixel costs more (Meta: design/design-graphic-rendering-pipeline.md). Keep translucent shapes
  few and small; alpha cards overdraw (Meta: documentation/native/android/po-art-direction.md). Glass draws only the
  faces that look at the eye.
- No full-screen effects such as bloom or motion blur (Meta: documentation/native/android/po-art-direction.md).
- Colour: the eye buffers are sRGB when the runtime offers `GL_SRGB8_ALPHA8` (native/xr/mcx_xr.cpp, `App::InitSwapchains`).
  Shaders write linear light. Give the kit sRGB hex, and never convert twice (design/display.md).
- Precision: `mediump` for colour and normals, `highp` for positions. Meta: 16-bit ALU instructions run faster and use
  less register space (documentation/native/android/ts-renderdoc-shaderstats.md). GLES 3 is legacy for Meta: new
  features are Vulkan only (documentation/native/android/os-vulkan-opengl.md).
- Foveation: `cfg.foveation` 2 (medium). Meta: about 25% gain in pixel-heavy apps, little for simple shaders. Keep text
  central. Compositor layers are not affected. Change the level between scenes (documentation/native/android/os-fixed-foveated-rendering.md).
- Levels: with passthrough, a Quest 3S keeps the GPU at level 2 at most and the CPU at level 3 at most (verified).
  Never add `com.oculus.trade_cpu_for_gpu_amount` to a passthrough app: a CPU-for-GPU trade dropped the GPU to level 1
  and gave the CPU nothing (verified). Meta: documentation/native/android/os-cpu-gpu-levels.md.
- Refresh: `cfg.refreshRate` (0 = the runtime default, 72 Hz for apps). Quest 3S lists 72, 80, 90 and 120 Hz; 96 and 100 depend on the display. The
  runtime rejects an unlisted rate: read `app.refreshRate` after `Init`. Hold the rate, or the store rejects the app
  (documentation/native/android/mobile-display-refresh-rate.md).
- Clear colour and depth each eye, as the example does. Meta: clear or invalidate each colour buffer before its first
  draw (documentation/native/android/po-advanced-gpu-pipelines.md).
- Edges: one sample per pixel (`sampleCount = 1`, native/xr/mcx_xr.cpp). Meta calls 4x MSAA cheap
  (documentation/unity/gpu-improved-algorithms.md). The kit does not enable it: ask the owner.

## 6. Checks before you call it done

1. Every `lit.Draw` and `lit.DrawTranslucent` sits between `lit.Begin` and `lit.End`, in the order of section 4.
2. Each object rests on the floor or floats for a reason, and its shadow matches its height.
3. The look holds with the room lights on and off (quest-native-quality, pass 3). `missed` is 0 over 30 s (quest-native-performance).
