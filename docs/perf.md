# Stable frame rate: rendering options (branch perf/stable-fps, 0.7.18)

Owner 2026-11-12: "improve greatly the stability and performance overall. Late Latching and foveated optimizations, so
we have stable fps even during the hardest tasks". Facts from Meta's docs (`metavr docs fetch
documentation/unity/unity-openxr-settings-quest.md`, `enable-late-latching.md`, `enable-phase-sync.md`,
`unity-fixed-foveated-rendering.md`), applied by `MetaCodexSetup.ConfigureQuestRendering` (Unity's Meta Quest OpenXR
feature) and `MetaCodexShell` (OVRManager) on Vulkan + multiview (this project's setup):

| Option | What it does | Here |
|---|---|---|
| Dynamic fixed foveated rendering | Horizon OS raises foveation only while the GPU is busy, up to the set level | on with FOVEATED (level High as the ceiling; before: always High) |
| Symmetric Projection | symmetric eye buffers the GPU renders more efficiently; Meta: 5-15 % in GPU-bound scenes, no visible change, "enable for most applications" | on |
| Multiview Render Regions (final pass) | skips the eye-buffer pixels outside the view that symmetric projection adds (Unity 6.1+) | on (FinalPass; AllPasses not tried) |
| Late Latching | head and hand poses taken at the end of the render thread: up to one frame less motion-to-photon latency | on (debug mode off) |
| Optimize Buffer Discards | memoryless MSAA buffers on the tile GPU | stays on |
| Phase Sync | adaptive frame timing (less latency) | off: Meta warns it "might cause more stale frames" when the workload fluctuates, as ours does (AI, builds, agents) |
| Eye-tracked foveation | - | not available: the Quest 3 / 3S have no eye tracking |

The CPU side was already split: the local AI and its children run on cores 0-2 (nice 10), Unity on the others; the local
coder waits for voice and the other way round (docs/local-coder.md).

Check on the headset (owner): 72 fps with many windows open, during a long spoken reply, a native build and a local-coder
turn; text at the window edges while idle (dynamic foveation lowers itself) and under load; Ambient Focus / occlusion and
bent windows look the same as before with symmetric projection (custom shaders read the per-eye matrices); hands and
the ray stay glued to the real hands when moving fast (late latching). `adb logcat -s VrApi` shows FPS and stale frames.
