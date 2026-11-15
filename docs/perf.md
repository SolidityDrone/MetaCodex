# Frame rate: how MetaCodex keeps 72 fps

The GPU is the tight resource. With passthrough the headset caps the GPU at level 2 (640 MHz at most); a frame at
72 fps has 13.9 ms, and the eye buffers render at a scale of 1.6. The CPU work is split: the local AI and every
child of the `:ai` process run on cores 0-2 at nice 10, and the frame loop keeps cores 3-5. The local coder and the
voice models take turns ([local-coder.md](local-coder.md)).

Check on the headset with `adb logcat -s VrApi` (FPS, GPU level, GPU %, stale frames) and the session log
(`files/session_log.txt`). Battery Saver (VrApi `LP=1`) must be off for comparable numbers.

## Rendering options

`MetaCodexSetup.ConfigureQuestRendering` (the vendor's OpenXR support feature) and `MetaCodexShell` set these for
Vulkan with multiview:

| Option | What it does | Setting |
|---|---|---|
| Dynamic fixed foveated rendering | The headset raises foveation only while the GPU is busy, up to the set level. | On with FOVEATED (level High as the ceiling), and always while Ambient Focus shows. |
| Symmetric projection | Symmetric eye buffers render more efficiently (5-15 % in GPU-bound scenes, by the vendor's docs). | On. |
| Multiview render regions | Skips the eye-buffer pixels outside the view that symmetric projection adds. | On (final pass). |
| Late latching | Head and hand poses are taken at the end of the render thread: up to one frame less latency. | On. |
| Optimize buffer discards | Memoryless MSAA buffers on the tile GPU. | On. |
| Phase sync | Adaptive frame timing. | Off: it may cause more stale frames when the load changes, as it does here (AI, builds, agents). |
| CPU and GPU levels | The app asks for a sustained high GPU level and does not trade a GPU level for a CPU level (`processorFavor = FavorEqually`). | GPU up to level 2. |

The vendor's OpenXR support feature is on for Android, with foveation and the subsampled layout, so these options
reach the build. Check: VrApi shows GPU level 2; `adb logcat | grep tradeCpuForGpu` shows 0; the OpenXR diagnostic
report at start says "Symmetric Projection: True".

## First start

- **No engine splash.** `ConfigurePlayer` turns the engine's own splash screen off, so the scene starts at once.
- **The shell is built in pieces behind the splash** (`MetaCodexShell.StartStaged`): the splash, the settings, each
  UI font, the theme, the router, the sounds, the vault, the services, the voice, the scripts, the context, every
  window (one piece each), the Whisp, the guide card and the glyphs of the first minutes. Pieces run until 20 ms of
  main-thread time is used in a frame (`StartFrameBudgetMs`), at least one per frame. The splash holds at least 1.4 s
  and until the shell is built (at most 8 s more). A window asked for before its turn runs the rest of the start at
  once. Self-tests and batch runs build everything in one call from the same list (`BuildSteps`), which must name the
  same window builders as `BuildPanels`.
- **The wrist menu is made only in WRIST mode.** Its icon atlas is drawn on a background thread when WRIST is first
  chosen; the DOCK stays until the wrist menu is ready (`wrist menu: built in N frames`).
- **The Linux starts at once** (`MetaCodexApp.Start`). Its unpack and the AI runtime's unpack run on dedicated
  threads at nice 10 (`LowPriority.Run`). The copies out of the APK stay on the engine's web-request thread (0.9 s
  for the models, about 2 s for the Linux archive). The install writes one log line per percent.
- **MQH's `/health`** is asked every 0.5 s while it starts, so its step ends as soon as it answers.
- **Render scale 1.4 while the setup runs**, 1.6 after it: 77 % of the eye-buffer pixels, text still
  supersampled. The scale changes once at the end (each change re-makes the eye textures). Log:
  `display: render scale 1.40 while the one-time setup runs`, then `display: render scale 1.60`.
- **Ambient Focus waits** (`FocusInitMayStart`): the depth and the camera are native starts that cannot be spread
  over frames. On a first start they start 1.5 s after the setup; on later starts as soon as the Linux is ready.
- **Work off the main thread:** the SYSTEM > TOOLS scan parses its output and writes its files on a worker; the
  decision model's cache is read on a worker; the APK's decision vectors seed it.
- **Logs:** a `Debug.Log` line carries no managed stack (`Application.SetStackTraceLogType(LogType.Log, None)`);
  warnings and errors keep theirs.
- **Release APK:** `MCX_RELEASE=1` builds optimised C++ (IL2CPP Release). A development APK keeps the Debug
  configuration, which is quicker to build and slower to run.

**Measured** on the test headset, a clean install of the packed APK, before the staged start, the guide card and the
lower render scale: `[start] shell built in 4823 ms` (one frame of 5,375 ms, then one of 403 ms; the wrist menu took
3,060 ms of it although the main menu was the DOCK). During the setup, with two setup windows, the DOCK and the
Whisp, the app's GPU time was 12-13.7 ms at 640 MHz, GPU 94-97 % busy, 55-65 fps for about 10 s. After it: 10 ms at
492-545 MHz (about 7.7 ms at 640 MHz), GPU 84 %, 72 fps. Stalls: 181 ms right after MQH came up, 69 ms when the setup
window opened, 2 x 56 ms when the depth started, 59 ms when the voice decision model finished. The changes above aim
at these numbers and have no headset run yet. Compare with
`grep -E '\[start\]|\[hitch\]|\[slow|setup:|display: render|ambient focus: depth|wrist menu:' files/session_log.txt`.

## Main thread and garbage collection

- The GC is incremental in every build (`PlayerSettings.gcIncremental`): it works in slices of a few ms across
  frames. The session log says `GC: incremental` at start.
- Userland commands (git checks, `ps`, terminals) start with `vfork()` in `native/pty/mcx_pty.c`: no copy of the
  app's page tables, and the child runs at nice 10.
- A settings change from MQH applies in place; only an accent change rebuilds the windows.
- The chat (`UiChatView`) keeps each block's laid-out lines while its key, text and state stay the same, and a draw
  walks only the lines on screen. A thread's rows are copied only when they change, and the workspace map is made
  only after a change.
- CHANGES builds its rows only when a file, a count or the empty text changed. The side bar of a thread without a
  folder merges CHANGES only when the timeline shows a new file.
- The Whisp's callouts are laid out only while one shows. The words of a thread alert are built only when the state
  changes.
- The scripts' folder walk runs on a pool thread; the main thread loads only what changed. Live texts (GESTURES >
  LIVE, the local coder tile) update 10 or 4 times a second.
- A closed window is an inactive object: no `Update`, no `Tick`.
- The first process sample for the session context runs at 12 s, not in the first frame.

Check: a long coding thread open for 10 minutes; the session log has no `[slow callback]` and no `[slow timeline]`
above 40 ms from the chat, and no hitch with a GC above 100 ms.

**The hitch log.** A frame longer than 25 ms writes `[hitch] t=<s since start> s, frame <ms>, shell <ms>, <step>
<ms>..., GC` into the session log (at most 4 lines a second, 400 a session). The steps are the shell's Update:
callbacks (work queued from other threads), scripts, mqh, voice, context, focus, rebuild, later, sync, layout, orb
(the Whisp).
A long frame with a small "shell" time is the GPU, the render thread or a shader compiled at its first use.

## Depth features

Ambient Focus and "real things in front of windows" share the live depth ([ui.md](ui.md#ambient-focus)).

| Part | How it is kept cheap |
|---|---|
| Sharp depth (`DepthSharp`) | Computed only for a new depth frame, with one reused command buffer: 480 x 480 per eye when the camera guides it, the sensor's 320 x 320 without; 2 x 2 readings, 5 camera taps with one matrix each, a temporal filter with one matrix. |
| The guide camera | 15 fps, for the guide and for the blur. |
| The UI depth test (`McxOcclusion.hlsl`) | Skipped per window when nothing real can be in front of it (`_McxPanelClear`, from the room's 48 x 48 points every 0.3 s). |
| Restarts | Voice never stops the camera (its frames are not used meanwhile). The depth and the camera stay on, idle, for 60 s after the last need: a restart costs about 15 s at 18 fps. |

Measured on the test headset with the earlier, larger versions of these passes (Ambient Focus off): 61-63 fps with
both features on, 65.8 fps with the sharp depth off, 70.6 fps with both off; so the guided sharp depth cost about
0.9 ms a frame and the depth with the UI test about 1.0 ms. The cheaper versions above are estimated to save 1.0-1.3
ms of these 1.9 ms; not measured yet.

What it trades: the sharp depth's edges move in steps of 2/3 of a reading; one bad reading is not voted out (the
temporal filter still averages it); the guide picture is up to 67 ms old; a window marked clear shows a real thing
behind it for up to about 0.4 s, and a thing thinner than about 2 degrees can be missed (hands always show).

## UI shader (`SciFiUI`)

Every pixel of a window runs `SciFiUI`. It keeps its cost down this way:

| How | Saved per pixel |
|---|---|
| The stone armour of a window (rails, corners, crest) only within 3 cm of its frame | about 290 ALU and 20 transcendentals on most body pixels |
| Stone grain from a tiling texture (`_McxStoneNoise`, 512 x 512, 13.2 cm) | 12 `sin` hashes and about 100 ALU become 1 tap |
| The hexagon lattice from a texture (`_McxHexField`) | about 30 ALU become 1 tap |
| An analytic chamfer normal | 4 SDF calls become 1 |
| A halo only outside a shape; nothing outside a button, selection or bar | about 15 ALU, or the whole fill |
| The pointer light only while a hand is near | about 16 ALU |
| The occlusion product per vertex, also in `TextBend` | about 20 ALU per UI and text pixel |
| The body depth of all windows first (queue 2989) | hidden pixels of windows behind fail the depth test |
| Lines and pills keep 0.8 mm for anti-aliasing | fewer margin pixels |

## The Whisp and window transitions

- The Whisp is one draw of a 98-triangle mesh, shaped and moved in the vertex shader, plus its depth pass. Its halo
  square is 1.7 radii, and its fragment work is a few dozen operations.
- A window's cast is a line of light 5 cm wide with two thin rays from the Whisp; there is no filled fan of light.
  A window opens in 0.35 s and closes in 0.22 s; callouts and the Whisp's menu open in 0.22 s and close in 0.12 s.
