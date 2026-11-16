---
name: quest-native-recipes
description: Worked examples and copy-ready recipes for native Quest apps built in MetaCodex on mcx_xr.h - Night Vision (environment depth shaded as night vision over a green passthrough, hands and controllers, HUD, room, anchors), a floating panel or clock over passthrough, pinch and controller buttons, placing things on the real table or wall from the room data, anchors that come back after a restart, and the Quest 3S frame budget (72 fps, foveation, CPU/GPU levels, draw calls). Use when writing a native Quest app (after quest-native-apps chose the base) and you want working code for a common need.
---

# Recipes for native Quest apps (mcx_xr)

Start the app with `mcx-app new /root/metacodex-projects/native/<name> --example nightvision --label "<Label>"` (quest-native-apps), keep
its `android_main` loop and replace its scene with a recipe. Every recipe runs on `mcx::App`, so every app has the
app menu (Back to MetaCodex / Report / Quit): keep it, add items with `app.menuItems` (quest-native-apps, Always). Every recipe uses only `mcx_xr.h` (quest-native-xr) and
GLES 3. After any change: `mcx-app check`, `mcx-app build`, `mcx-app verify`.

| Recipe (`references/recipes.md`) | For |
|---|---|
| 1. A floating panel with text over passthrough (a clock) | utility apps: timers, notes, dashboards |
| 2. Pinch and controller buttons | one input path for hands and controllers |
| 3. Things on the real table / wall (the room) | room-aware placement |
| 4. Anchors that come back after a restart | persistent placement |
| 5. Frame budget on the Quest 3S | keeping 72 fps |
| 6. The app's own Library icon (pure Python) | every app made from an example |

Whole designed apps that compile, to adapt:
- `references/desk-game.md`: a breakout game on the real table (Neon arcade look, the TABLE from the room, the
  paddle under the hand, instanced bricks, sparks, a trail, start / HUD / result panels).
- quest-native-rendering `references/effects.md`: a solar system in the room (Cosmic look, glow, trails, a
  starfield, grow-in motion, a start panel and a control panel).

Pick the look first: quest-native-art-direction.

## Night Vision, the full example (`/opt/metacodex/quest-native/examples/nightvision`)

`src/main.cpp` (~290 lines) uses every feature at once: read it before writing a mixed-reality app.

- **Config**: `cfg.passthrough = cfg.depth = cfg.hands = cfg.controllers = cfg.scene = cfg.anchors = true`,
  `cfg.foveation = 2`, `cfg.cpuLevel = cfg.gpuLevel = 2`.
- **Depth as an image**: per eye a full-screen triangle; the fragment's ray (eye pose + fov tangents) -> a point
  3 m out -> `f.depth.viewProj[eye]` -> uv -> depth from the texture array, metres from near/far, edges from the
  depth's Laplacian; green phosphor, grain, scanlines; three modes.
- **Passthrough**: green through `app.passthrough.SetMonoToRgba(...)`; toggled with `Start()`/`Pause()` and
  `app.projectionFlags`; output premultiplied (`vec4(col * v * a, a)`).
- **Input** (only when `f.focused`): a `pinchStarted` of either hand or the right `triggerPressed` = next mode; both
  hands pinching together (the edge) or A/X `primaryPressed` = passthrough on/off; `stick.y` = gain; left `squeeze` =
  `CreateAnchor` at the grip; `app.controllers.Haptic(1, 0.4f, 0.05f)` as feedback. This is the example's rule
  (quest-native-hands-first, rule 1); a pinch with the palm towards you does not count.
- **HUD**: `PlaceHud` (lazy follow, 1.2 m ahead, 25 cm low, head yaw only) + `app.text.Rect` + `app.text.Draw`,
  showing the state of depth, hands, controllers, room and anchors.
- **Room / anchors**: `QueryScene()` once `app.HasPermission(mcx::kPermScene)`; `LoadAnchors()` at start.
- **Manifest**: PASSTHROUGH feature, USE_SCENE, USE_ANCHOR_API, HAND_TRACKING + handtracking feature, icon,
  supportedDevices `quest3|quest3s`. Measured on the Quest 3S: builds in 1.5 s after a change, `verify ok`.
  Not yet seen running on the headset: when the user first tries it, ask what the HUD's `depth` line says.

## Comfort and design

For comfort, layout and interaction choices (distances, text sizes, locomotion, hands vs controllers) follow the
bundled skill **hz-immersive-designer** (its guidance is engine-independent). Short rules: content 1-3 m away, text at least 2.5 cm tall at 1 m, nothing
head-locked, no camera moves the user did not make, everything works with the hands (quest-native-hands-first:
controllers only when the user asks for them).
