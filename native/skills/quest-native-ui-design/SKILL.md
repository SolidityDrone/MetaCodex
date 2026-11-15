---
name: quest-native-ui-design
description: Use for every menu, panel, button, slider, toggle, HUD, settings screen, start screen, pause screen, result screen, first-use hint, 3D label or other 2D interface in a native Quest app made in MetaCodex, and whenever the user asks for the look of a native app (curated, clean, legible, designed, not a debug overlay). Gives the visual language and the themed UI kit mcx_ui.h (hierarchy, sizes from the angle and the distance, spacing and panel height, the family look with the accent of each app look, states and feedback, motion and screen changes, placement) and ready layouts (start menu, settings, HUD, hint, pause, result, 3D label, app menu item). Read it before writing any UI code.
---

# Native UI design: curated menus and HUDs

Read **quest-native-hands-first** (hands only, mixed reality) and **quest-native-quality** (the passes and the
self-review) first. Take the look of the app from **quest-native-art-direction**. This skill sets the look of the UI.
The kit is `mcx_ui.h`: `#include <mcx_ui.h>`. Every name, the menu items and the full frame loop are in
`references/api.md`. Scene lights and materials: **quest-native-rendering**.

The quick path to a menu that looks designed:

1. Take the three UI colours from the look of the app (section 4). Keep the dark glass.
2. Pick the distance: 1.0 to 1.5 m for a pointed panel, 0.45 m for a poked one. Sizes follow it (section 2).
3. Write one function per screen (section 8). Set each panel height with the formula (section 3).
4. Place each panel once, beside the hero, with its centre below the eyes (section 7).
5. Let each screen grow in. Never fake an exit (section 6). Answer the critic check (section 9).

## 1. Hierarchy

- One title per panel (`panel.title`, in the header bar). One primary action (`mcx::UiStyle::Primary`) at the
  bottom: for touch, Meta puts frequent actions in the lower half. Other buttons are `Secondary`.
- `Danger` only for a destructive action, on its own confirm panel. About five widgets per panel at most.
  The kit has no scroll view: split a long list into panels.
- Button labels: one to three words ("Start", "Done"). Subtitles and hints: one short line. Say "pinch", "touch" or
  "point", never "button" or "controller".
- `glow = true` puts a soft accent edge round a panel. Use it on the one panel that waits for the user (start,
  result). Never on a HUD. Never on two panels at once.

## 2. Sizes from the angle

The theme is sized for 1 m. `PlaceInFront(f, d)` sets the scale to `d`, so text keeps its angle. If you set the pose
by hand, set `scale` yourself: it is the distance from the eyes to the panel (`PlaceBehind` in section 7 does it).
Width and height are metres at the placement distance: a 0.40 m panel at 1 m is `0.40f * d` at distance `d`.

| At 1 m | Size | Angle | Rule |
|---|---|---|---|
| Title / Body / Small | 36 / 26 / 20 mm | 2.1 / 1.5 / 1.1 deg | Body is the smallest text the user must read (quest-native-quality: at least 25 mm at 1 m); Small is for captions |
| Button | 66 mm tall (the default) | 3.8 deg | Meta: primary controls 60 dp; never below 3 deg (48 dp, 22 mm at 0.42 m) |

The sizes at each distance are the theme sizes times `scale`. The angle is the same in every row.

| Distance from the eyes | Use | Title | Body | Small | Button |
|---|---|---|---|---|---|
| 0.45 m | a poke-only panel with two or three buttons (Meta: touch distance 0.42 to 0.46 m) | 16 mm | 12 mm | 9 mm | 30 mm |
| 1.0 m (0.8 to 3 m) | menus and settings, aimed at with the pinch ray (default) | 36 mm | 26 mm | 20 mm | 66 mm |
| 1.5 m | a HUD, to the side; a panel behind a table-top game | 54 mm | 39 mm | 30 mm | 99 mm |
| 2.0 m | a large panel, far in the room, aimed at only | 72 mm | 52 mm | 40 mm | 132 mm |
| 0.5 to 0.8 m | never: the hand-to-ray handoff zone (Meta) | | | | |

- Scale the box with the distance too: the 0.40 m start panel is 0.18 m wide at 0.45 m and 0.60 m wide at 1.5 m.
- A character is half as wide as the text is high. A 0.40 m panel at 1 m holds about 27 Body characters or 19 Title
  characters in one line. A `Label` does not wrap and does not shrink: a long one runs out of the panel. Break a
  line with `\n`. The label of a `Button`, `Toggle` or `Slider` shrinks to fit: keep it short.
- At 0.45 m the gap between widgets stays 12 mm, not 7 mm: the kit holds the Meta minimum (section 3).

## 3. Spacing grid and panel height

Spaces are multiples of 4 mm at 1 m: padding 24 mm, gap 16 mm, header bar 60 mm, least gap 12 mm (Meta minimum;
the kit keeps it in world metres). The corner radius is 22 mm. Use `ui.Space(metres)` in those steps. Two or three
buttons on one line: `ui.Row(n)` before them.

The kit does not size a panel: you set `height`. At 1 m, the height is 0.048 (padding above and below) plus 0.060
(the header bar, when there is a title) plus the widgets, plus 0.016 between two widgets, plus each `ui.Space`.
Multiply by the distance. At 0.45 m a gap is 12 mm, not 7 mm: add 5 mm for each gap.

| Widget | Height at 1 m |
|---|---|
| Label, one line: Title / Body / Small (each extra line adds 1.1 times the text size) | 0.036 / 0.026 / 0.020 |
| Button, Toggle | 0.066 |
| Slider | 0.069 |

The start menu in section 8: 0.060 + 0.048 + 0.026 + 0.016 + 0.012 + 0.066 + 0.016 + 0.066 = 0.31 m. A panel that is
too short lets its last widget touch the edge. A panel that is too tall puts more glass over the room.

## 4. Colour, contrast and the look of the app

| Role | sRGB | Use |
|---|---|---|
| Text | 0xE2F4FF | every instruction and label |
| Text dim | 0x9DBDD2 | secondary facts, in a dark room only |
| Accent | 0x35D0FF | one filled element per panel (the Primary button), a toggle on, a slider fill, hover and glow, the pointer dot |
| Accent soft | 0x2A9FD6 | the tint of the header bar (22 percent alpha), only on a panel with a title |
| Title text | 0x8FEBFF | the title, the slider value, a label with `UiTone::Accent` |
| Frame | 0x5F7C9C | the edge of a secondary button |
| Glass | 0x08121B, alpha 0.86 | every panel: dark, translucent |
| Ok, warn, danger | 0x46F28C, 0xFFB547, 0xFF4D5E | a shape or a word with the colour; text only in a dark room |

- Accent sparingly. Never accent body text. No pure white, no pure black: take colours from `ui.theme`.
- WCAG 2.1 (Meta): 4.5:1 for text, 3:1 for parts. Over a white wall, the worst passthrough, main text is 4.8:1;
  dim 2.7:1, ok 3.7:1, warn 3.1:1, danger 1.7:1 and the secondary edge 1.2:1. Only main text carries an instruction.
  Check each panel on the headset, against a white wall and in a dark room.

### Keep the family look: change three colours

Every native app keeps the dark glass, the shapes, the sizes and the spacing of this kit: the user knows them. Change
only three colours, from the look of the app (**quest-native-art-direction**): `ui.theme.accent`,
`ui.theme.accentSoft` and `ui.theme.textAccent`. Set them once, after `ui.Init`. Do not touch `text`, `textDim`,
`glass` and `glassAlpha`: they are contrast-tested over a white wall. `accentSoft` tints the header bar.
`textAccent` colours the panel title, the slider value and the `UiTone::Accent` labels.

| Look | accent | accentSoft | textAccent |
|---|---|---|---|
| Night glass (the theme default) | 0x35D0FF | 0x2A9FD6 | 0x8FEBFF |
| Cosmic | 0xFFC46B | 0xC9923F | 0xFFE2B0 |
| Neon arcade | 0xFF4FD8 | 0xB8379C | 0xFFB3F0 |
| Soft daylight | 0xFF8A5B | 0xC9643D | 0xFFC9B0 |
| Gallery | 0xE0B25A | 0xA88340 | 0xF2DDB0 |
| Toy clay | 0x4CC9F0 | 0x2E94B8 | 0xB5ECFF |

For example, Cosmic: `ui.theme.accent = 0xFFC46B; ui.theme.accentSoft = 0xC9923F; ui.theme.textAccent = 0xFFE2B0;`

The dark text on a Primary button (`onAccent`, 0x061018) needs a bright accent: all six keep 6:1 or more with it. By
calculation, over a white wall the title keeps 3.3 to 4.3:1 (a title is large text: 3:1 is enough), and the accent
as a shape keeps 1.9 to 3.4:1 against the glass (6 or more in a dark room). So colour never carries a meaning alone:
the label says it too.

## 5. States and feedback

Every widget reacts in the frame its pinch or poke starts. The kit draws these states:

| State | What the user sees |
|---|---|
| Hover (ray, or the fingertip near) | fill lifts, accent edge and glow; the layout never changes |
| Pressed, clicked | the face sinks 12 percent; a flash for 0.25 s |
| Disabled (the third argument of `ui.Button` is false) | faded: add a Label that says why and what fixes it |

Toggle and slider move their knob; the slider shows its value. Say the fix in words for empty and error states
("Run Space Setup").

Feedback is visual: the kit's states plus a reaction on the 3D object. The native layer has no audio, and hands have
no haptics. In the frame of the action, the object it touches answers: a glow pulse, or a small scale pop with
`mcx::EaseOutBack` from `mcx_gfx.h`. Use `mcx::EaseOutBack` on 3D objects only, never on a panel. With
`t = mcx::Progress(f.seconds, pressedAt, 0.3f)`, the pop is `1.f + 0.15f * (1.f - mcx::EaseOutBack(t))`. The pulse
adds `1.f - mcx::EaseOut(t)` times a strength to the glow.

## 6. Motion and transitions

- A panel appears in 0.18 s (`ui.theme.appearTime`): it grows from 92 percent and fades in, ease-out. No bounce,
  slide, spin or motion that follows the head.
- Move a panel only when the user walks away, and glide it (the lazy follow in quest-native-recipes).

The kit checks at each `ui.Open(panel)` whether the panel was open in the frame before. `appear` and `lastFrame` are
kit state: do not set them.

| What the app did in the frame before | The panel |
|---|---|
| It opened this panel (every frame) | stays, no animation |
| It did not open this panel (hidden, another screen, never shown) | grows in |
| It did not run `ui.Begin` (`app.BeginFrame` returned false) | no change: that frame does not count |

A panel takes no pointer until it is 90 percent in (about 0.16 s): the user cannot press a new screen by mistake.

Switch screens like this:

- Keep one `UiPanel` for each screen (a `UiScreen`, section 8). One panel for two screens does not grow in at the
  switch: its content only jumps.
- In the frame of the click, only set the next screen. In the next frame, open the new panel and not the old one:
  the new one grows in, the old one is gone. Never open both in one frame: they overlap.
- Open the new panel at the same spot (`KeepSpot` in section 8).
- Never open a panel every other frame: it restarts each time and never finishes.

The kit has no exit animation: a panel is gone in the first frame that the app does not open it. The rule: close fast,
open soft. The 3D scene answers in the same frame (section 5), and the next screen grows in. Do not keep a panel open
to fake an exit. A button that closes its own panel shows its press for one frame only: to show it, keep opening the
old panel for about 0.1 s after the click, then switch.

## 7. Placement and rules

- Place once: `if (!placed) { panel.PlaceInFront(f, d); placed = true; }`. It puts the centre 0.1 m below the eyes
  (the third argument, `drop`), turned to the user, yaw only: about 6 deg below eye level at 1 m.
- Read the head from `f.views` (the midpoint of the two eye positions, as `PlaceInFront` does). Never assume a
  height: the eyes are about 1.6 m above the floor when the user stands and about 1.2 m when the user sits.
- The centre of a menu goes 10 to 15 cm below the eyes (`drop` 0.10 to 0.15). Never put the centre above eye level.
- Beside the hero, never over it. Panels draw over the scene with no depth test, so a panel hides all that is behind
  it. Put the UI to the side of the hero at the same distance (`ShiftRight`), or behind it for a table-top game.
- A panel behind a table-top game stands behind the board, a little above it, tilted back 15 to 20 degrees
  (`PlaceBehind`). A tilted panel works. The kit finds the pointer on the plane of the panel pose: the normal is
  local +Z turned by `pose.orientation`. It does this for the fingertip and for the ray. `PlaceInFront` only makes
  yaw-only poses, so the tilt is yours to set.
- A poke panel (0.45 m): keep the space between the user and the panel free. The fingertip hovers a widget from 0.10 m
  in front of the panel (`reach`) and presses when it crosses the panel. The nearest panel wins: no other panel and no
  3D object in front of it.
- A panel takes input only from its front: the ray and the fingertip must come from the +Z side.
- Head-locked UI is a bug. A HUD is placed once, or follows with a lag. Never anchor a menu to a wrist or a hand.
- Poke uses the index fingertip only. Keep 3D objects out of the view of a panel.
- No debug text on screen (no FPS, state names, counters or depth). Use `mcx::Log`.
- The app menu stays: Back to MetaCodex and Quit are built in. Add items with `app.menuItems` (section 8); never
  draw your own Quit or Exit.
- While `f.focused` is false (a system menu or the app menu is open) the kit shows no pointer. The game waits
  (section 8, Pause).

```cpp
#include <mcx_ui.h>
#include <mcx_gfx.h>

// The head: the midpoint of the two eye positions, as PlaceInFront reads it. Never assume a height.
XrVector3f HeadOf(const mcx::Frame& f)
{
    const XrVector3f a = f.views[0].pose.position, b = f.views[1].pose.position;
    return {(a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, (a.z + b.z) * 0.5f};
}

// Moves a placed panel along its own right (negative: left), in metres. Use it to stand a panel beside the hero.
void ShiftRight(mcx::UiPanel& p, float metres)
{
    const XrVector3f r = mcx::Rotate(p.pose.orientation, {1.f, 0.f, 0.f});
    p.pose.position.x += r.x * metres;
    p.pose.position.y += r.y * metres;
    p.pose.position.z += r.z * metres;
}

// Stands a panel behind the hero, as the user sees it: `behind` metres further away, `lift` above the hero, tilted
// back by `tilt` degrees (15 to 20). It sets `scale` to the distance from the eyes: set width and height after it,
// for example board.width = 0.40f * board.scale.
void PlaceBehind(mcx::UiPanel& p, const mcx::Frame& f, XrVector3f hero, float behind, float lift, float tilt)
{
    const XrVector3f head = HeadOf(f);
    float dx = hero.x - head.x, dz = hero.z - head.z;
    const float len = std::sqrt(dx * dx + dz * dz);
    if (len < 1e-3f) return;                           // the hero is straight above or below the eyes
    dx /= len;
    dz /= len;
    p.pose.position = {hero.x + dx * behind, hero.y + lift, hero.z + dz * behind};
    const float yaw = std::atan2(-dx, -dz);            // local +Z faces the eyes: yaw only, as PlaceInFront
    const XrQuaternionf turn{0.f, std::sin(0.5f * yaw), 0.f, std::cos(0.5f * yaw)};
    const float rad = tilt * 3.14159265f / 180.f;      // the top leans away: a turn about the panel's own X axis
    p.pose.orientation = mcx::QuatMul(turn, mcx::QuatAxisAngle({1.f, 0.f, 0.f}, -rad));
    const float ex = p.pose.position.x - head.x, ey = p.pose.position.y - head.y, ez = p.pose.position.z - head.z;
    p.scale = std::sqrt(ex * ex + ey * ey + ez * ez);  // the text keeps its angle
}
```

## 8. Layouts

Each screen is a function, called between `ui.Begin` and `ui.End`. Keep the calls in one fixed order per screen:
widget state is kept by call order. Each screen has its own `UiScreen` (a panel and a `placed` flag). Heights come from
the formula in section 3. Start, settings and result stand beside the hero: remove the `ShiftRight` call when no hero
stands in front of the user.

### Start menu: title, subtitle, a secondary and a primary button

```cpp
#include <mcx_ui.h>

struct UiScreen                                        // one screen: its panel, placed once
{
    mcx::UiPanel panel;
    bool placed = false;
};

enum class StartChoice { None, Start, Settings };

// Title, subtitle, Settings, then the primary Start at the bottom.
StartChoice ShowStart(mcx::Ui& ui, UiScreen& s, const mcx::Frame& f)
{
    mcx::UiPanel& p = s.panel;
    if (!s.placed) { p.PlaceInFront(f, 1.0f); ShiftRight(p, 0.45f); s.placed = true; }   // 1 m ahead, beside the hero
    p.title = "Lantern";
    p.width = 0.40f;                                   // 0.40 m at 1 m
    p.height = 0.31f;                                  // 0.060 + 0.048 + 0.026 + 0.016 + 0.012 + 0.066 + 0.016 + 0.066
    ui.Open(p);
    ui.Label("Paint light into your room");
    ui.Space(0.012f);
    StartChoice c = StartChoice::None;
    if (ui.Button("Settings")) c = StartChoice::Settings;
    if (ui.Button("Start", mcx::UiStyle::Primary)) c = StartChoice::Start;
    ui.Close();
    return c;
}
```

### Settings: a toggle and a slider

```cpp
// Toggle and slider, then Done (primary, at the bottom). True when Done is pressed.
bool ShowSettings(mcx::Ui& ui, UiScreen& s, bool& hints, float& speed, const mcx::Frame& f)
{
    mcx::UiPanel& p = s.panel;
    if (!s.placed) { p.PlaceInFront(f, 1.0f); ShiftRight(p, 0.45f); s.placed = true; }
    p.title = "Settings";
    p.width = 0.40f;
    p.height = 0.353f;                                 // 0.060 + 0.048 + 0.066 + 0.016 + 0.069 + 0.016 + 0.012 + 0.066
    ui.Open(p);
    ui.Toggle("Hints", hints);
    ui.Slider("Speed", speed, 0.5f, 2.f);              // true when changed
    ui.Space(0.012f);
    const bool done = ui.Button("Done", mcx::UiStyle::Primary);
    ui.Close();
    return done;
}
```

### HUD: small, to the side, not head-locked

```cpp
// A HUD: small, to the side of the view, placed once, never a pointer target.
void ShowHud(mcx::Ui& ui, UiScreen& h, const mcx::Frame& f, int found, int total)
{
    mcx::UiPanel& p = h.panel;
    if (!h.placed)
    {
        p.PlaceInFront(f, 1.5f, 0.2f);                 // 1.5 m ahead, 20 cm below the eyes
        ShiftRight(p, 0.5f);                           // 50 cm to the right, in the panel's own plane
        h.placed = true;
    }
    p.input = false;
    p.width = 0.22f * 1.5f;                            // 0.22 m at 1 m, times the distance
    p.height = 0.075f * 1.5f;                          // 0.048 + 0.026, times the distance
    ui.Open(p);
    char text[32];
    std::snprintf(text, sizeof text, "Found %d of %d", found, total);
    ui.Label(text);
    ui.Close();
}
```

### First-use hint: "Pinch to start"

```cpp
// A hint under the start screen, shown until the first Start press. Never a pointer target.
void ShowHint(mcx::Ui& ui, UiScreen& hint, const UiScreen& start, bool show)
{
    if (!show) return;
    mcx::UiPanel& p = hint.panel;
    p.pose = start.panel.pose;
    p.pose.position.y -= 0.23f * start.panel.scale;    // 23 cm lower
    p.scale = start.panel.scale;
    p.width = 0.30f * p.scale;
    p.height = 0.072f * p.scale;
    p.input = false;
    ui.Open(p);
    ui.Label("Pinch to start");
    ui.Close();
}
```

### Pause: Resume, never Quit

Show it while the user has paused, and while `f.focused` is false (a system menu has the focus). The game waits then.
Set `paused = true` when the user asks for a pause. Set it also when `f.focused` is false and `app.MenuOpen()` is false.
Show the panel with `show = paused && !app.MenuOpen()`: the app menu draws over your panels, so it needs no Pause
panel. Resume is the Primary button. Never add a Quit button: the app menu has Quit. When Resume returns true, set
`paused = false`. Stop the game clock while `paused` or while `f.focused` is false: `f.dt` keeps running.

```cpp
// Pause: shown while `show` is true. True when Resume is pressed.
bool ShowPause(mcx::Ui& ui, UiScreen& s, const mcx::Frame& f, bool show)
{
    if (!show) { s.placed = false; return false; }     // closed: the next pause opens in front of the user
    mcx::UiPanel& p = s.panel;
    if (!s.placed) { p.PlaceInFront(f, 1.0f); s.placed = true; }
    p.title = "Paused";
    p.width = 0.40f;
    p.height = 0.23f;                                  // 0.060 + 0.048 + 0.026 + 0.016 + 0.012 + 0.066
    ui.Open(p);
    ui.Label("The game waits for you");
    ui.Space(0.012f);
    const bool resume = ui.Button("Resume", mcx::UiStyle::Primary);
    ui.Close();
    return resume;
}
```

### Result: the score is the title

The score is the title. One line goes under it. "Play again" is the Primary button. Set `placed` to false when the
game starts again: the next result then opens in front of the user.

```cpp
// Result: the score as the title, the best score in one line, "Play again" as Primary. True when it is pressed.
bool ShowResult(mcx::Ui& ui, UiScreen& s, const mcx::Frame& f, int score, int best)
{
    mcx::UiPanel& p = s.panel;
    if (!s.placed) { p.PlaceInFront(f, 1.0f); ShiftRight(p, 0.45f); s.placed = true; }   // beside the hero
    static char title[32];                             // static: the panel keeps this pointer
    std::snprintf(title, sizeof title, "Score %d", score);
    p.title = title;
    p.width = 0.40f;
    p.height = 0.23f;                                  // 0.060 + 0.048 + 0.026 + 0.016 + 0.012 + 0.066
    ui.Open(p);
    char line[32];
    std::snprintf(line, sizeof line, "Best %d", best);
    ui.Label(line);
    ui.Space(0.012f);
    const bool again = ui.Button("Play again", mcx::UiStyle::Primary);
    ui.Close();
    return again;
}
```

### App menu item: a rare action, added once at start

The app menu lists Back to MetaCodex and Report to MetaCodex (when MetaCodex started the app), your items, then Quit and
Close menu. A `MenuItem` is a label and an action: `app.menuItems.push_back({"Restart level", [&] { ... }});`. Rules:

- Add items once, after `app.Init`, before the frame loop. Use them for rare actions (Restart level, Show the tour).
  Frequent actions belong in the panels. Never add Quit, Exit or Back: they are built in.
- The label is two or three words, 24 characters at most.
- The action runs in the frame loop, inside `app.BeginFrame`, after the menu has closed. Set a flag and do the work
  in the frame. Capture only variables that live as long as the loop: a reference to a local of a short function
  hangs after it returns.

```cpp
// Once at start. `restartLevel` lives in the function that runs the frame loop.
void AddRestartItem(mcx::App& app, bool& restartLevel)
{
    app.menuItems.push_back({"Restart level", [&restartLevel] { restartLevel = true; }});
}
```

### 3D label: a name above an object

A small panel with `input = false`, above the object, turned to the user (yaw only), Small text (Body for a name that
matters). Show at most about 8 labels: each costs draw calls (a box and a text). Panels draw with no depth test, so
show a label only for an object the user can see. Keep it to a name, not a sentence.

```cpp
// A label for one object. `above` is the centre of the label. Call it between ui.Begin and ui.End, with no panel open.
void ShowLabel(mcx::Ui& ui, mcx::UiPanel& p, const mcx::Frame& f, XrVector3f above, const char* name)
{
    const XrVector3f head = HeadOf(f);
    const float dx = above.x - head.x, dy = above.y - head.y, dz = above.z - head.z;
    float k = std::sqrt(dx * dx + dy * dy + dz * dz);  // the distance from the eyes
    k = k < 0.6f ? 0.6f : (k > 2.5f ? 2.5f : k);       // a far label does not grow past its object
    p.pose.position = above;
    p.pose.orientation = mcx::QuatLookAt(head, {above.x, head.y, above.z});   // faces the head, yaw only
    p.scale = k;
    p.input = false;
    p.width = (static_cast<float>(std::strlen(name)) * 0.010f + 0.048f) * k;  // Small text: 10 mm a character at 1 m
    p.height = 0.068f * k;                             // 0.048 + 0.020
    ui.Open(p);
    ui.Label(name, mcx::UiSize::Small);
    ui.Close();
}
```

### Join the screens: one state, one panel for each

```cpp
enum class Screen { Start, Settings, Play };

struct Prefs
{
    bool hints = true;
    float speed = 1.f;
};

// The next screen opens where this one was: the same pose and scale, no new PlaceInFront.
void KeepSpot(const UiScreen& from, UiScreen& to)
{
    to.panel.pose = from.panel.pose;
    to.panel.scale = from.panel.scale;
    to.placed = true;
}

// Start and Settings. A click only sets `screen`. The next frame opens the new panel (it grows in) and does not open
// the old one (it is gone). Never both in one frame.
void ShowMenus(mcx::Ui& ui, const mcx::Frame& f, Screen& screen, UiScreen& start, UiScreen& settings, Prefs& prefs)
{
    if (screen == Screen::Start)
    {
        const StartChoice c = ShowStart(ui, start, f);
        if (c == StartChoice::Settings) { KeepSpot(start, settings); screen = Screen::Settings; }
        else if (c == StartChoice::Start) screen = Screen::Play;
    }
    else if (screen == Screen::Settings)
    {
        if (ShowSettings(ui, settings, prefs.hints, prefs.speed, f)) screen = Screen::Start;
    }
}
```

## 9. The UI critic check (answer each with yes before the first install)

- [ ] Does each panel have one title, about five widgets at most, and one Primary button at the bottom, with labels of
  one to three words?
- [ ] Are the sizes right for the distance: `scale` set, Body text or larger, buttons at least 3 degrees (66 mm at
  1 m), and a panel height from the formula in section 3?
- [ ] Is each panel 1.0 to 1.5 m away (pointed at) or 0.45 m away (poked), never 0.5 to 0.8 m, with its centre 10 to
  15 cm below the eyes, placed once and not head-locked?
- [ ] Is the UI beside the hero or behind it, never over it, with nothing between the user and a poke panel?
- [ ] Are only `accent`, `accentSoft` and `textAccent` changed, from the look of the app, with the accent on one filled
  element for each panel?
- [ ] Does each screen have its own panel, change in one frame (the new one grows in), and show no debug text?
- [ ] Does the game wait and show a Pause panel while `f.focused` is false, a Result panel at the end ("Play again" as
  Primary), and no Quit button (the app menu has it)?
- [ ] Does each action answer in the frame it starts (the kit's states plus a reaction on the 3D object), do empty and
  error states say the fix in words, and was the main text read on a white wall and in a dark room?

Then run the self-review of **quest-native-quality**.
