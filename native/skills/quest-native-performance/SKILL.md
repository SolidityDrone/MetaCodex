---
name: quest-native-performance
description: Budgets and rules that keep a native Quest 3 / 3S app made in MetaCodex (mcx_xr.h) at its frame rate - frame time at 72, 90 or 120 Hz, draw calls, triangles, texture memory, transparency over passthrough, CPU and GPU levels (cfg.cpuLevel, cfg.gpuLevel) and what passthrough allows, foveation, refresh rate, no per-frame allocations or GL object creation, batching, culling and LOD, the physics budget, loading at start, and the frame-time logger read with mcx-app logs. Use when a native app is slow, hot, jerky or drops frames, when you choose its refresh rate, levels or foveation, or for the performance pass before an app is called done.
---

# Performance of native Quest apps

Each rule is marked **Meta** (Meta's docs), **verified** (the owner measured it on a Quest 3S) or **ours**
(a MetaCodex target; the docs give no number). Sources and doc paths: `references/budgets.md`.

## 1. Frame budget

| Refresh | Budget per frame | Source |
|---|---|---|
| 72 Hz (default) | 13.9 ms | Meta |
| 90 Hz | 11.1 ms | Meta |
| 120 Hz | 8.3 ms | Meta |

- A missed refresh repeats the previous frame: `dt` doubles (27.8 ms at 72 Hz) and the app runs at half rate.
- Pick a rate from `app.refreshRates` (Quest 3S: 72, 80, 90, 120). Never 60 Hz: media apps only (Meta).
- Stay at 72 Hz. Use 90 or 120 Hz only when the heaviest scene keeps its work under 70% of that budget (ours).
  The app must hold the rate it asks for, or it fails store review (Meta).

## 2. Budgets

| Item | Budget | Source |
|---|---|---|
| Draw calls per frame | 300 or fewer | ours; Meta for Quest 3/3S: busy 200-300, medium 400-600, light 700-1000 |
| Triangles per frame | 1.3 M or fewer | ours; Meta for Quest 3/3S: 1.3 M-1.8 M |
| GPU textures | 128 MB or fewer, eye buffers extra | ours (Meta gives no texture figure) |
| Full-screen passes, translucent layers | one full-screen pass per eye; few, small translucent quads | ours; a pass that reads an earlier pass's output costs a tile resolve (Meta); with passthrough every translucent pixel blends over the room |
| Physics | 2 ms CPU per frame, fixed step | ours |

## 3. CPU and GPU levels, and what passthrough allows

`cfg.cpuLevel` and `cfg.gpuLevel` take OpenXR performance levels: 0 PowerSavings, 1 SustainedLow,
2 SustainedHigh, 3 Boost. The default -1 keeps the runtime default, SustainedHigh (Meta).

With passthrough on (`cfg.passthrough = true`), the top sustained levels are **CPU 3 and GPU 2**
(Meta's availability table; verified on a Quest 3S). Without passthrough, CPU 4 and GPU 3-4 are available.

- Passthrough app: `cfg.cpuLevel = 2; cfg.gpuLevel = 2;`. `gpuLevel = 3` gains nothing: the GPU stays at 2.
- Never add `com.oculus.trade_cpu_for_gpu_amount` to a passthrough app. Verified: with passthrough, a CPU-for-GPU
  trade dropped the GPU to level 1 and gave the CPU nothing.
- Boost (3) is for a loading phase: `app.SetPerformance(3, -1)` before it, `app.SetPerformance(2, -1)` after.
  Meta limits Boost to 45 s in a row and 20% of run time. Not measured with passthrough: check it with the logger.
- Set levels at start or at a scene change: each `SetPerformance` call writes a log line and calls the runtime.

## 4. Draws, text, foveation and GL state

- Sort draws by shader, then material, then mesh. Meta's Unity tests: a shader switch costs about 175% more
  draw-call time than none; a repeat of the same object costs about a quarter of a new one.
- `app.text.Draw` and `app.text.Rect` re-upload their vertices on every call. Join lines into one string;
  keep them under 50 calls per frame (ours).
- Clear colour and depth at the start of each eye. Invalidate depth after its last test: call
  `glInvalidateFramebuffer` for `GL_DEPTH_ATTACHMENT` before `app.EndEye` (Meta: depth is commonly not needed after a frame).
- `cfg.foveation` (-1 off, 0 none, 1 low, 2 medium, 3 high) or `app.SetFoveation` at a scene change. Meta: up to
  about 25% faster in pixel-heavy apps; simple shaders can get slower. Keep text near the centre of the view.

## 5. Per-frame rules

- No `new`, `malloc`, `std::vector` growth, `std::string` building or file reads in the frame. Reserve at start.
- No GL object creation in the frame: `glGen*`, shader compiles, `glTexImage2D`, `glBufferData` with a new size.
  Upload at start; `glBufferSubData` for changes.
- No `glFinish`, `glReadPixels` or `glGetError` in the frame: the first two wait for the GPU, and `glGetError` can stall too.
- Share meshes and batch: one draw per mesh and material for many copies (instancing: quest-native-rendering).
  Prefer fewer, fatter triangles and normal maps for detail: thin triangles cost more on tile GPUs (Meta).
- Culling and LOD (ours): skip objects outside the view and far ones; use a cheaper mesh where detail is lost.
- Load at start: models, textures and shaders before the first frame, or in a loading state.

## 6. Measure: the frame logger

Add this block to a file in `src/`. Call `stats.Begin(f, app.refreshRate)` when `app.BeginFrame` returns true,
and `stats.End()` before `app.EndFrame()`. It logs one line every 5 s, after a 3 s warm-up.

```cpp
#include <mcx_xr.h>
#include <chrono>

// Frame times for mcx-app logs. dt = the interval between frames; work = CPU time from BeginFrame to EndFrame.
class FrameStats
{
public:
    // after BeginFrame returned true; hz = app.refreshRate. False for warm-up, the first frame and pauses.
    bool Begin(const mcx::Frame& f, float hz)
    {
        hz_ = hz > 0.f ? hz : 72.f;
        t0_ = std::chrono::steady_clock::now();
        counted_ = f.seconds >= 3.0 && f.dt > 0.f && f.dt <= 0.25f;
        if (!counted_) return false;
        const double ms = f.dt * 1000.0;
        frames_++;
        windowS_ += f.dt;
        sumDt_ += ms;
        if (ms > worstDt_) worstDt_ = ms;
        if (ms > 1.5 * 1000.0 / hz_) missed_++; // a refresh was missed
        return true;
    }
    // before app.EndFrame(); returns this frame's CPU work in ms
    double End()
    {
        const double work = (std::chrono::steady_clock::now() - t0_) / std::chrono::duration<double, std::milli>(1.0);
        if (!counted_) return work;
        sumWork_ += work;
        if (work > worstWork_) worstWork_ = work;
        if (work > 1000.0 / hz_) overWork_++;
        if (windowS_ >= 5.0) Report();
        return work;
    }

private:
    void Report()
    {
        mcx::Log("frames %.0f s: %.1f fps; frame avg %.1f ms, worst %.1f ms, missed %d of %d; "
                 "work avg %.1f ms, worst %.1f ms, over budget %d (budget %.1f ms)",
                 windowS_, frames_ / windowS_, sumDt_ / frames_, worstDt_, missed_, frames_,
                 sumWork_ / frames_, worstWork_, overWork_, 1000.0 / hz_);
        frames_ = missed_ = overWork_ = 0;
        windowS_ = sumDt_ = sumWork_ = worstDt_ = worstWork_ = 0.0;
    }
    std::chrono::steady_clock::time_point t0_{};
    double hz_ = 72.0, windowS_ = 0.0, sumDt_ = 0.0, worstDt_ = 0.0, sumWork_ = 0.0, worstWork_ = 0.0;
    int frames_ = 0, missed_ = 0, overWork_ = 0;
    bool counted_ = false;
};

void Loop(mcx::App& app)
{
    FrameStats stats;
    while (app.Pump()) {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        stats.Begin(f, app.refreshRate);
        /* draw both eyes here: BeginEye, EndEye (quest-native-xr) */
        stats.End();
        app.EndFrame();
    }
}
```

Read it with `mcx-app logs <dir>` (the last 200 lines; the newest window starts with `frames`). Do not add `-f`:
it never ends in a shell.

- `missed` above 0 and `work` under 70% of the budget: the GPU is late. Cut fill: foveation, full-screen passes, shader cost.
- `over budget` above 0, or `work` near the budget: the CPU is late. Cut draw calls, per-frame work and allocations.
- `work` includes the wait for a free swapchain image, and the first window includes shader compiles: judge from the second window on.
- No logcat, VrApi stats or GPU timer here. If `mcx-app logs` is empty, ask for a Report: it sends the last 60 lines with the picture.

## 7. Over budget: change the biggest cost first

1. Find the side, CPU or GPU, with section 6.
2. GPU: foveation 2 when the text is central; fewer full-screen passes; cheaper fragment work; mipmapped textures.
3. GPU: fewer draw calls and triangles (merge, instance, cull).
4. CPU: remove per-frame allocations and GL creation; format text every 5 s, not every frame.
5. Levels: 2 and 2 for passthrough (section 3).
6. Last: 72 Hz. Never lower the quality without telling the user what was given up.
