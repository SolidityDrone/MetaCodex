---
name: quest-native-3d-models
description: 3D models in native Quest apps made in MetaCodex - a glTF file (.glb or .gltf, for example a chair, a figure, a prop, or an asset from Kenney, Quaternius or Poly Haven) or a shape made from primitives. Loads it from the app's assets folder with mcx_model.h, fits it to a height in metres, places it on the floor or in front of the user, draws it lit per eye, keeps it inside the Quest budgets (triangles, draw calls, textures), and records its licence. Use when the user asks to show, add, place, import, load or fix a 3D model, mesh, object or asset, or when objects look wrong in size, direction or shading.
---

# 3D models in native apps

Read first: **quest-native-apps** (the flow), **quest-native-hands-first** (mandatory), **quest-native-xr** (the room and
anchors). Models use `mcx_model.h`, next to `mcx_xr.h`. Every name: `references/api.md`.

## 1. Choose the source

| Source | Use for | Rule |
|---|---|---|
| Primitives (box, sphere, cylinder, capsule) from `mcx_gfx.h` | stylised shapes, placeholders | no file, so no licence row |
| A glTF you export (Blender: glTF 2.0, .glb, +Y up) | a real object | record it as your own work |
| Kenney, Quaternius, Poly Haven | props, furniture, plants | read each download's licence file; most are CC0, but check |
| Khronos glTF-Sample-Assets | tests only | each model has its own licence |

Licence rules:
- Give every asset one row in the app's `README.md`: name, author, URL, licence (SPDX id), your changes.
- Use a clear licence only: CC0, CC-BY (with the credit in the README), or one that allows reuse. Never NC or ND, and
  never an asset without a licence file.
- Keep the licence file in `assets/` next to the model when the licence asks for it.

## 2. How the file reaches the headset (verified)

- `mcx-build` packs `<app>/assets/` into the APK (`aapt2 link -A assets`: `userland/overlay/usr/local/bin/mcx-build`,
  function `aapt2_base`).
- Put the file at `<app>/assets/models/chair.glb`. Load it with
  `LoadAsset(app.android_->activity->assetManager, "models/chair.glb")`.
- A `.gltf` finds its `.bin` and images in the same folder inside `assets/`.
- Files outside `assets/` are not in the APK. `Load(path)` reads a file path, but the app has no such file unless it
  writes one first.

## 3. Budgets for Quest 3 and 3S

Meta's numbers (metavr docs: Unity performance, Memory, Mobile GPUs and tiled rendering):

| Measure | Meta (Quest 3, 3S) | This skill keeps the app to |
|---|---|---|
| Draw calls per frame | 200-300 busy, 400-600 medium, 700-1000 light | 300 in total (`Primitives()` counts one model's draw calls) |
| Triangles per frame | 1.3 M to 1.8 M (Meta's range, all content) | 200 000 in total (`Triangles()`) |
| Triangles per model | not stated | hero 30 000; prop 5 000-10 000; ornament under 2 000 |
| Base colour texture | not stated on the pages read | 1024 px; 2048 px for one hero texture |
| Texture memory | 5.75 GiB app limit (PSS) | 1024 px RGBA = 4 MiB (5.3 MiB with mipmaps); all textures under 64 MiB |
| Materials | - | one material per mesh: each primitive is one draw call |

Every triangle costs one binning pass, then one pass for each tile it touches. Many tiny triangles cost more than
their count. Log the counts once after the load:
`mcx::Log("model %s: %d triangles, %d draw calls", m.Name().c_str(), m.Triangles(), m.Primitives());`

## 4. Units and orientation

- glTF is metres, Y up. Read the height with `Bounds().Size(1)`. A height 100 times too big or small means the file is
  in centimetres: `FitHeight` fixes the scale.
- `FitHeight(h)`: the model is `h` metres tall, its lowest point is on y = 0, centred on x and z = 0.
- Turn a model with the world matrix you pass to `Draw`, not with `SetRoot` (`FitHeight` replaces the root).
- Check the front on the headset. Turn a model that faces away 180 degrees about Y (see the example).

## 5. Where the model goes

- **Floor**: set `cfg.scene = true`, then find the FLOOR object (recipe 3, quest-native-recipes) and take its pose y as
  the floor height. Check that height on the headset once. With no room, use a height you set and say so to the user.
- **In front of the user**: 0.8 to 1.2 m along the horizontal part of `mcx::Forward(f.views[0].pose)`, at floor height,
  turned to face the user.
- **Pinned**: an anchor (`cfg.anchors = true`, recipe 4). Each frame use `mcx::Mat4::FromPose(anchor.pose)` as the world.
- A model the user grabs follows the hands rules: pinch inside the object, follow the index tip while pinching.
- `mcx_gfx.h` draws a soft contact shadow (`mcx::ContactShadow`). This kit does not draw shadows.

## 6. The look

- Set the light once with `renderer.SetLight(sky, ground, sun, dir)`. Colours come from `mcx::ModelSrgb(0xRRGGBB)`.
  The defaults suit a bright room: tune them on the headset.
- Base colour (with its texture), metallic and roughness factors and emissive are read from the file. Cut-outs use
  alpha mode mask; glass uses blend.
- Blend parts draw last inside a model. The kit does not sort models: draw glass objects from far to near yourself.
- Match the app's look (quest-native-art-direction): pass the look's ambient and key light to `SetLight`. A model
  viewer takes the Gallery look: the model on a matte plinth (`mcx::MakeRoundedBox`, the body tone), a contact
  shadow under it from its bounds (`mcx::ContactShadow`, quest-native-rendering), the menu panel beside it.
- A viewer's motion: the model grows in (`mcx::EaseOut`, 0.4 s), turns slowly when idle (10-15 degrees per second),
  and turns with the hand while the user pinches beside it (the change of the index tip's angle round the model,
  smoothed with `mcx::Damp`). Two hands pinching scale it, between 0.5 and 2 times the fitted size.
- The file is missing or fails to load (`Load` or `LoadAsset` returns false): log the error, draw a stand-in of the
  same size (a rounded box in the support tone, with its shadow), and say in one line on the panel what is missing
  ("Model file not found: models/chair.glb"). Never show an empty scene.

## 7. Worked example

```cpp
#define MCX_MODEL_IMPLEMENTATION // only in this one .cpp; the other .cpp files include mcx_model.h without it
#include <mcx_model.h>
#include <cmath>

void android_main(android_app* android)
{
    mcx::Config cfg;
    cfg.name = "Chair viewer";
    cfg.passthrough = true;   // mixed reality: the room shows around the model
    cfg.controllers = false;  // hands only
    mcx::App app;
    if (!app.Init(android, cfg)) { while (app.Pump()) {} return; }

    mcx::Model chair;
    mcx::ModelRenderer renderer;
    const bool loaded = chair.LoadAsset(app.android_->activity->assetManager, "models/chair.glb");
    if (loaded) chair.FitHeight(0.9f); // 0.9 m tall, lowest point on y = 0
    renderer.Init();
    renderer.SetLight(mcx::ModelSrgb(0xCFE6FF), mcx::ModelSrgb(0x5A5048), mcx::ModelSrgb(0xFFF1E0),
                      XrVector3f{0.4f, 0.8f, 0.5f});

    // 1 m ahead and 1.2 m below the start point (use the room's floor height when you have it).
    // A front that faces away: use the orientation {0, 1, 0, 0} (180 degrees about Y) instead.
    const XrPosef place{{0.f, 0.f, 0.f, 1.f}, {0.f, -1.2f, -1.f}};
    const mcx::Mat4 world = mcx::Mat4::FromPose(place);
    bool logged = false;

    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        if (loaded && !logged)
        {
            mcx::Log("chair: %d triangles, %d draw calls, %.2f m tall", chair.Triangles(), chair.Primitives(),
                     chair.Bounds().Size(1));
            logged = true;
        }
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            glClearColor(0.f, 0.f, 0.f, app.clearAlpha());
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            if (loaded) renderer.Draw(eye, chair, world);
            app.EndEye(e);
        }
        app.EndFrame();
    }
    renderer.Destroy();
    chair.Destroy();
    app.Shutdown();
}
```

## 8. Checks before you say it is done

1. `mcx-app check <dir>` and `mcx-app build <dir>` pass.
2. `mcx-app verify <dir>` prints `RESULT: verify ok`.
3. The log has your count line with the height you expect, and no `model ...:` error line.
4. On the headset: the model is the right size beside a known object, stands on the floor, faces the user, and the
   frame rate holds (quest-native-performance).
5. The README has one licence row for each asset.
6. If `#include <mcx_model.h>` fails, this build has no model kit yet: tell the user, and use primitives meanwhile.
