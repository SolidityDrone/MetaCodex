---
name: quest-native-art-direction
description: MANDATORY with quest-native-quality before the first line of scene code of a native Quest app made in MetaCodex. Pick the app's look and keep it - a palette of four colours with one accent, one light direction, one material family, one motion style - and make it read over the real room (mixed reality, passthrough). Six ready looks (Night glass, Cosmic, Neon arcade, Soft daylight, Gallery, Toy clay) with exact colours, light and material values, the rules for what reads over passthrough, real scale in metres, composition, motion, the first second, and a table of what looks cheap and its fix. Use when the user asks for an app, game or toy that should look good, beautiful, pretty, polished, epic, stunning, cool, designed, realistic or cartoon; mentions a style, theme, mood, colours, palette, lighting, materials, glow, neon, space, cosmic; or says an app looks flat, cheap, ugly, boring, plain, grey or unfinished.
---

# Art direction: pick a look, keep it, make it read over the room

Read **quest-native-quality** (the brief and the passes) with this skill. The kits: **quest-native-rendering**
(`mcx_gfx.h`: light, materials, shadows, glow, motion; effects in its `references/effects.md`) and
**quest-native-ui-design** (`mcx_ui.h`). This skill decides WHAT it looks like; those skills say HOW to draw it.
Labels: **ours** (a MetaCodex choice; compiled, tune on the headset).

## 1. Pick a look in one minute

Pick from the subject of the request. Never ask the user; say the look in one line of your plan.

| The request is about | Look |
|---|---|
| space, planets, stars, the sky, science | **Cosmic** |
| a game: arcade, breakout, pinball, targets, a score | **Neon arcade** |
| a tool: timer, notes, dashboard, a control panel | **Night glass** (the MetaCodex family look) |
| nature: plants, an aquarium, pets, weather, a garden | **Soft daylight** |
| a model viewer, a museum piece, a product, furniture | **Gallery** |
| toys, blocks, puzzles, something for children, physics play | **Toy clay** |

The user names a style ("make it pink", "realistic", "cartoon"): start from the nearest look and change only the
colours they named. Keep one look for the whole app: every new object takes its colours from the look.

## 2. The look card in DESIGN.md

```text
Look card: <look>
  palette: body <hex>, support <hex>, detail <hex>, accent <hex>   (the accent on the hero and the active thing only)
  light: key from <above-left-front | the sun | the lamp>, <warm | neutral | cool>; ambient sky <...>, ground <...>
  materials: bodies <matte, roughness ...>, accent <glow, strength ...>, glass <where, if any>
  motion: arrive <ease-out 0.35 s, staggered 0.06 s>; idle <what moves, how slowly>; feedback <lift, glow>
  UI accent: <hex> (quest-native-ui-design)
  over the room: <leave the room as it is | dim it a little | fade to a dark sky on request>
```

## 3. The six looks (ours)

| Look | body / support / detail / accent (sRGB) | Light | Materials | Over the room |
|---|---|---|---|---|
| Night glass | 1B2A3A / 2E4A63 / 9DBDD2 / 35D0FF | neutral key from above-left-front | matte bodies, cyan glow edges | as it is |
| Cosmic | 1A1F3A / 3B3F6B / C4DAFF / FFC46B | the sun is the key light, warm | matte planets with a thin rim, a glowing sun with a halo | as it is; a dark sky on request (effects section 2) |
| Neon arcade | 1C1830 / 2C2650 / 35D0FF / FF4FD8 | cool key, low ambient | glossy dark bodies, magenta glow on what you hit, cyan on thin edges only | dim it a little |
| Soft daylight | E8E1D3 / 8FB59A / 6C8FB3 / FF8A5B | warm key, bright sky ambient | matte, soft; glow only on the active thing | as it is |
| Gallery | 3A3836 / 8C857C / D9D2C5 / E0B25A | warm key from above-front, like a spot | the model's own materials; a matte plinth; brass accents | as it is |
| Toy clay | E9C46A / 2A9D8F / E76F51 / 4CC9F0 | warm key, bright ambient | very matte (clay), round shapes, no glass | as it is |

Neon arcade is the one look with two neon colours: the second one (cyan) only on thin edges and lines.
"Dim it a little": `app.passthrough.SetBrightnessContrastSaturation(-10.f, 0.95f, 0.8f)` (brightness -100..100,
contrast and saturation 1 = unchanged); glow then reads more. Never hide the room unless the user asks for VR or space.

The code: one `Look` per app, applied once at init. It sets the light of `mcx::Lit` and the accent of `mcx::Ui`,
and gives the materials. Colours are sRGB hex, as the kits take them.

```cpp
#include <mcx_gfx.h>
#include <mcx_ui.h>

struct Look
{
    const char* name = "Night glass";
    unsigned int body = 0x1B2A3A;         // most surfaces: mid-dark, never pure black
    unsigned int support = 0x2E4A63;      // a second surface tone
    unsigned int detail = 0x9DBDD2;       // small parts, lines, dots, glass tint
    unsigned int accent = 0x35D0FF;       // the ONE saturated colour: the hero, the active thing, glow
    unsigned int uiAccent = 0x35D0FF;     // ui.theme.accent: the Primary button, hover, glow
    unsigned int uiAccentSoft = 0x2A9FD6; // ui.theme.accentSoft: the panel's header bar
    unsigned int uiTextAccent = 0x8FEBFF; // ui.theme.textAccent: titles and values
    XrVector3f keyDir{-0.35f, 0.85f, 0.40f};  // towards the key light: above, a little left and in front
    XrVector3f keyColor{0.95f, 0.92f, 0.87f}; // linear; near white: the colour lives in the materials
    XrVector3f sky{0.28f, 0.31f, 0.36f};      // ambient from above, linear
    XrVector3f ground{0.10f, 0.09f, 0.085f};  // ambient from below, linear
    float bodyRoughness = 0.85f;              // the material family: 0.3 glossy .. 0.9 clay
    float rimLight = 0.25f;                   // edge light on bodies: it outlines them against the room
    float glowStrength = 0.9f;                // the accent glow (mcx::Material::Glow)
};

Look LookNightGlass() { return Look{}; }

Look LookCosmic()
{
    Look k;
    k.name = "Cosmic";
    k.body = 0x1A1F3A;
    k.support = 0x3B3F6B;
    k.detail = 0xC4DAFF;
    k.accent = 0xFFC46B;
    k.uiAccent = 0xFFC46B;
    k.uiAccentSoft = 0xC9923F;
    k.uiTextAccent = 0xFFE2B0;
    k.keyColor = {1.f, 0.86f, 0.66f}; // a warm sun: point keyDir at it per object (effects section 7)
    k.sky = {0.12f, 0.13f, 0.17f};    // not darker over the room: a black night side reads as a hole
    k.ground = {0.05f, 0.05f, 0.06f};
    k.bodyRoughness = 0.7f;
    k.rimLight = 0.35f;
    k.glowStrength = 1.f;
    return k;
}

Look LookNeonArcade()
{
    Look k;
    k.name = "Neon arcade";
    k.body = 0x1C1830;
    k.support = 0x2C2650;
    k.detail = 0x35D0FF;
    k.accent = 0xFF4FD8;
    k.uiAccent = 0xFF4FD8;
    k.uiAccentSoft = 0xB8379C;
    k.uiTextAccent = 0xFFB3F0;
    k.keyColor = {0.85f, 0.88f, 0.95f};
    k.sky = {0.20f, 0.18f, 0.28f};
    k.ground = {0.06f, 0.05f, 0.08f};
    k.bodyRoughness = 0.4f;
    k.rimLight = 0.5f;
    k.glowStrength = 1.f;
    return k;
}

Look LookSoftDaylight()
{
    Look k;
    k.name = "Soft daylight";
    k.body = 0xE8E1D3;
    k.support = 0x8FB59A;
    k.detail = 0x6C8FB3;
    k.accent = 0xFF8A5B;
    k.uiAccent = 0xFF8A5B;
    k.uiAccentSoft = 0xC9643D;
    k.uiTextAccent = 0xFFC9B0;
    k.keyColor = {1.f, 0.94f, 0.84f};
    k.sky = {0.32f, 0.35f, 0.40f};
    k.ground = {0.14f, 0.12f, 0.10f};
    k.bodyRoughness = 0.8f;
    k.rimLight = 0.15f;
    k.glowStrength = 0.5f;
    return k;
}

Look LookGallery()
{
    Look k;
    k.name = "Gallery";
    k.body = 0x3A3836;
    k.support = 0x8C857C;
    k.detail = 0xD9D2C5;
    k.accent = 0xE0B25A;
    k.uiAccent = 0xE0B25A;
    k.uiAccentSoft = 0xA88340;
    k.uiTextAccent = 0xF2DDB0;
    k.keyDir = {-0.2f, 0.9f, 0.5f}; // a spot from above and in front
    k.keyColor = {1.f, 0.95f, 0.88f};
    k.sky = {0.26f, 0.27f, 0.29f};
    k.ground = {0.09f, 0.085f, 0.08f};
    k.bodyRoughness = 0.6f;
    k.rimLight = 0.3f;
    k.glowStrength = 0.4f;
    return k;
}

Look LookToyClay()
{
    Look k;
    k.name = "Toy clay";
    k.body = 0xE9C46A;
    k.support = 0x2A9D8F;
    k.detail = 0xE76F51;
    k.accent = 0x4CC9F0;
    k.uiAccent = 0x4CC9F0;
    k.uiAccentSoft = 0x2E94B8;
    k.uiTextAccent = 0xB5ECFF;
    k.keyColor = {1.f, 0.95f, 0.88f};
    k.sky = {0.30f, 0.31f, 0.34f};
    k.ground = {0.12f, 0.10f, 0.09f};
    k.bodyRoughness = 0.9f;
    k.rimLight = 0.2f;
    k.glowStrength = 0.5f;
    return k;
}

// once at init, after lit.Init() and ui.Init(app). text, textDim, glass and glassAlpha of the UI theme stay as they
// are: they are contrast-tested over a white wall.
void ApplyLook(const Look& k, mcx::Lit& lit, mcx::Ui& ui)
{
    lit.light.keyDir = k.keyDir;
    lit.light.keyColor = k.keyColor;
    lit.light.sky = k.sky;
    lit.light.ground = k.ground;
    ui.theme.accent = k.uiAccent;
    ui.theme.accentSoft = k.uiAccentSoft;
    ui.theme.textAccent = k.uiTextAccent;
}

// the material family: bodies in one of the look's tones, the accent glow, glass, flat marks
mcx::Material LookBody(const Look& k, unsigned int tone)
{
    mcx::Material m = mcx::Material::Matte(tone);
    m.roughness = k.bodyRoughness;
    m.rim = k.rimLight;
    return m;
}
mcx::Material LookGlow(const Look& k) { return mcx::Material::Glow(k.accent, k.glowStrength); }
mcx::Material LookGlass(const Look& k) { return mcx::Material::Glass(k.detail, 0.22f); } // DrawTranslucent only
mcx::Material LookMark(const Look& k) // unlit lines and dots in the detail tone
{
    mcx::Material m;
    m.base = mcx::GfxSrgb(k.detail);
    m.unlit = true;
    return m;
}

// usage: the look of the whole app, decided once
struct LookedScene
{
    mcx::Lit lit;
    mcx::Ui ui;
    Look look = LookCosmic();
    mcx::Material bodyMat, supportMat, glowMat;
    bool Init(mcx::App& app)
    {
        if (!lit.Init() || !ui.Init(app)) return false;
        ApplyLook(look, lit, ui);
        bodyMat = LookBody(look, look.body);
        supportMat = LookBody(look, look.support);
        glowMat = LookGlow(look);
        return true;
    }
};
```

## 4. What reads over the room

The room is the background, and it is busy, grey and a little noisy. Design for it:

- **A stage, not a world.** Content lives on a table top or in a volume 1-2 m wide. Leave the rest of the room
  visible. Never fill the view with a floor, a wall or a sky plane (unless the user asked for VR or space).
- **Light reads, flat grey vanishes.** Emissive edges, a rim (`m.rim` 0.2-0.5 on bodies) and glow sprites read on any
  wall. Mid-grey flat colours disappear into a grey room.
- **Value contrast.** Mid-dark bodies with bright accents read best. No large pure white surface (it glares and looks
  flat) and no pure black (it looks like a hole; the display cannot show steps below about 13 of 255).
- **One saturated accent.** The room is low in saturation, so one saturated colour pops. Five of them look like toys
  in a heap.
- **Grounded.** An object rests ON a real surface (its centre at the surface height plus half its height) and has a
  contact shadow (`mcx::ContactShadow`). A floating object floats for a reason and its shadow is faint.
- **Depth cues.** Shadows, things at several depths (parallax when the head moves), highlights that move with the
  view, and far things that fade into the room (`lit.light.fogStart`, `fogEnd`, `fogOpacity` 0).
- **Occlusion.** The kits draw over the real world: a real chair in front does not hide a virtual ball behind it,
  and a hand does not hide a virtual object (the object draws over the hand). Place content in free space and on top
  of surfaces, never behind furniture; keep things the user touches small and thin (a panel, a button), and show the
  fingertip's contact with the kit's hover state. Real occlusion needs the environment depth in your own shader
  (quest-native-xr: `f.depth`, `cfg.depth = true`): an advanced step with a GPU cost; do it only when the app is
  about the room.
- **Real scale.** People judge size against the real room at once. Use these (ours):

| Thing | Size |
|---|---|
| a table top | 0.72-0.76 m high; place things 30-60 cm from its front edge |
| eye height | standing about 1.6 m, seated about 1.2 m (read the head from `f.views`, never assume) |
| reach | 40-60 cm from the shoulder; things to touch go there |
| a table-top object | 5-40 cm; a ball 3-5 cm; a game board about 0.5 x 0.35 m |
| a room object | 0.3-2 m; a hero on the floor 0.5-1.2 m tall |
| a solar system on a stage | 1.2-1.6 m wide; the sun 0.25-0.35 m; planets 3-11 cm |

## 5. Light: one direction for the whole scene

- One key light from above and a little in front (the default `keyDir`). Never from below, except on purpose.
- Keep the key near white and warm; put the colour in the materials. A strongly coloured key fights the room.
- Ambient: `sky` above, `ground` below. This is your free gradient: a cool sky and a warm ground shade every object
  from top to bottom.
- A point light (a sun, a lamp, a fire): set `lit.light.keyDir` from the object towards the light and call
  `lit.Begin(eye)` again before each object (`Begin` is what sends the light to the shader;
  quest-native-rendering `references/effects.md`, section 7).

## 6. Composition

- **One hero**: the thing the app is about. It is 2-3 times larger or brighter than the rest, and it has the accent.
- **A cast**: a few supporting things in the support tone, smaller, around the hero.
- **The ground**: the real table or floor, never a virtual plane over it.
- **The stage**: its centre 1.0-1.5 m ahead and 0.1-0.35 m below the eyes when standing, or on the table 30-60 cm
  from its front edge. Things the user touches are within 40-60 cm of the hand.
- **The UI** goes beside or below the hero, turned to the user, never over it (panels draw with no depth test).
  Leave the space between the user and what they touch free.

## 7. Motion: one style for the app

| Moment | Motion (kit names in `mcx_gfx.h`) |
|---|---|
| arrive | grow from 0 with `mcx::EaseOut` (calm) or `mcx::EaseOutBack` (game objects), 0.3-0.45 s; next object 0.06-0.08 s later |
| idle | one slow motion on the hero: a bob of 5-10 mm or a glow of plus 10-15 % with `mcx::Pulse` (0.2-0.5 Hz); a turn of 10-20 degrees per second at most |
| feedback | in the frame the pinch or touch starts: a lift of 3-5 mm and a brighter glow, moved with `mcx::Damp` |
| move | `mcx::EaseInOut` from A to B; follow a hand with `mcx::Vec3Damp` (rate 10-20) |
| celebrate | a burst of 20-40 glow particles for about 0.6 s (`mcx::Particles`) |
| leave | shrink with `mcx::EaseIn` in 0.2 s, then stop drawing |

UI panels have their own motion in the kit (grow and fade in over 0.18 s): no bounce, no slide, no spin on UI.

## 8. The first second

1. The room shows at once: clear to alpha 0 (`app.clearAlpha()`); never a black or grey full-view frame.
2. The hero grows in (0.4 s), then the cast, staggered.
3. The start panel appears about 0.3 s after the hero, beside it.
4. No splash screen, no logo, no loading text over the view. Load models before the first frame you draw them in;
   until then, draw nothing in their place.

## 9. Cheap, and the fix

| Looks cheap | Fix |
|---|---|
| flat unlit colour | lit materials: `LookBody` (key light, ambient, rim) |
| default grey everywhere | the look's palette; grey only as the support tone of Gallery |
| everything in the MetaCodex cyan | cyan is the accent of Night glass only; pick the look first |
| z-fighting: two surfaces in one plane flicker | lift one 1-2 mm; never draw a decal at exactly the surface height |
| things pop in or out in one frame | grow in and shrink out (section 7) |
| text too small or too far | the sizes of quest-native-ui-design (body 2.6 cm at 1 m) |
| everything the same size | one hero 2-3 times larger; a clear cast |
| a perfect grid of identical copies | vary the scale (plus or minus 10 %) and the turn a little, seeded |
| glow on everything | glow is the accent: about a fifth of what is seen at most |
| every object spins | one idle motion, on the hero only |
| jitter when following a hand | `mcx::Vec3Damp` with `f.dt`, not a fixed lerp per frame |
| faceted spheres | 48 x 24 for a hero sphere, 24 x 12 for small ones, 10 x 6 for instanced rocks |
| a big floor or sky plane hides the room | the real surface is the ground; a sky only on request |
| a 3D object inside a panel's view | move the panel beside the hero |
| a strongly coloured light | a near-white key; colour in the materials |
| a panel 0.5-0.8 m away | 1.0-1.5 m (pointed at) or 0.45 m (poked), quest-native-ui-design |
| debug lines on screen (FPS, state, depth) | `mcx::Log` only |
| a black night side over the room | raise `sky` and `ground` (Cosmic: 0.12 and 0.05) |

## 10. The critic's look check (answer each with yes before the first install)

- [ ] DESIGN.md has a look card, and every colour in the code comes from it.
- [ ] The hero is clear at a glance: larger or brighter, and it has the accent.
- [ ] Every object is lit (no flat colour) and has a rim or a glow where it meets the room.
- [ ] Every grounded object rests on a real surface and has a contact shadow.
- [ ] Sizes are real metres from section 4; nothing is in the user's face.
- [ ] Nothing pops: things grow in, staggered; things shrink out.
- [ ] One idle motion, slow; every action answers in its frame.
- [ ] The room stays visible (unless the user asked for VR or space), and no virtual plane hides it.
- [ ] The UI accent matches the look; the UI sits beside the hero, not over it.
- [ ] No pure white, no pure black, no debug text.
