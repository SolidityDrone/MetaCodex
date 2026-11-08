# Spatial dome: Ambient Focus from live depth, with blur (branch `feat/spatial-dome`)

Owner, 2026-11-08: no Space Setup needed ("look at SandyBridge: we don't use Space Setup and we still detect the
ground easily"); asking for the camera permission is fine. Copy only what the dome needs from SandyBridge, which
is far more complex (surface map, room layout, consensus). The branch can be dropped if this goes wrong.

## What SandyBridge does (and what we take)

| SandyBridge | Lines | Take? |
|---|---|---|
| Meta Depth API through `com.unity.xr.meta-openxr` 2.6.1 (OpenXR features `ARSessionFeature` + `AROcclusionFeature`), `EnvironmentDepthManager` | setup | **yes**: same Unity 6000.4.5f1 + Meta SDK 205, proven on the owner's Quest 3S |
| Floor = the tracking floor (floor-level origin), confirmed by the depth under the user's feet (`_feetFloor`, steady within 5 cm), since the Guardian floor can be off (28 cm above / 11 cm below the real one) | SurfaceMap ~40 | **yes, light**: a small depth readback every 0.3 s, no surface map |
| Planes, walls, room layout, consensus, meshing | ~10 000 | **no**: the dome needs a distance per pixel, not a model of the room |
| `PassthroughCameraAccess` (MR Utility Kit 205, `horizonos.permission.HEADSET_CAMERA`) | CameraFeed ~60 | **yes**: the camera image for the blur |

## Design

1. **Depth per pixel.** The focus sphere (drawn first, ZTest Always, as today) samples `_EnvironmentDepthTexture`
   (Texture2DArray, one slice per eye) along each pixel's view ray and rebuilds the real surface's world position
   with the inverse of `_EnvironmentDepthReprojectionMatrices` (computed on the CPU each frame, after Meta's
   manager updates in `onBeforeRender`). Darkness = intensity × smoothstep(radius, radius + softness, |P − feet|),
   feet = the floor under the head. Walls, furniture and your hands near you stay; far ones fade. No scan, no meshes.
2. **Without depth** (no permission yet, unsupported, Editor): the floor-plane estimate of 0.6 stays as fallback.
3. **Floor.** Tracking floor, corrected by the depth seen under the feet when it is steady (SandyBridge's rule,
   light): readback of a 64×64 copy of the depth every 0.3 s, points within 0.6 m of the head and > 0.8 m below
   it, lowest dense 2 cm level, accepted when 3 readings agree within 5 cm. The FOCUS tab shows the source.
4. **Blur.** A BLUR slider in FOCUS. When above 0, the camera (left, 640×480, 30 fps) starts after its permission;
   each new frame is copied into a small mip-mapped texture. Outside the dome the sphere draws the camera image at
   a high mip (soft), projected per pixel through the camera's pose and intrinsics (depth gives the right parallax),
   darkened by INTENSITY, with alpha = the dome mask. The camera sees less than the passthrough: beyond its edges
   the image is clamped (smeared colours) and fades to the plain darkness.
5. **Hands** keep the capsule cut-outs (needed at BLACKOUT, where the radius is 0).
6. **Removed:** the Space Setup room meshes from 0.7 (scene anchors, FocusMesh shader). The USE_SCENE permission
   stays: the Depth API needs it ("spatial data"), but no scan is needed.
7. **Cost:** depth runs only while focus is on; the camera only while BLUR > 0. One full-screen sphere pass with
   2-6 texture samples. 72 fps is the gate.

## Steps

1. Packages: `com.unity.xr.meta-openxr` 2.6.1, `com.meta.xr.mrutilitykit` 205.0.0; MetaCodexSetup enables the two
   OpenXR features (Android) and `isPassthroughCameraAccessEnabled`; manifest gets `HEADSET_CAMERA`. One batch
   Editor run to resolve and compile.
2. Depth dome (shader + `ShellAmbient`), floor estimator, removal of the room meshes.
3. Blur (camera, mip copy, projection), FOCUS tab: BLUR slider, status line (depth, floor, camera); voice
   `focus_blur`.
4. Editor tests with synthetic depth (a wall 1 m / 4 m away, the floor) and a synthetic camera image, like
   SandyBridge's SteadyDepthTest: alpha near 0 / far 1, floor estimate, blur colour outside, sharp inside.
5. Docs, commit, push to the branch. No APK until the owner asks.

## Risks

- Package resolution / compile errors after adding the packages (Library is rebuilt for them): on this branch only;
  revert = drop the branch and re-resolve.
- Depth is noisy at edges: a soft edge (softness ≥ 0.3 m) hides it; Meta's preprocessed (smoothed) depth is an option.
- The camera image is ~40-60 ms late and narrower than the view: acceptable for a blur, not for sharp detail.
