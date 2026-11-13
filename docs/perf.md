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

## Smooth start and Ambient Focus (0.7.19)

Owner 2026-11-13: "performance seems quite bad during ambient focus", "at startup we have a little bit of stuttering ...
the first 10 seconds shall be lag-free". No headset trace was taken for this change (the headset was not connected);
these are the causes found in the code, fixed, and a log that measures what is left:

| Cause | Fix |
|---|---|
| Every userland command (git checks, `ps`, `mcx-project`, terminals) started with `fork()` of the app: on the headset the app maps gigabytes, so fork copied its page tables and made every page copy-on-write; the app's threads then took a fault on each page they wrote | `native/pty/mcx_pty.c` uses `vfork()` (memory shared until `execve`, only the calling thread waits, microseconds); signals stay blocked across it; the child runs at nice 10, below the frame loop |
| A settings change from MQH (often the first sync, a few seconds after start) rebuilt every panel: hundreds of text meshes at once | the values apply in place (`OnSettingsChanged` + `SyncSettingsControls`); only an accent change still rebuilds |
| The first process sample (`ps` for the session context) ran in the first frame | first sample at 12 s |
| The sharp depth (640 x 640 x 2) was computed every frame, with a new command buffer each time (garbage), although Meta's depth comes slower than 72 Hz | computed only for a new depth frame (its matrices change), one command buffer reused |
| Ambient Focus draws a full-view pass on every eye-buffer pixel at RenderScale 1.6 | foveation (dynamic, up to High) is on while the focus is shown, also with FOVEATED off: the edges of the view are dark or blurred then |

The hitch log: a frame that takes more than 25 ms writes `[hitch] t=<s since start> s, frame <ms>, shell <ms>, <step> <ms>...,
GC` into the session log (`files/session_log.txt`; at most 4 lines a second, 400 a session). The steps are the shell's
Update: callbacks (work queued from other threads), scripts, mqh, voice, context, focus, rebuild, later, sync, layout, orb.
A long frame with a small "shell" time is the GPU, the render thread or a shader compiled at its first use.

Next, if the log shows first-use shader hitches: record a `GraphicsStateCollection` on the headset and warm it up at
start (Unity 6, Vulkan PSOs).

## Cheaper UI shader (branch perf/ui-shader)

The app was GPU-bound (98-99 % busy at idle with a few windows). Every UI pixel runs `SciFiUI`. Changes, same look:

| Change | Saved per pixel |
|---|---|
| Stone armour of a window (rails, corner pieces, crest) only within 3 cm of its frame; stone and normal only where a piece covers the pixel | ~290 ALU + ~20 transcendentals on most body pixels |
| Stone grain from a tiling texture (`_McxStoneNoise`, 512 x 512, 13.2 cm) | 12 `sin` hashes + ~100 ALU -> 1 tap, per stone pixel |
| Hexagon lattice distance from a texture (`_McxHexField`, one period) | ~30 ALU -> 1 tap; the formula stays for the cast |
| `ChamferNormal` analytic | 4 SDF calls -> 1 |
| Halo only outside a shape; outside a button, selection, bar or flat only the halo (none: transparent, no depth tap) | ~15 ALU, or the whole fill |
| Pointer light only while a hand is near | ~16 ALU |
| Occlusion's 4x4 product per vertex (`McxOcclusionCoord` / `McxVisibilityAt`), also in `TextBend` | ~20 ALU per UI and text pixel |
| Body depth of all windows first (queue 2989) | the hidden pixels of windows behind are rejected by the depth test |
| Quads of lines and solid pills keep 0.8 mm (AA) instead of 1-2 mm | fewer margin pixels |

What looks different: the grain has other random values (same scale and contrast) and repeats every 13.2 cm; at a
distance its finest octave is filtered instead of shimmering; the bevel's mitre lines are sharp (they were blended over
0.4 mm). Where a window overlaps another at PANEL OPACITY below 100 %, the room shows through the front window's
missing opacity there, not the window behind (as everywhere else on the window). Check: `~/mcxwork/perf-ui/verify.py`
(CPU port of the old and new code: gates and early-outs differ by at most 0.05/255).

