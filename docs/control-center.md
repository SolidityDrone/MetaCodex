# CONTROL: one place for settings, and a calmer main menu

Owner, 2026-11-11: the radial (wrist menu) is overwhelming (19 tiles); MQH, agents and providers are not "in
settings"; wanted a comprehensive control panel with a sidebar of categories, windows merged and placed smartly,
rich but intuitive and organized, without merging too much and without oversimplifying.

## The split: work windows stay windows, setup goes into CONTROL

| Kind | Windows | How you reach them |
|---|---|---|
| Work (things you look at while working; several at once) | terminals, TERMS (the list), FILES, THREADS, THREAD (+ its WORKSPACE), NEW THREAD, WEB, LINKS, CLIPBOARD, agent views, LOG · MESSAGES | the main menu, their own buttons, voice |
| Setup (one at a time, in one place) | SETTINGS (display, look and sound, focus, hands, about), VOICE, AGENTS, MQH, VAULT, SYSTEM | CONTROL |

Setup is not merged into one big window: each category keeps its own window (its tabs, its tests), and a sidebar
rides on its left. That keeps the pages as rich as they are (MQH's five tabs, the gesture studio, the vault's git, the
voice models), while the user sees one control center. The six windows have one size (`ControlPageW` x
`ControlPageH`, 0.76 x 0.66 m; branch feat/polish): a category taking another's place covers the same rectangle, so it
never grows over a neighbour and the view does not jump.

## CONTROL (`Shell/ShellControl.cs`)

- **The sidebar**: a narrow rail docked on the left of the category's window, top edges aligned, turned a little
  towards the user. Ten categories in three groups, each with a live status on the right:
  - THIS HEADSET: ◧ DISPLAY (fixed / float), ◈ LOOK & SOUND (sound / muted), ◐ FOCUS (on / off; the same glyph as
    FOCUS on the menu), ✋ HANDS (pinch / poke / both)
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
  `focus`, `storage`, ...), now with the rail; voice also knows "control panel", "control center".
- **Cross links**: HANDS > GESTURE STUDIO ▸ opens MQH on GESTURES; SYSTEM > STORAGE > AI MODELS opens VOICE; ABOUT >
  LICENCES ▸ opens FILES at `/usr/share/doc/metacodex`. System pages of Horizon OS sit where they belong: HORIZON
  HANDS ⚙ on HANDS, APP PERMISSIONS ⚙ on ABOUT (LINKS keeps only ⟳ SCAN; FILES has FILES APP).

## The main menu (wrist and dock): 19 -> 13

- One table builds both (`MainMenuItems`, `Shell/ShellWrist.cs`): glyph, line icon, label, action, lit state.
- Inner ring (most used): TALK, TERM, FILES, THREADS, WEB, CONTROL
- Outer ring: TERMS, CLIP, FOCUS (on / off), SELECT, KEYS, TABLE, CENTER (TABLE: the keyboard laid on a table, owner
  2026-11-10)
- The dock (when the wrist menu is off): the same in the same order, two rows of six, without TABLE (a wrist and
  voice command). Its tiles show what is on as the wrist's do (selected while TALK listens, FILES / THREADS / WEB /
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
  CLEAR ALL, the dock's lit tiles, ShowTerminalKeyboard. Updated: `wrist`, `web`, `mqh`, `placement` (the five-panel
  scene and the opened-from-a-button scene).
- Headset: docs/TEST_CHECKLIST.md sections 26 and 35.
