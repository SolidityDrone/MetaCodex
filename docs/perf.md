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
