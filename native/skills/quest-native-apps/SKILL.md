---
name: quest-native-apps
description: Make a native Meta Quest app (Quest 3 / 3S, Horizon OS) on this headset, from a one-line request to an installed app with its own Library icon - C++ with OpenXR and Meta's extensions (passthrough, environment depth, hands, controllers, the room, anchors), built, checked, installed and launched by MetaCodex itself (mcx-app; no PC, no Unity, no Gradle). Use whenever the user wants a native, standalone, separate, "real" or headset app, a VR or mixed-reality app, a game, tool, toy, demo, viewer, utility or experiment that runs on its own in the headset, or asks to build, install, launch, update or fix such an app. Entry point of the quest-native-* skills. For behaviour inside MetaCodex itself use quest-scripting, quest-gesture-plugins or quest-views instead.
---

# Native Quest apps: from "I want an app that ..." to an app in the Library

> **Always.** Every app runs on `mcx::App` (`#include <mcx_xr.h>`, the template's loop) and keeps its built-in app
> menu (left controller's menu button / left palm pinch). Opened from MetaCodex, the menu has **Back to MetaCodex**
> and **Report to MetaCodex**, and Back **always closes the app** (MetaCodex starts, the app finishes); **Quit** closes
> it too. Never remove, disable or take over the menu input, never write your own exit path (`ANativeActivity_finish`,
> `xrRequestExitSession`, `exit`) or your own OpenXR loop; add your items with `app.menuItems`. Why: an app that only
> went to the background with its own loop froze, and the headset showed "<App> isn't responding" (0.7.15).
> `mcx-app verify` fails apps that break this.
>
> **Always ask before taking the user out of MetaCodex.** Check, build and verify freely. To launch the app, or for a
> first install, use only `mcx-app launch` / `mcx-app install` / `mcx-app run`: MetaCodex shows the user LAUNCH /
> INSTALL / LATER and the command returns at once with `RESULT: requested ...`. Then say one short sentence ("it's
> ready: tap LAUNCH in MetaCodex") and continue or wait; `mcx-app status <dir>` tells you pending / launched / declined.
> This holds in every approval mode (MetaCodex enforces it). Never adb, `am start`, `pm` or any other route.

The user says what the app should do; you do everything else. Their only jobs: tap **Install** once per new app
(Android's dialog), answer permission dialogs inside the app, and tell you what they see. Every step is one
`mcx-app` command; lines starting `>>> ASK THE USER` are for the user (say them short, then let the command wait);
`RESULT:` lines are outcomes; `PROBLEM:` lines (verify) must be fixed.

Mandatory with this one: **quest-native-hands-first** (hands only, mixed reality), **quest-native-quality** (the
brief, the passes, the self-review: how an app gets curated) and **quest-native-art-direction** (pick a look and keep
it: palette, light, materials, motion, what reads over the room). The kits next to `mcx_xr.h` and their skills:
**quest-native-rendering** (`mcx_gfx.h`: lit materials, shadows, glow sprites, particles, trails, easing; effects in its
`references/effects.md`), **quest-native-ui-design** (`mcx_ui.h`), **quest-native-physics** (`mcx_physics.h`),
**quest-native-3d-models** (`mcx_model.h`); budgets: **quest-native-performance**.
The suite: **quest-native-xr** (every Quest feature: calls + manifest entries), **quest-native-recipes** (worked
example and short recipes), **quest-native-dev-loop** (the Runner: seconds per edit, no APK), **quest-native-debug**
(errors, verify problems, install failures, logs). Read the one you need when you get there.

## The flow (do it in this order)

1. **Understand the request.** Ask only what you cannot decide (at most one short question). First read
   **quest-native-hands-first** (mandatory): the app is mixed reality (passthrough) and hands only, unless the request
   explicitly asks for full VR or for controllers; never ask the user about either (a "VR" or "space" look on
   request: the room can fade to a dark sky, quest-native-rendering `references/effects.md`). Decide yourself which Quest
   features it needs (quest-native-xr). A Quest 3 / 3S app: depth and passthrough are there. Then read
   **quest-native-quality** and **quest-native-art-direction** (both mandatory) and write the app's `DESIGN.md` (the
   brief and the look card) before any code. The table "Common requests" below gives the path for frequent ones.
2. **Name it.** The app's folder: the project folder itself when your thread works in a Native app project
   (`/root/metacodex-projects/native/<name>`, made by MetaCodex's + PROJECT: `mcx-app new .` fills it and keeps its
   `.git`, `AGENTS.md`, `CLAUDE.md`), else `/root/metacodex-projects/native/<name>` (lower case, short; never
   `/root/apps`, the old place). Below, `<dir>` is that folder. Label = a short title, package
   `com.metacodex.user.<name>`. Never reuse another app's package; never `com.pnncl.metacodex`, `com.oculus.*`, `com.meta.*`.
3. **`mcx-app doctor`.** `WAIT`: the first-use setup is running, wait for it. `MISSING`: `nice -n 10 mcx-setup quest`
   (400 MB download, 2.5 GB on disk). `warn no builder daemon`: MQH is not running; builds still work, slower for the
   frame rate.
4. **Start from the right base** (`mcx-app new` copies it and sets package and label):

   | The app | Start with |
   |---|---|
   | uses passthrough, depth, hands, the room, anchors, text panels (almost everything) | `mcx-app new <dir> --example nightvision --label "<Label>"`, then replace `src/main.cpp`'s scene (keep the `mcx::App` loop), set `cfg.controllers = false` and remove the controller code and the manifest entries of features you drop (quest-native-hands-first) |
   | a plain VR scene, no Meta features | `mcx-app new <dir> --label "<Label>"` (the template: three cubes) |
   | visual experiments you want to see change every few seconds | `mcx-app new <dir> --module --label "<Label>"`: quest-native-dev-loop |

5. **Write it** on `mcx_xr.h`: whole small apps to adapt are in quest-native-recipes (a floating clock / text panel
   over passthrough, pinch and buttons, the room, anchors, the icon); every feature's calls and manifest entries are
   in quest-native-xr. Give it its own icon (recipe 6) and remove the example's features and manifest entries you do
   not use. Draw with the kits (quest-native-rendering, -ui-design, -physics, -3d-models), and build in the passes of
   quest-native-quality (blockout, look, motion, UI, polish, self-review), steps 6-8 after each pass. Install and
   launch (steps 9-10) only after the self-review: the first launch is the user's first impression, so it must
   already look designed. Never hand over a greybox.
6. **`mcx-app check <dir>`** (about 1 s): fix every `file:line:col: error:` and check again.
7. **`mcx-app build <dir>`** (about 2 s after the first build): `RESULT: APK ...`.
8. **`mcx-app verify <dir>`**: every `PROBLEM:` is a missing manifest entry, an unresolved symbol, a
   missing icon or a wrong device list: fix and build again until `RESULT: verify ok`.
9. **Tell the user in one sentence what the app will do**, then `mcx-app install <dir>`. A new app:
   `RESULT: requested install of ...`: MetaCodex offers INSTALL to the user, then Android's dialog, then LAUNCH; nothing
   more for you to run. An update of an app MetaCodex installed goes through silently (`RESULT: installed`).
10. **`mcx-app launch <dir>`**: `RESULT: requested launch of ...`: MetaCodex offers LAUNCH / LATER. Say "ready:
    tap LAUNCH in MetaCodex", what to look at and try (controls, what should appear), and how to come back: **"the left menu button (or look at
    your left palm and pinch) opens the app's menu: Back to MetaCodex closes it and brings you here, Report to
    MetaCodex also sends me a picture and its log"**. Every app on `mcx::App` has that menu built in. Opened from the
    Library (or reopened from there), the menu has only the app's items and Quit: Quit closes it, the headset goes Home.
11. **When they are back**: MetaCodex brings this thread forward. A **report** arrives as a draft in this thread's
    composer, "Feedback on <Label> <version>:", with two attachments (a picture of the app, `log <Label>`): the user
    adds what they saw and sends it; read the picture and the log file, then fix. Without a report:
    `mcx-app logs <dir>` and ask what they saw. Then 6-10 again (`mcx-app run <dir>` = build + install +
    launch).

**Done** = `verify ok`, installed, launched, the user saw it work, and you told them the app's name in the Library.

## Common requests: the path through the skills

Decide these yourself; do not ask. Base = the nightvision example (hands, passthrough) with its scene replaced.

| Request | Look (art-direction) | Stage and scale | Hero and cast | Skills in order after the mandatory three |
|---|---|---|---|---|
| "a solar system", planets, stars | Cosmic | a 1.2-1.6 m wide volume 1.2 m ahead at chest height; the sun 0.25-0.35 m | the sun (glow sprites, the key light from it); planets with orbit rings, trails | the whole app to adapt: quest-native-rendering `references/effects.md` section 8 (halo, orbits, trails, the room fading to space on request); ui-design (start, a label per planet) |
| "a breakout / pinball / table game on my desk" | Neon arcade | on the TABLE from the room (quest-native-recipes, recipe 3), 40-60 cm from its front edge; a board about 0.5 x 0.35 m | the ball (a trail), the bricks (instanced, one draw per row colour), the paddle under the hand | the whole app to adapt: quest-native-recipes `references/desk-game.md`; physics for grab and throw; ui-design (start, pause, result) |
| "a 3D model viewer", a museum piece, a product | Gallery | on the table or a plinth at 1 m; the model fitted to 0.3-0.5 m | the model on a turntable with a contact shadow | 3d-models (fit, pivot, fallback), ui-design (a menu panel beside it, not over it) |
| a timer, notes, a dashboard, a tool | Night glass | panels at 1 m, 10-15 cm below the eyes | the main panel | ui-design, quest-native-recipes (recipe 1: a panel over passthrough) |
| a toy or sandbox to grab and throw | Toy clay | on the floor or table, real sizes (5-30 cm) | the objects to grab | physics (grab and throw), rendering (contact shadows) |
| something that uses the real room (walls, floor, furniture) | from the subject | on the room's surfaces | what the room shows | quest-native-xr (scene, depth), recipes 3-4 |

## Updates, versions, the key

- Same package + same key + `android:versionCode` not lower = an update; MetaCodex's own installs update without a
  dialog. Raise `android:versionCode` (and `versionName`) when you hand the user a new version; never lower it.
- Every app is signed with `~/.android/metacodex-debug.keystore` (and its private cache `...keystore.signcache.pem`).
  Never delete, regenerate, copy or show either: a new key means every app must be uninstalled first.

## Rules

- Run builds only through `mcx-app check|build|dev` (they go to cores 0-2, off MetaCodex's frame loop), never `clang`,
  `aapt2`, `apksigner` or `build.sh` by hand in a terminal.
- Show the user what you will install before `mcx-app install`; one app change per install; ask after each launch.
- Never install with adb (`adb connect 127.0.0.1:5555`), `pm install` or `cmd package`; never write into other apps'
  folders. Uninstalling is the user's (Library > the app > ... > Uninstall).
- Store builds of MetaCodex cannot install apps (`unavailable: ...`, exit 3): tell the user, never look for a way around.

## Commands

| Command | Does |
|---|---|
| `mcx-app doctor` | tools present? where builds run? may MetaCodex install apps? |
| `mcx-app new <dir> [--module \| --example nightvision] [--package <id>] [--label <name>]` | a new app folder |
| `mcx-app check [dir]` | compile check only |
| `mcx-app build [dir]` | incremental build -> `<dir>/build/<dir name>.apk` |
| `mcx-app verify [dir]` | static checks of the APK (manifest vs the Meta APIs used, symbols, icon, devices) |
| `mcx-app install [dir\|apk]` | Android's installer, waits up to 5 min for the user |
| `mcx-app launch [dir\|package]` | asks the user to open the app (LAUNCH in MetaCodex); returns at once |
| `mcx-app run [dir]` | build + install (+ launch request) |
| `mcx-app status [dir\|package]` | the request: pending / launched / declined / installing / failed |
| `mcx-app dev [dir]` / `mcx-app runner` | module apps in the Runner (quest-native-dev-loop) |
| `mcx-app logs [dir\|package] [-f]` | what the app logged (`LOG(...)` / `mcx::Log(...)`) |
| `mcx-app info [dir\|package]` | installed version, installer of record |

Install states, failure codes and what to tell the user: `references/install.md`. Tools and folders:
`references/environment.md`.
