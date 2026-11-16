# CONTROL: one place for settings, and a calmer main menu

CONTROL is one window for everything that sets the app up, with a rail of categories beside it. The main menu keeps
only the work windows and the quick actions.

## The split: work windows stay windows, setup goes into CONTROL

| Kind | Windows | How you reach them |
|---|---|---|
| Work (things you look at while working; several at once) | terminals, TERMS (the list), FILES, THREADS, THREAD (+ its WORKSPACE), NEW THREAD, WEB, LINKS, CLIPBOARD, agent views, LOG · MESSAGES | the main menu, their own buttons, voice |
| Setup (one at a time, in one place) | SETTINGS (display, look and sound, focus, hands, about), WRIST MENU, VOICE, AGENTS, MQH, VAULT, SYSTEM | CONTROL |

Setup is not merged into one big window: each category keeps its own window (its tabs, its tests), and a sidebar
rides on its left. That keeps the pages as rich as they are (MQH's five tabs, the gesture studio, the vault's git, the
voice models), while the user sees one control center. The seven windows have one size (`ControlPageW` x
`ControlPageH`, 0.76 x 0.66 m): a category taking another's place covers the same rectangle, so it
never grows over a neighbour and the view does not jump.

## CONTROL (`Shell/ShellControl.cs`)

- **The sidebar**: a narrow rail docked on the left of the category's window, top edges aligned and as tall as the window (bottom edges level too, 0.7.19), turned a little
  towards the user. Eleven categories in three groups, each with a live status on the right:
  - THIS HEADSET: ◧ DISPLAY (fixed / float), ◔ WRIST MENU (the rings' counts, "6 + 5"; "dock" when the DOCK is the
    main menu), ◈ LOOK & SOUND (sound / muted), ◐ FOCUS (on / off; the same glyph as FOCUS on the menu), ✋ HANDS
    (pinch / poke / both)
  - AI: ♫ VOICE (the AI service's state), ◆ AGENTS (how many are ready, else "sign in"), ⬡ MQH (running / starting /
    off), ⚿ VAULT (how many secrets)
  - LINUX & APP: ⬢ SYSTEM (first-use setup), ✦ ABOUT (the version)
  - Row names, title and group captions are in the display font (Rajdhani capitals), the console look.
- **One category at a time**: another category (from the rail, voice, a button, `ui.open`) takes the place of the
  shown one: same left edge, same top edge, same size, the rail does not move. DISPLAY, LOOK & SOUND, FOCUS, HANDS and
  ABOUT are pages of the SETTINGS window (its own tab bar is gone: the rail is the navigation; a line says what the
  page is for). Each has its category's id: `display`, `look`, `focus`, `hands`, `about` (SETTINGS' tabs 1, 3, 2, 0,
  4); `control` opens CONTROL on the last category shown (a page reached any way counts), and toggling or closing
  `control` takes the window and the rail together.
- **Moving it**: drag the window by its header, or the rail by its bar: the other follows. ✕ on the window closes
  both. FLOAT: the pair takes one slot of the ring.
- **Where it opens**: in front of the user, with room for the rail on its left (the placement reserves it). It comes
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

- **Rings**: the inner ring (3 to 8 tiles) and the outer ring (up to 12) as rows of buttons in order; pick one, then
  ◂ MOVE / MOVE ▸ (round its ring), TO INNER, TO OUTER, HIDE. OFF THE MENU lists the system items and app plugins'
  tiles that are not on it: a tap puts one at the end of the outer ring. The rings share their circle evenly, however
  many tiles they hold. CONTROL never leaves the menu. A plugin that is gone is skipped; a new item or a new plugin's
  tile goes to the end of the outer ring.
- **ICON** (a plugin's tile): ◂ ▸ through the generic icons (`UiIconAtlas.PluginIcons`); PLUGIN'S gives back the
  plugin's own.
- **Look**: MENU SIZE (0.8-1.3 x), TEXT SIZE (0.8-1.4 x, the names and the time; a name still fits its tile), GLASS
  (its own opacity, not PANEL OPACITY), CORE: BATTERY / NETWORK / CLOCK, "Fold the rings after a tap".
- **A twin**: every change shows on a copy of the wrist menu on the page; APPLY makes it the wrist menu (and the
  DOCK's order), CANCEL drops it, RESET LAYOUT sets the copy's rings back to the default (APPLY still needed).
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

- Editor self-test section `control` (`Editor/MetaCodexUiTestControl.cs`): menu counts and entries, the rail beside
  its page, category swaps in place, old ids, the close link, FLOAT room, the reserved place, statuses, contextual
  openers; then `ControlPolish`: page ids, `control` toggled and closed, one size, HANDS' row and the studio link, MQH's
  title and STATUS, DISPLAY's dimmed controls, ABOUT's buttons, LINKS / FILES / SYSTEM rows, AGENTS' buttons, LOG's
  CLEAR ALL, the dock's lit tiles, ShowTerminalKeyboard; then `WristMenuPage`: the layout rules, the page with its
  twin, HIDE / CANCEL / APPLY / RESET LAYOUT, and the voice settings. Sections `wrist`, `web`, `mqh` and `placement` (the
  five-panel scene and the opened-from-a-button scene) check CONTROL too.
- Headset: [TEST_CHECKLIST.md](TEST_CHECKLIST.md), areas "CONTROL and settings" and "Main menu: DOCK and wrist menu".
