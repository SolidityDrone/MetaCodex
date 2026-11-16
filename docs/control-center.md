# CONTROL: one place for settings, and a calmer main menu

CONTROL is one window for everything that sets the app up, with a rail of categories on its left. The main menu keeps
only the work windows and the quick actions.

## The split: work windows stay windows, setup goes into CONTROL

| Kind | Windows | How you reach them |
|---|---|---|
| Work (things you look at while working; several at once) | terminals, TERMS (the list), FILES, THREADS, THREAD (+ its WORKSPACE), NEW THREAD, WEB, LINKS, CLIPBOARD, agent views, LOG · MESSAGES | the main menu, their own buttons, voice |
| Setup (one at a time, in one place) | SETTINGS (display, look and sound, focus, hands, about), WRIST MENU, VOICE, AGENTS, MQH, VAULT, SYSTEM | CONTROL |

Setup is not merged into one big window: each category keeps its own window (its tabs, its tests), and a sidebar
lies on its left, on its surface. That keeps the pages as rich as they are (MQH's five tabs, the gesture studio, the vault's git, the
voice models), while the user sees one control center. The seven windows have one size (`ControlPageW` x
`ControlPageH`, 0.76 x 0.66 m): a category taking another's place covers the same rectangle, so it
never grows over a neighbour and the view does not jump.

## CONTROL (`Shell/ShellControl.cs`)

- **The sidebar**: a narrow rail on the category's window's own surface, on its left, edge to edge (owner 2026-11-16:
  "embedded in the same panel as a sidebar ... otherwise the curve is not handled well"; it was 10 mm off and turned
  12 degrees, and on a bent window the two drew two arcs). Flat, it is in the window's plane; bent, it lies on the
  window's cylinder (the same axis and world radius, the window's curve snapped onto it), so the two draw one arc; it
  moves and scales with the window and leans with it in curve mode, never on its own. Top and bottom edges level (as
  tall as the window). It stays a panel of its own (`RailPose`, `FollowRail`), placed on the window's surface like a
  thread group's windows on the group's curve: merging it into the seven windows would change seven page builders and
  every width the placement and the FLOAT ring use. It follows its window in the layout tick, in the window's
  `BendChanged` (the same tick the curve eases) and once more before the frame is drawn (`Application.onBeforeRender`,
  after the router's drags and the window's lean): never a frame behind. Eleven categories in three groups, each with
  a live status on the right:
  - THIS HEADSET: ◧ DISPLAY (fixed / float), ◔ WRIST MENU (the rings' counts, "6 + 5"; "dock" when the DOCK is the
    main menu), ◈ LOOK & SOUND (sound / muted), ◐ FOCUS (on / off; the same glyph as FOCUS on the menu), ✋ HANDS
    (pinch / poke / both)
  - AI: ♫ VOICE (the AI service's state), ◆ AGENTS (how many are ready, else "sign in"), ⬡ MQH (running / starting /
    off), ⚿ VAULT (how many secrets)
  - LINUX & APP: ⬢ SYSTEM (first-use setup), ✦ ABOUT (the version)
  - Row names, title and group captions are in the display font (Rajdhani capitals), the console look.
- **One category at a time**: another category (from the rail, voice, a button, `ui.open`) takes the place of the
  shown one: same left edge, same top edge, same size, the same curve and lean, the rail does not move. DISPLAY, LOOK & SOUND, FOCUS, HANDS and
  ABOUT are pages of the SETTINGS window (its own tab bar is gone: the rail is the navigation; a line says what the
  page is for). Each has its category's id: `display`, `look`, `focus`, `hands`, `about` (SETTINGS' tabs 1, 3, 2, 0,
  4); `control` opens CONTROL on the last category shown (a page reached any way counts), and toggling or closing
  `control` takes the window and the rail together.
- **Moving it**: drag the window by its header, or the rail by its bar: the other follows. ✕ on the window closes
  both (the header ✕ is large and is hit beyond what it draws: docs/ui.md). FLOAT: the pair takes one slot of the ring,
  the rail on the ring's curve with its window.
- **Where it opens**: in front of the user, with room for the rail on its left (the placement reserves its width). It comes
  back where it was while that place is free and in view.
- Every old id still opens its page (`agents`, `mqh`, `vault`, `git`, `skills`, `gestures`, `daemon` (MQH > STATUS),
  `focus`, `storage`, ...), with the rail; voice also knows "control panel", "control center".
- **Cross links**: HANDS > GESTURE STUDIO ▸ opens MQH on GESTURES; SYSTEM > STORAGE > AI MODELS opens VOICE; ABOUT >
  LICENCES ▸ opens FILES at `/usr/share/doc/metacodex`. System pages of Horizon OS sit where they belong: HORIZON
  HANDS ⚙ on HANDS, APP PERMISSIONS ⚙ on ABOUT (LINKS keeps only ⟳ SCAN; FILES has FILES APP).

## The main menu (DOCK and wrist menu): 11 tiles by default

- One table builds both (`MainMenuItems`, `Shell/ShellWrist.cs`): id, glyph, line icon, label, action, lit state, and
  the first start's lock step, in the order the user arranged on CONTROL > WRIST MENU (the default below until then).
- Inner ring (most used): TALK, TERMS, FILES, THREADS, WEB, CONTROL.
- Outer ring: CLIP, FOCUS (on / off), SELECT, KEYS (the system keyboard for the terminal), CENTER.
- App plugins can add up to 6 tiles of their own (`menu.add`), at the end of the outer ring; gesture and behaviour
  plugins have none on the menu.

## WRIST MENU (`Shell/ShellWristSettings.cs`, page `wrist-settings`)

The wrist menu's own page in CONTROL, its own window of CONTROL's size. Settings `wrist` in the settings file:

- **Rings, by drag and drop** on a copy of the wrist menu (the twin): pinch a tile and drag it to another place, the
  other ring, or off the copy to take it off the menu; the copy shows where it lands (an empty slot, the other tiles
  move aside) and its core says INNER, OUTER or TAKE OFF. The inner ring holds 3 to 8 tiles, the outer up to 12; the
  rings share their circle evenly. CONTROL never leaves the menu (dropped off, it goes back, with the reason). A tap on
  a tile of the copy chooses it.
- **The palette**: SYSTEM and APPS tiles off the menu (glyph and name); drag one onto the copy to add it at that place,
  a tap adds it at the end of the outer ring. App plugins that are off show dimmed (turn them on in MQH > PLUGINS). A
  plugin that is gone is skipped; a new item or a new plugin's tile goes to the end of the outer ring.
- **Input**: the router's pinch drag (the element under the pinch gets it every frame until it is let go), with the
  hand ray at the page's distance; the ray meets the copy's own plane; picking is generous (anywhere in a ring's band).
- **ICON** (a plugin's tile): ◂ ▸ through the generic icons (`UiIconAtlas.PluginIcons`); PLUGIN'S gives back the
  plugin's own.
- **Look**: MENU SIZE (0.8-1.3 x), TEXT SIZE (0.8-1.4 x, the names and the time; a name still fits its tile), GLASS
  (its own opacity, not PANEL OPACITY), CORE: BATTERY / NETWORK / CLOCK, "Fold the rings after a tap".
- **APPLY**: every change shows on the copy; APPLY makes it the wrist menu (and the DOCK's order), CANCEL drops it,
  RESET LAYOUT sets the copy's rings back to the default (APPLY still needed).
  Closing the page drops what was not applied. The copy exists only while the page is open.
- **Voice**: open_panel `wrist-settings` ("wrist menu settings", "radial menu settings"); set_setting
  `wrist_menu_size`, `wrist_text_size`, `wrist_opacity`, `wrist_battery`, `wrist_network`, `wrist_clock`,
  `wrist_close_tap` (apply at once; "make the wrist menu bigger" is its size).
- The wrist menu is the default main menu ("Main menu on the wrist" in DISPLAY is on for a new install); with it off the DOCK is the main menu and the wrist menu is not made. The DOCK shows
  the same tiles in the same order. Its tiles show what is on as the wrist's do (selected while TALK listens, FILES / THREADS / WEB /
  TERMS / CLIP are open, CONTROL shows, FOCUS and SELECT are on, the keyboard is up): a state-only change, 4 times a
  second.
- Gone from the menu (inside CONTROL): AGENTS, MQH, SYSTEM, VOICE, VAULT, GIT, SETUP. THREADS gets a ◆ AGENTS button
  (providers are what threads need most).

## Smart places for work windows

A window opened from the menu or by voice opens beside the window it belongs to, when that one is open (otherwise in
front, as before): TERMS beside the active terminal; FILES beside the active terminal, else the open thread; CLIPBOARD
beside the open thread, else WEB; LINKS beside WEB; NEW THREAD beside THREADS. A window the user placed comes back
where it was while that place is free and in view.

## Tests

- Editor self-test section `control` (`Editor/MetaCodexUiTestControl.cs`): menu counts and entries, the rail on its
  page's surface (edge to edge, one plane), category swaps in place, old ids, the close link, FLOAT room and the seam
  on the ring, the reserved place, statuses, contextual openers; `ControlRailOnCurve`: bent, one axis, one world
  radius, the seam's corners on each other, a ray at a row hits it, leaning high above the eyes the rail leans with
  its window and none of its own, a swapped category keeps the curve and the lean (picture `ui-control-bent.png`);
  `HeaderCloseSize`: every window's header ✕ (drawn size, glyph, hit rect, nothing on it, the title before it; the
  terminal frame's too), and on a bent, leaning window rays at it, past it (also 2 mm past the frame's corner) and
  left of it hit it and a pinch past the frame's corner closes the window; then `ControlPolish`: page ids, `control`
  toggled and closed, one size, HANDS' row and the studio link, MQH's title and STATUS, DISPLAY's dimmed controls, ABOUT's buttons, LINKS / FILES / SYSTEM rows, AGENTS' buttons, LOG's
  CLEAR ALL, the dock's lit tiles, ShowTerminalKeyboard; then `WristMenuPage`: the layout rules, the page with its
  twin, drags on and off the twin and from the palette, CANCEL / APPLY / RESET LAYOUT, and the voice settings. Sections `wrist`, `web`, `mqh` and `placement` (the
  five-panel scene and the opened-from-a-button scene) check CONTROL too.
- Headset: [TEST_CHECKLIST.md](TEST_CHECKLIST.md), areas "CONTROL and settings" and "Main menu: DOCK and wrist menu".
