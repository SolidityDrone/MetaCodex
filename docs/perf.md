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

## Main-thread spikes and GC while coding (branch perf/gc-cpu)

Owner 2026-11-13: "stable fps above all; the app stutters 1-3 s every ~10 s while coding". The 0.7.19 session log of a
coding thread: main-thread callbacks of 2.0-2.9 s every 5-6 s, and a GC in every big hitch. The timeline's walks went
to the pool thread first (Frame stability commit). This change cuts what stays on the main thread:

| Before | Now |
|---|---|
| The GC stopped the frame for a full collection (the project setting was on, but no build step set it) | `ConfigurePlayer` sets `PlayerSettings.gcIncremental` for every build: the GC works in slices of a few ms across frames. The session log says `GC: incremental` at start |
| Each timeline update (every 0.8 s while the agent works) laid out every block of the conversation again: a copy of each block's wrapped lines, a header string per step, a search over all lines per drawn prompt | `UiChatView` keeps each block's laid-out lines while its key, text and state stay the same; only new and changed blocks are wrapped again. First and last line, key and prompt are indexed per block; a draw walks only the lines on screen. `[slow timeline]` says how many blocks were laid out again |
| A new chat rect (the approval row shows or goes) dropped all wrapped lines | the lines stay while the width is the same |
| The open thread's rows (every 2 s while it works) were all copied again and the workspace map made again | a row that did not change is not copied; the map is made again only after a change |
| CHANGES (each git refresh: every 4 s while the agent works) built all its rows and drew the list again | the rows are built only when a file, a count or the empty text changed (a hash over the fields) |
| The sidebar of a thread without a folder merged and drew CHANGES every frame | only when the timeline shows a new file |
| The orb's callouts were laid out every frame, also with no callout | nothing while no callout, no pinned panel and no rail shows |
| "The thread answered" words (title, project, the alert) were built at every pulse, once a second | only when the state changed to answered, failed or waiting |
| Scripts and plugins: the folder walk (every plugin file's time) ran on the main thread once a second (2-3 ms on the headset) | the walk runs on a pool thread; the main thread only loads what changed |
| GESTURES > LIVE, SETTINGS > GESTURES and the local coder tile built their texts every frame | 10 times a second (the bars stay every frame); the tile 4 times a second |

Closed windows already do nothing per frame: a closed panel is an inactive object (no `Update`, no `Tick`), and an
element on a tab that is not shown is not visible (no `Update`).

Check on the headset: a long coding thread open for 10 minutes. The session log must have no `[slow callback]` and no
`[slow timeline]` above 40 ms from the chat, and no hitch with a GC above 100 ms.

## Cheaper depth features (2026-11-13)

Measured on the Quest 3S (VrApi, the owner's settings, Ambient Focus off): 61-63 of 72 fps, GPU 98-99 % busy.
"Sharp depth" off: 65.8 fps. "Real things in front of windows" off too: 70.6 fps. So the camera-guided sharp depth
cost ~0.9 ms a frame, the depth with the UI's depth test ~1.0 ms. Both on again: 17.7 fps for ~15 s (a restart of
the depth and the camera). Both features stay; their cost goes down:

| Cost | Before | Now | Expected GPU saving (estimate, not measured yet) |
|---|---|---|---|
| Sharp depth pass (`DepthSharp`, per new depth frame) | 640 x 640 x 2; 3 x 3 readings, a median (81 compares), 10 camera taps with 2 matrices each, temporal with 2 matrices | guided: 480 x 480 x 2 (0.56 x the texels); without the camera 320 x 320 x 2 (0.25 x). 2 x 2 readings, the nearest one as reference, 5 camera taps with one matrix each (depth clip -> camera, from the CPU), temporal with one matrix | ~80 % of the pass: ~0.4-0.5 ms a frame guided, ~0.2 ms without the camera |
| Passthrough camera for the guide | 30 fps; a new component when the rate changed | 15 fps always (also for the blur) | about half the camera's frame work: ~0.1-0.2 ms |
| UI depth test (`McxOcclusion.hlsl`) | every fragment of every window: a 4 x 4 matrix, a divide, one depth tap | per window: a window that nothing real can be in front of (`_McxPanelClear`, from the room's 48 x 48 points every 0.3 s) skips it; still one tap where it runs | most of the test, ~0.2-0.4 ms with several windows open; none for a window with something (or a hand) in front |
| Restarts | the camera stopped during every voice command and started again after; the depth stopped when focus and occlusion were both off (also while the Horizon OS menu is open with occlusion off) | voice never stops the camera (its frames are not used meanwhile); the depth and the camera stay on, idle (no sharp depth drawn, no frames used), for 60 s after the last need | no 15 s at ~18 fps after each dictation |

Total expected: ~1.0-1.3 ms of the ~1.9 ms these features cost, so ~67-70 fps with both on (to measure on the
headset: VrApi FPS and GPU %, both settings on, then each off).

What was traded:
- Sharp depth edges: 480 x 480 instead of 640 x 640. The picture still moves an edge between two readings, but in
  steps of 2/3 of a reading instead of 1/2. Without the camera: the sensor's own resolution, sampled bilinearly.
- Noise: 2 x 2 readings instead of a 3 x 3 median, so one bad reading is not voted out; the temporal filter
  (70 % history) still averages it.
- The guide picture is up to 67 ms old (15 fps); its own pose is used, so this matters only for things that move.
- A window marked clear shows a real thing behind it for up to ~0.4 s after the thing comes in front (one reading
  and its read-back); a thing thinner than ~2 degrees can be missed. Hands always show (the hand cut-outs).
- The depth and the camera run idle for 60 s after you turn both features off.

## GPU level and the Quest rendering options (2026-11-13)

Measured on the Quest 3S up to 0.7.19: VrApi `CPU4/GPU=3/1, 1651/350MHz`, GPU 91-99 % busy. Cause: the build traded one
GPU level for one CPU level (`processorFavor = FavorCPU`, manifest `com.oculus.trade_cpu_for_gpu_amount = -1`). With
passthrough the Quest 3/3S caps are CPU 3 and GPU 2 (Meta, "CPU and GPU levels"), so the trade gave the CPU nothing and
held the GPU at level 1. Now: no trade (`FavorEqually`): GPU up to level 2, 456 MHz (+30 %).

The Meta Quest Support OpenXR feature was off on Android, so the options `ConfigureQuestRendering` sets (symmetric
projection, render regions, late latching, lazy MSAA buffers) never shipped. Now it is on, with Meta XR Foveation and
Subsampled Layout, so the dynamic foveation the shell asks for works.

Check: VrApi shows `GPU=.../2, .../456MHz`; `adb logcat | grep tradeCpuForGpu` shows 0; the Unity OpenXR diagnostic
report at start says "Symmetric Projection: True". Battery Saver (VrApi `LP=1`) must be off for comparable numbers.

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
missing opacity there, not the window behind (as everywhere else on the window). The occlusion test keeps the
per-window skip of "Cheaper depth features" (`McxVisibilityAtIn(slot, q)`). Check: `~/mcxwork/perf-ui/verify.py`
(CPU port of the old and new code: gates and early-outs differ by at most 0.05/255).


## First start: no stall while the userland unpacks (branch perf/first-run)

Measured on the Quest 3S after a clean install of the packed APK (log of 2026-11-14, launch 08:08:09): the app unpacks the
Linux userland (8,393 files, 362 MB, 9.4 s), copies 649 MB of models and starts the AI service and MQH while the user
takes the guided tour. Frames must stay at 72 fps with no stall over 50 ms. Everything below is "to measure on the
headset" (no device run was made for these changes). Compare with `grep -E '\[hitch\]|\[slow|installing the Linux|Linux installed|shell up' files/session_log.txt`.

| What it was | What changed | Measured |
|---|---|---|
| The Linux unpack (`Task.Run`: untar, chmod, symlinks, the overlay and the MQH bundle) and the AI runtime unpack ran on thread-pool threads at normal priority, on the app's cores 3-5, next to the main and render threads | `LowPriority.Run`: each is a dedicated background thread that sets itself to nice 10 (`setpriority` on its own thread id) before it starts; a pool thread would keep the lowered priority for the next job. The copies out of the APK stay on Unity's web-request thread (0.9 s for the 649 MB of models, ~2 s for the 118 MB rootfs) | to measure: `Linux installed in N s` was 9.4 s; expect a few seconds more while a core is busy, and no `[hitch]` between `installing the Linux userland 10%` and `Linux installed` |
| The install loop wrote a log line and built a string every frame (the log has ~700 `installing the Linux userland N%` lines, 13 ms apart, many with the same N; in a development build each line carries a stack trace) | one line and one string per percent (at most 100); the AI runtime's and the models' status text the same | to measure: ~100 `installing the Linux userland` lines instead of ~700 |
| `MqhHost.Tick` (every frame) read `mqh.applied` from the disk through `LinuxHost.MqhApplied`: a file read and a string per frame for the whole session | `LinuxHost.MqhApplied` keeps the text once read (reset when a bundle is laid or the userland is deleted; an empty result is not kept) | no per-frame file read |
| `[slow callback] 324 ms <RefreshTools>b__1` right after MQH came up (a scan of SYSTEM > TOOLS, started by the first-use check): the callback on the main thread parsed the output, made the cache JSON and the agents' inventory JSON (Newtonsoft's first use of the tool list: reflection) and wrote both files, then redrew the list of a window nobody had open | the worker thread does the parsing, both JSON texts and both file writes; the main thread only swaps the result in. `ShowTools` does not redraw while the SYSTEM window is closed (opening it or its TOOLS tab calls `RefreshTools`, which draws). The inventory write of the scheduled scan (45 s after start) is a worker job too. Log line `tools scan: applied on the main thread in N ms` appears only above 20 ms | to measure: no `[slow callback] ... RefreshTools` line; no `tools scan: applied` line (or a few ms) |
| Two stalls at Ambient Focus start, with the default settings ("real things in front of windows" and the depth-guided-by-camera are on, so the depth and the camera start with the app): `frame 403 ms, shell 0.9 ms` at the depth's first frame (08:08:25.7: the native start of the environment depth, its swapchain `RenderTexture::Create`) and `frame 278 ms, focus 158 ms` at the camera's start (08:08:25.9), both within 1 s of the 5.9 s start freeze and as the Linux began to install | `FocusInitMayStart` (ShellAmbient): the depth and the camera do not start until the Linux is Ready (or Failed), plus 1.5 s when it was installed in this session. They are native calls and cannot be spread over frames, so they move to a quiet moment: on a first start, after the unpack and before MQH's first minute; on later starts, as soon as `Prepare` is done (a few frames). Not in the Editor; gives up waiting after 180 s. The log says `ambient focus: depth and camera may start now (t=..., Linux ...)` | to measure: the two `[hitch]` lines move from t=11.0 / 11.2 s to just after `Linux installed`, the depth's `live` and the camera's `live` lines follow it ~1 s apart; none during `installing the Linux userland` |
| The start freeze: one frame of 5,861 ms ending 08:08:25.2 (OVRManager init 08:08:19.3, vault 20.2, AI prep 20.5, plugins 20.7, then nothing logged until `shell up: 20 panels` at 24.7: ~4 s inside `Build`) | **Not fixed, now measured.** Nothing in the code or the log says which piece takes the 4 s (the likely ones: building 20 panels' text meshes with dynamic SDF font atlases, which add glyphs on the main thread, and first-use costs of the first start such as the keystore key at 0.4 s). `Build` is one synchronous call; spreading it over frames (build the splash first, a few panels per frame, a guard in Update, replays for the Recentered and Focus events) is the real fix but touches every handler that expects panels to exist, and the SETUP lane edits the same method, so it waits for the numbers. New: `ShellStartTiming` marks each piece (vault + services, voice + scripts, context, dock and terminals, every panel by id, wrist, orb, splash) and logs one line | to measure: `[start] shell built in N ms, before the first mark A, <piece> ms, ...` (pieces under 15 ms left out; a mark lasts until the next one). Then the first `[hitch]` lines (TMP builds the texts at the first render) |
| Every `[MetaCodex]` line went to logcat with an 18-line managed stack under it (a development build captures the stack of each `Debug.Log` call: a stack walk on the main thread per line; the first-start logcat is 1 MB for ~7,000 lines, most of it stack) | `SessionLog.Init`: `Application.SetStackTraceLogType(LogType.Log, None)`; warnings, errors and exceptions keep their stacks. The session log file never had them | to measure: `adb logcat` shows one line per message; less main-thread time in the frames that log (the Linux unpack's percent lines, the `[hitch]` lines) |
| The voice decider's main-thread side while it embeds 1,916 examples (from the AI service running at 08:08:29.7 to ready at 08:09:12.1: up to 42 s on the AI cores): checked, no stall except one 54 ms callback (`<Call>b__1` at 08:09:12.102, `frame 69 ms`) where the model became ready. The examples are made on a worker thread (`Build`), each batch of 64 goes to the service over HTTP and its JSON is parsed on the worker (`QvacClient.Embed`); the main thread only stores 64 vectors per batch (~30 batches, microseconds each). The cache file of an earlier run was read on the main thread at every start (735,000 `ReadSingle` calls: tens of ms inside the start frame) | `ReadDeciderCache` reads and parses the file on a low-priority worker thread, the main thread only seeds the dictionary (1,916 inserts); the build waits for it (`_deciderCacheLoading`). `VoiceDecider.LastFinishMs` times the final step; the ready line says it. **Finding for the integrator:** the 2026-11-14 APK has no `qvac/decider-vectors.bin` (`unzip -l` shows only models.json, the four models, runtime.json, runtime.tar under `assets/qvac/`), which is why the examples were embedded on the headset (63 s after the launch) although the previous commit meant to pack them; now the seeding says so in the log | to measure: `voice: N decision vectors from the APK` (or `voice: no decision vectors in this APK (...)`); `voice: decision model ready: ... (built in N s; main-thread finish N ms)` |
