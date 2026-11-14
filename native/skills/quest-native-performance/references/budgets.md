# Budgets and their sources

Labels: **Meta** = Meta's developer docs (read them with `metavr docs fetch "<path>"`, search with
`metavr docs search "<query>"`). **Verified** = the owner's tests on a Quest 3S (MetaCodex device tests).
**Ours** = a MetaCodex target. The docs give no such number; it is a choice, not a Meta rule.

## Frame time (Meta)

| Refresh | Budget per frame | Doc path |
|---|---|---|
| 72 Hz | 13.9 ms (13888 us) | `documentation/native/android/os-missed-frames.md`, `documentation/native/android/ts-ovr-best-practices.md` |
| 90 Hz | 11.1 ms (11111 us) | same |
| 120 Hz | 8.3 ms (8333 us) | same |

- A missed frame is a stale frame: the compositor shows the previous frame with TimeWarp. A low GPU% can hide a
  miss, so read the app's own GPU time. Doc: `documentation/native/android/os-missed-frames.md`.
- Refresh rates on Quest 3S: 72, 80, 90, 120 Hz. 96 and 100 Hz depend on the display. Doc:
  `documentation/native/android/mobile-display-refresh-rate.md`.
- 60 Hz is for media apps only. Other apps that use it fail store review. Same doc.
- The default is 72 Hz. An app must hold the rate it requests, or it fails store review and shows judder. Same doc.
- Under thermal pressure the OS can drop a higher rate to 72 Hz. Doc: `documentation/native/android/os-missed-frames.md`.
- Meta's VRC performance rule: an app keeps at least 60 fps, and does not stay below its requested refresh rate
  for long periods (loading screens and black screens are exempt). Doc:
  `documentation/native/android/ts-ovr-best-practices.md`.

## Scene (Meta, Quest 3 and 3S)

| Item | Meta range | Doc path |
|---|---|---|
| Draw calls per frame | busy simulation 200-300; medium 400-600; light 700-1000 | `documentation/unity/unity-perf.md` (Draw Calls on Meta Quest) |
| Triangles per frame | 1.3 M to 1.8 M | `documentation/unity/unity-perf.md` (Triangle Counts on Meta Quest) |
| Memory limit per app | 5.75 GiB PSS | `documentation/native/android/po-memory-ram.md` |
| Eye buffer size | Quest 3 default 1680 x 1760 per eye; one RGBA8 image is about 11 MiB | `documentation/native/android/gpu-tiled.md` |
| Shader switch cost | about 175% more draw-call time than no switch (Unity test); a material switch about 64% | `documentation/unity/po-draw-call-analysis.md` |
| Repeat draw cost | a repeat of the same object about 25% of a different object (Unity test) | `documentation/unity/po-draw-call-analysis.md` |
| Shared meshes, few shaders | share meshes and instances; a minimal set of shaders; texture arrays | `documentation/unity/unity-perf.md` |

The draw-call and triangle ranges come from Meta's Unity page. Meta gives them for Unity apps. MetaCodex runs on
the same GPU and compositor, so use them as a guide for native apps too (our reading, not a Meta statement).
The shader-switch numbers are Unity tests: use them for the direction, not the exact cost.

## Tile GPU rules (Meta, native)

- Clear or invalidate each colour buffer before the first draw. Skipping both slows the frame. Doc:
  `documentation/native/android/po-advanced-gpu-pipelines.md`.
- Invalidate depth (and MSAA) after the frame when nothing reads it later: `glInvalidateFramebuffer` before flush.
  Same doc.
- A pass that reads the output of an earlier pass (bloom, blur) needs a resolve. Reading main memory is about ten
  times slower than reading tile memory. Doc: `documentation/native/android/gpu-impaired-algorithms.md`.
- Each triangle's vertex shader runs once per bin it touches. Fat triangles cost less than thin ones. Same doc.
- 4x MSAA is cheap on these GPUs, and Meta recommends it. Doc: `documentation/native/android/gpu-improved-algorithms.md`.
  The MetaCodex eye buffers are single-sampled today (`sampleCount = 1` in `native/xr/mcx_xr.cpp`).

## CPU and GPU levels (Meta, with the passthrough column verified)

Quest 3 and 3S availability (Meta's table in `documentation/native/android/os-cpu-gpu-levels.md`):

| Level | Available when | With passthrough |
|---|---|---|
| CPU 0-3 | always | yes |
| CPU 4 | the app enables no passthrough feature | no |
| CPU 5 | CPU 4 is available and the trade is -1 | no |
| CPU 6 (Boost) | CPU Boost is enabled | not measured |
| GPU 0-2 | always | yes |
| GPU 3-4 | the app enables no passthrough feature | no |
| GPU 5 | GPU 4 is available and the trade is +1, or dynamic resolution is on | no |

- Meta's ProcessorPerformanceLevel ranges: PowerSavings CPU 0-4 / GPU 0-4; SustainedLow CPU 2-4 / GPU 1-4;
  SustainedHigh CPU 4 / GPU 3-5; Boost CPU 4 to the device maximum / GPU 3-5. No level set means SustainedHigh.
- **Verified** on a Quest 3S: with passthrough the GPU is capped at level 2 and the CPU at level 3.
- **Verified** on a Quest 3S: with passthrough a CPU-for-GPU trade drops the GPU to level 1 and gives the CPU nothing.
  Never add `com.oculus.trade_cpu_for_gpu_amount` to a passthrough app. Trade values (Meta): 1 gives one level to the
  GPU and takes one from the CPU; -1 is the reverse; 0 is no change. Trading works on Quest 3 and 3S only, and it is
  fixed in the manifest. Doc: `documentation/native/android/po-quest-boost.md`.
- Boost (Meta, same doc): CPU level 4 to 8 on Quest 3 and 3S (about 23% faster). It runs only when battery saver is
  off, the system is not throttling, it has run for at most 45 s in a row, and it is at most 20% of the run time.
  Meta's use: loading screens.
- Dual-core mode is for Quest 2 and Quest Pro only (Meta, same doc).
- The mcx values 0-3 map to OpenXR performance levels: `native/xr/mcx_xr.cpp`, `App::SetPerformance`.
- Meta's Glasses page uses CPU 4 / GPU 4 as the nominal target. That is another headset, not a Quest 3S
  passthrough target. Doc: `documentation/native/android/optimize-performance.md`.

## Foveation (Meta)

- Fixed foveated rendering lowers resolution at the edges. Meta: about 25% gain in pixel-heavy apps; very simple
  shaders can get slower. Doc: `documentation/native/android/os-fixed-foveated-rendering.md`.
- Put high-contrast items, such as text, at the centre of the view. Same doc.
- Change the level between scenes, not often inside one scene. Same doc.

## Passthrough (Meta)

- Each extra composition layer costs a significant amount of compositor time. Prefer alpha in the app's own layer,
  which is what mcx does. Doc: `documentation/native/android/mobile-passthrough-customization.md`.

## Not used by MetaCodex (Meta, for reference)

- Application SpaceWarp renders at half rate and synthesizes the rest. It needs motion vectors and depth from the
  app. Not in `native/xr/mcx_xr.h`. Doc: `documentation/native/android/os-app-spacewarp.md`.

## What MetaCodex can measure

- The frame logger in SKILL.md: `dt` (frame interval) and CPU work (BeginFrame to EndFrame).
- `mcx-app logs <dir>` shows the log file. A Report sends the last 60 log lines with the picture (`RecentLog(60)`
  in `native/xr/mcx_xr.cpp`).
- Not available here: logcat, VrApi stats (App GPU time, Stale), the OVR Metrics Tool, RenderDoc.

## MetaCodex targets (ours)

| Item | Target | Reason |
|---|---|---|
| Missed frames | 0 in 30 s of normal use | the user sees judder from a few misses |
| Work | under 70% of the budget on average | room for spikes |
| 90 or 120 Hz | only when the heaviest scene holds 70% of that budget | Meta: the app must hold the rate |
| Draw calls | 300 or fewer per frame | the busy row of Meta's Quest 3/3S range |
| Triangles | 1.3 M or fewer per frame | the low end of Meta's Quest 3/3S range |
| GPU textures | 128 MB or fewer, eye buffers not counted | small apps; room for the system |
| Full-screen passes | one per eye | a pass that reads an earlier pass's output costs a resolve (Meta) |
| Text and Rect calls | 50 or fewer per frame | each call re-uploads its vertices (`native/xr/mcx_xr.cpp`, `DrawVerts`) |
| Physics | 2 ms of CPU per frame at most, fixed time step | leaves time for drawing |
| Log lines | one every 5 s | the log socket and the log file |
| Asset loads | before the first frame, or in a loading state | Meta: frame dips during loading are expected |
