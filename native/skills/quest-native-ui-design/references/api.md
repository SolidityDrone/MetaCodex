# mcx_ui.h API, name by name

All names are in namespace `mcx`. Header `mcx_ui.h`: header-only, includes `mcx_xr.h`, needs nothing else. Units:
metres, Y up, right-handed. The theme takes sRGB hex; the kit converts it to linear once per frame.

## Types

- `UiStyle`: enum class. `Secondary` (default: dark glass, frame edge), `Primary` (accent fill, dark text: the one action of a panel), `Danger` (red fill, dark text: destructive actions only).
- `UiSize`: enum class. `Title` (34 mm at 1 m), `Body` (24 mm), `Small` (18 mm).
- `UiTone`: enum class. Label colours: `Text` (main), `Dim`, `Accent`, `Ok`, `Warn`, `Danger`.
- `UiTheme`: palette as sRGB hex (`accent`, `accentSoft`, `frame`, `warn`, `danger`, `ok`, `text`, `textDim`, `textAccent`, `glass`, `onAccent`); `glassAlpha` (0.86), `glowAlpha` (0.45); sizes in metres at 1 m: `titleText`, `bodyText`, `smallText`, `padding`, `gap`, `radius`, `border`, `buttonHeight`, `headerHeight`, `glow`, `cursor` (radians); interaction: `reach` (0.10 m: a fingertip this close in front hovers), `rayRange` (4 m), `touchGap` (0.012 m); motion: `appearTime` (0.18 s), `flashTime` (0.25 s).
- `UiPanel`: the box in world metres: `width`, `height`; `pose` (the centre; local +Z faces the user); `scale` (content scale, set by `PlaceInFront`); `title` (header text, nullptr for none); `glow` (accent glow outside the edge); `input` (false: a HUD, never a pointer target). Kit state, do not set: `appear`, `lastFrame`.
- `Ui`: the kit object. Public members: `theme` (a `UiTheme`) and `controllers` (false by default: hands only; true lets a controller's aim ray and trigger point too, when that hand has no aim ray).

## Functions

- `UiSrgb(uint32_t rgb, float alpha = 1)`: sRGB hex to a linear `XrColor4f`. The kit uses it for its theme.
- `UiPanel::PlaceInFront(const Frame& f, float distance = 1, float drop = 0.1)`: puts the centre `distance` ahead of the eyes and `drop` below them, turned to the user (yaw only); sets `scale = distance`.
- `Ui::Init(App& app)`: compiles the shader and makes the quad. Call once after `app.Init`. Returns false on a link error.
- `Ui::Begin(App& app, const Frame& f)`: once per frame, after `app.BeginFrame`. Reads the hands and the focus; works out which panel each hand points at.
- `Ui::Open(UiPanel& p)`: starts a panel. The widgets that follow go into it. One panel at a time: a second Open before Close is ignored.
- `Ui::Close()`: ends the open panel and draws its pointer dot.
- `Ui::Row(int count)`: the next `count` widgets share one line, in equal slots with `gap` between them.
- `Ui::Space(float metres)`: a gap below the last widget, scaled by the panel. Ignored inside a row.
- `Ui::Label(const char* text, UiSize size = UiSize::Body, UiTone tone = UiTone::Text)`: text at the next slot, top left. No wrap and no clip: break lines with `\n`.
- `Ui::Button(const char* label, UiStyle style = UiStyle::Secondary, bool enabled = true)`: true on the frame of a click. The label shrinks to fit the width.
- `Ui::Toggle(const char* label, bool& on)`: a switch on the right; a click flips `on`; true when it changed.
- `Ui::Slider(const char* label, float& value, float min, float max)`: a track and a knob; slide it with a poke or a pinch; shows the value; true when it changed.
- `Ui::Draw(const Eye& eye)`: draws every panel of this frame into one eye. After the scene, before `app.EndEye`.
- `Ui::End()`: ends the frame. Its panels are the pointer targets of the next frame.
- `Ui::Destroy()`: deletes the GL program, VAO and VBO.

Not API: everything in `mcx::detail` (helpers and the two shaders) and the private members of `Ui`.

## Behaviour the code guarantees

- Poke: the index fingertip within `reach` in front of a panel hovers the widget under it. A press is the tip crossing the panel plane inside the widget. A fingertip in reach beats the aim ray.
- Ray: the aim ray must hit the panel from the front, within `rayRange`. A click is a pinch that starts on the widget.
- Among panels, the nearest one wins. A panel takes a pointer only once it has nearly finished appearing (appear at least 0.9).
- No pointer while the app menu is open or the app has no focus (`Begin` reads `f.focused`).
- Pointer targets come from last frame's panels (one frame of lag).
- Widgets keep hover and press state by call order. Keep the order fixed on each screen.
- `Draw` restores the program, the VAO, the blend, depth-test and cull enables, and the depth mask. It does NOT restore `glBlendFunc`, the array-buffer binding or the 2D texture binding (`Text::Draw` changes them). Set your own GL state after `ui.Draw`.
- Panels draw with no depth test, over everything drawn before them in the same eye.

## Menu items and the frame loop

The flag a menu item sets must outlive the menu: it belongs to the app, not to a short function. Items go before Quit.

```cpp
#include <mcx_ui.h>

// Your items come before Quit. Back and Quit stay built in: never add an exit.
void AddMenuItems(mcx::App& app, bool& startOver)
{
    app.menuItems.push_back({"Start over", [&startOver] { startOver = true; }});
}
```

The loop with the UI: Init once after `app.Init`; then Begin, the screens, Draw inside each eye, End.

```cpp
// The frame with the UI, in the order every native app uses.
void RunFrames(mcx::App& app)
{
    mcx::Ui ui;
    if (!ui.Init(app)) return;
    mcx::UiPanel menu;
    menu.title = "Lantern";
    bool placed = false, started = false, startOver = false;
    AddMenuItems(app, startOver);
    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;                // false: nothing to draw this frame (still Pump)
        ui.Begin(app, f);
        if (startOver) { started = false; startOver = false; }   // the menu asked for the start screen
        if (!placed) { menu.PlaceInFront(f, 1.0f); placed = true; }
        if (!started)
        {
            ui.Open(menu);
            ui.Label("Pinch to start");
            started = ui.Button("Start", mcx::UiStyle::Primary);
            ui.Close();
        }
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            glClearColor(0.f, 0.f, 0.f, app.clearAlpha());   // alpha 0 where the room should show
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            // the scene: draw with eye.viewProj
            ui.Draw(eye);                                    // the UI last, over the scene
            app.EndEye(e);
        }
        ui.End();
        app.EndFrame();
    }
    ui.Destroy();
}
```

## Sources

Meta docs: run `metavr docs fetch "<path>"` (the path is in quotes).

- `design/hands-ui-best-practices.md`: touch at 42 to 46 cm; ray comfortable from 0.8 to 3 m; avoid 0.5 to 0.8 m (the handoff); 48 dp hit target (about 22 mm), 2.5 to 3 deg for touch and ray; 12 mm minimum spacing; index finger only for touch; frequent actions in the lower half; no menus anchored to a wrist; hover and press states; audio and visual feedback because hands have no haptics; panels oriented to the user, roll constrained; no hover-driven layout changes.
- `design/mr-design-guideline.md`: about 1 m, slightly below the line of sight; fade-in for large objects; angular scaling for readability, with an animation when the distance changes; minimise head-locked content (anchor to space, or follow with smoothing); 45 cm for hand-touch windows, about 1 m for ray windows; no objects behind physical walls.
- `design/styles_inputs_hit_targets.md`: minimum 48 dp; 60 dp for primary controls with hand tracking.
- `design/accessibility.md`: hit target 22 mm, 48 dp, 3 deg at 0.42 m; visual targets at least 32 dp; WCAG 2.1 contrast (4.5:1 normal text, 3:1 large text and UI parts); do not rely on colour alone.
- `design/styles_typography.md`: test on the headset; light walls wash out light text; no italics; regular weight for body copy.
- `design/comfort.md`: static content in front of the eyes hides passthrough; avoid depth on text.
- `design/head.md`: interactables at a comfortable height, in the natural line of sight.
- `design/raycasting_specs.md`: hit-test ray 10 m (the kit uses 4 m); the hand beam fades over the last 5 cm.

Local: `native/xr/mcx_ui.h` (theme 65-84, panel 86-112, class Ui 214-279, Begin 402, Open 448, Button 624, Toggle 667,
Slider 701, Draw 783). `native/xr/mcx_font.h`: 16 x 32 cells, so a character is half as wide as it is tall.
`native/xr/mcx_xr.cpp`: `Text::Draw` and `DrawVerts` (blend and depth-mask changes), `DrawMenu` (drawn after the UI in `EndEye`),
menu items (`menuItems` after Back, Report and before Quit).

Contrast (WCAG 2.1 relative luminance on linear colour, computed with `python3 -I`): the glass (0x08121B, alpha 0.86)
over a white wall (linear 1.0) gives main text 4.78:1, dim 2.73:1, title text 3.98:1, ok 3.68:1, warn 3.07:1,
danger 1.66:1, and the secondary edge (0x5F7C9C at 0.8) 1.20:1. Over a black wall main text is about 17:1.
The primary fill (accent at 0.92 over white) with the dark text gives 11.3:1. These are estimates: judge the real
room on the headset.
