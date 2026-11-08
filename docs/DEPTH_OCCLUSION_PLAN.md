# Sharp depth, real things in front of windows, blur fix (branch `feat/spatial-dome`, 2026-11-08)

Owner, after testing 0.7.0 on the Quest 3S:
1. BLUR at 0 or 100 makes no difference.
2. Hands are hidden behind windows ("cut off in rendering order"), and the Horizon OS pinch menu cannot be seen.
3. The dome's cut-out flickers and is pixelated: the headset's depth is coarse. Do as in SandyBridge: use the
   sharper RGB camera to upscale the depth (SandyBridge `docs/DEPTH_UPSCALE_PLAN.md`, option A).
4. Use the depth so that real things in front of windows stay visible ("it's risky, we may not see objects").
5. VOICE: name the installed models, quantization included.

## Findings

- Blur: both permissions are granted on the headset; the session log shows `MaxFramerate can only be changed while
  PassthroughCameraAccess is not running` each time BLUR starts the camera (harmless: the value stays 60). Frames were
  copied from MetaCodex's own Update, which can run before the camera's Update, so new frames may never have been
  copied; the camera's state was not logged, so the rest is unknown. Fix + log every state change.
- SandyBridge never ran an RGB-guided upscaler on the device: its bench (`Bench/depthup.py`, 1280x960 pictures)
  measured edge distance 4.12 px (nearest) -> 2.99 (joint bilateral) -> 2.52 (weighted median). The camera covers
  about 30 % of the depth image (the centre of the view). Its occlusion (`docs/OCCLUSION.md`) is map-based (too
  heavy to take).
- Windows are drawn over the passthrough, so anything real in front of them (hands, people) is covered; the hand
  cut-outs were drawn before the windows.

## Design

1. **Sharp depth** (`Resources/Shaders/DepthSharp`, run each frame while the depth is live): a 640 x 640 linear depth
   per eye (2x the sensor), joint bilateral upsampling of the 3x3 sensor neighbourhood guided by the camera colour
   where the camera sees (projected through its pose and intrinsics), an edge-preserving depth-only filter elsewhere,
   and a temporal filter (the previous sharp depth reprojected; kept where it agrees within 2 cm + 2 %, so moving
   things follow at once). Dome and occlusion read it (`_McxDepthSharp`).
2. **Occlusion of the UI** (`Shaders/McxOcclusion.hlsl`, included by SciFiUI, TextBend, UiIcon, ViewImage, PanelUnlit,
   StageLit): a fragment whose real surface (sharp depth along its view ray) is 2-6 cm or more in front of it fades
   out, so hands, people and objects in front of a window show. Setting "Real things in front of windows" (on).
   The terminal's glyphs move to TextBend.
3. **Hands** last: the capsule cut-outs draw after every window (Overlay queue, depth-tested against the opaque
   panels), whenever hands are tracked.
4. **Horizon OS menu**: while a hand does the system gesture or the app loses input focus, the windows and the
   focus darkness fade out (global `_McxUiFade`), so the system menu shows.
5. **Blur**: frames copied in LateUpdate when the camera's timestamp changes; the pose and intrinsics are checked;
   every camera state change is logged; the camera also runs for the sharp depth (FOCUS or occlusion on).
6. **VOICE**: each model line shows its name with quantization, e.g. "Qwen3 0.6B instruct, tool calling (q4_0)".

## Tests (Editor, synthetic depth and camera)

Sharp depth edges follow the picture's edge; temporal noise drops; a panel with a hand-sized object 30 cm in front
fades there and stays elsewhere; a hand capsule clears a window; the fade on input focus loss; blur state machine.
