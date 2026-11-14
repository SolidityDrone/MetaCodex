---
name: quest-native-ui-design
description: Use for every menu, panel, button, slider, toggle, HUD, settings screen, start screen, first-use hint or other 2D interface in a native Quest app made in MetaCodex, and whenever the user asks for the look of a native app (curated, clean, legible, designed, not a debug overlay). Gives the visual language and the themed UI kit mcx_ui.h (hierarchy, sizes from the angle, spacing, colour and contrast, states, motion, placement) and four layouts (start menu, settings, HUD, first-use hint). Read it before writing any UI code.
---

# Native UI design: curated menus and HUDs

Read **quest-native-hands-first** (hands only, mixed reality) and **quest-native-quality** (the passes and the
self-review) first. This skill sets the look. The kit is `mcx_ui.h`: `#include <mcx_ui.h>`. Every name, the menu
items and the full frame loop are in `references/api.md`. Scene lights and materials: **quest-native-rendering**.

## 1. Hierarchy

- One title per panel (`panel.title`, in the header bar). One primary action (`mcx::UiStyle::Primary`) at the
  bottom: for touch, Meta puts frequent actions in the lower half. Other buttons are `Secondary`.
- `Danger` only for a destructive action, on its own confirm panel. About five widgets per panel at most.
  The kit has no scroll view: split a long list into panels.
- Button labels: one to three words ("Start", "Done"). Subtitles and hints: one short line. Say "pinch", "touch" or
  "point", never "button" or "controller".

## 2. Sizes from the angle

The theme is sized for 1 m. `PlaceInFront(f, d)` sets the scale to `d`, so text keeps its angle. Width and height are
metres at the placement distance: a 0.40 m panel at 1 m is `0.40f * d` at distance `d`. Labels do not wrap.

| At 1 m | Size | Angle | Rule |
|---|---|---|---|
| Title / Body / Small | 36 / 26 / 20 mm | 2.1 / 1.5 / 1.1 deg | Body is the smallest text the user must read (quest-native-quality: at least 25 mm at 1 m); Small is for captions |
| Button | 66 mm tall (the default) | 3.8 deg | Meta: primary controls 60 dp; never below 3 deg (48 dp, 22 mm at 0.42 m) |

| Distance from the eyes | Use |
|---|---|
| 1.0 m (0.8 to 3 m) | menus and settings, aimed at with the pinch ray (default) |
| 0.44 m (0.42 to 0.46 m) | a poke-only panel with two or three buttons (Meta: touch distance) |
| 1.5 m, to the side | HUD, out of the middle of the view |
| 0.5 to 0.8 m | never: the hand-to-ray handoff zone (Meta) |

## 3. Spacing grid

Every space is a multiple of 4 mm at 1 m: padding 24 mm, gap 16 mm, least gap 12 mm (Meta minimum), corner 22 mm,
header 58 mm. Use `ui.Space(metres)` in those steps. Two or three buttons on one line: `ui.Row(n)` before them.

## 4. Colour and contrast

| Role | sRGB | Use |
|---|---|---|
| Text | 0xE2F4FF | every instruction and label |
| Text dim | 0x9DBDD2 | secondary facts, in a dark room only |
| Accent | 0x35D0FF | one filled element per panel, a toggle on, a slider fill, hover |
| Title text | 0x8FEBFF | the title |
| Frame | 0x5F7C9C | the edge of a secondary button |
| Glass | 0x08121B, alpha 0.86 | every panel: dark, translucent |
| Ok, warn, danger | 0x46F28C, 0xFFB547, 0xFF4D5E | a shape or a word with the colour; text only in a dark room |

- Accent sparingly. Never accent body text. No pure white, no pure black: take colours from `ui.theme`.
- WCAG 2.1 (Meta): 4.5:1 for text, 3:1 for parts. Over a white wall, the worst passthrough, main text is 4.8:1;
  dim 2.7:1, ok 3.7:1, warn 3.1:1, danger 1.7:1 and the secondary edge 1.2:1. Only main text carries an instruction.
  Check each panel on the headset, against a white wall and in a dark room.

## 5. States and feedback

Every widget reacts in the frame its pinch or poke starts. The kit draws these states:

| State | What the user sees |
|---|---|
| Hover (ray, or the fingertip near) | fill lifts, accent edge and glow; the layout never changes |
| Pressed, clicked | the face sinks 12 percent; a flash for 0.25 s |
| Disabled (`enabled = false`) | faded: add a Label that says why and what fixes it |

Toggle and slider move their knob; the slider shows its value. Hands have no haptics, so play a short sound on each
selection when the app has sound. Say the fix in words for empty and error states ("Run Space Setup").

## 6. Motion

- A panel appears in 0.18 s: it grows from 92 percent and fades in, ease-out. No bounce, slide, spin or motion that
  follows the head.
- Move a panel only when the user walks away, and glide it (the lazy follow in quest-native-recipes).

## 7. Placement and rules

- Place once: `if (!placed) { panel.PlaceInFront(f, d); placed = true; }`. It puts the centre 0.1 m below the eyes,
  turned to the user, yaw only: about 6 deg below eye level at 1 m.
- Head-locked UI is a bug. A HUD is placed once, or follows with a lag. Never anchor a menu to a wrist or a hand.
- Poke uses the index fingertip only. Panels draw over the scene with no depth test: keep 3D objects out of their view.
- No debug text on screen (no FPS, state names, counters or depth). Use `mcx::Log`.
- The app menu stays: Back to MetaCodex and Quit are built in. Add items with `app.menuItems`; never draw your own
  Quit or Exit.

## 8. Layouts

Each screen is a function, called between `ui.Begin` and `ui.End`. Keep the calls in one fixed order per screen:
widget state is kept by call order.

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
    if (!s.placed) { p.PlaceInFront(f, 1.0f); s.placed = true; }   // 1 m ahead
    p.title = "Lantern";
    p.width = 0.40f;                                   // 0.40 m at 1 m
    p.height = 0.29f;
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
bool ShowSettings(mcx::Ui& ui, UiScreen& s, bool& sound, float& volume, const mcx::Frame& f)
{
    mcx::UiPanel& p = s.panel;
    if (!s.placed) { p.PlaceInFront(f, 1.0f); s.placed = true; }
    p.title = "Settings";
    p.width = 0.40f;
    p.height = 0.33f;
    ui.Open(p);
    ui.Toggle("Sound", sound);
    ui.Slider("Volume", volume, 0.f, 1.f);             // true when changed
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
        p.pose.position.x += 0.5f;                     // 50 cm to the right
        h.placed = true;
    }
    p.input = false;
    p.width = 0.22f * 1.5f;                            // 0.22 m at 1 m, times the distance
    p.height = 0.075f * 1.5f;
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

## 9. Before you call the UI done

Check sections 1 to 7 of this skill, test the main text on a white wall and in a dark room, then run the self-review
of **quest-native-quality**.
