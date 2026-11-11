---
name: quest-native-apps
description: Make a native Meta Quest app (Quest 3 / 3S, Horizon OS) on this headset, from a one-line request to an installed app with its own Library icon - C++ with OpenXR and Meta's extensions (passthrough, environment depth, hands, controllers, the room, anchors), built, checked, installed and launched by MetaCodex itself (mcx-app; no PC, no Unity, no Gradle). Use whenever the user wants a native, standalone, separate, "real" or headset app, a VR or mixed-reality app, a tool, toy, demo, utility or experiment that runs on its own in the headset, or asks to build, install, launch, update or fix such an app. Entry point of the quest-native-* skills. For behaviour inside MetaCodex itself use quest-scripting, quest-gesture-plugins or quest-views instead.
---

# Native Quest apps: from "I want an app that ..." to an app in the Library

The user says what the app should do; you do everything else. Their only jobs: tap **Install** once per new app
(Android's dialog), answer permission dialogs inside the app, and tell you what they see. Every step is one
`mcx-app` command; lines starting `>>> ASK THE USER` are for the user (say them short, then let the command wait);
`RESULT:` lines are outcomes; `PROBLEM:` lines (verify) must be fixed.

The suite: **quest-native-xr** (every Quest feature: calls + manifest entries), **quest-native-recipes** (worked
example and short recipes), **quest-native-dev-loop** (the Runner: seconds per edit, no APK), **quest-native-debug**
(errors, verify problems, install failures, logs). Read the one you need when you get there.

## The flow (do it in this order)

1. **Understand the request.** Ask only what you cannot decide (at most one short question). Decide yourself:
   mixed reality (the room visible: passthrough) or full VR; hands, controllers or both (support both unless told
   otherwise); which Quest features it needs (quest-native-xr). A Quest 3 / 3S app: depth and passthrough are there.
2. **Name it.** Folder `/root/apps/<name>` (lower case, short), label = a short title, package
   `com.metacodex.user.<name>`. Never reuse another app's package; never `com.pnncl.metacodex`, `com.oculus.*`, `com.meta.*`.
3. **`mcx-app doctor`.** `WAIT`: the first-use setup is running, wait for it. `MISSING`: `nice -n 10 mcx-setup quest`
   (400 MB download, 2.5 GB on disk). `warn no builder daemon`: MQH is not running; builds still work, slower for the
   frame rate.
4. **Start from the right base** (`mcx-app new` copies it and sets package and label):

   | The app | Start with |
   |---|---|
   | uses passthrough, depth, hands, controllers, the room, anchors, text panels (almost everything) | `mcx-app new /root/apps/<name> --example nightvision --label "<Label>"`, then replace `src/main.cpp`'s scene (keep the `mcx::App` loop) and remove the manifest entries of features you drop |
   | a plain VR scene, no Meta features | `mcx-app new /root/apps/<name> --label "<Label>"` (the template: three cubes) |
   | visual experiments you want to see change every few seconds | `mcx-app new /root/apps/<name> --module --label "<Label>"`: quest-native-dev-loop |

5. **Write it** on `mcx_xr.h`: whole small apps to adapt are in quest-native-recipes (a floating clock / text panel
   over passthrough, pinch and buttons, the room, anchors, the icon); every feature's calls and manifest entries are
   in quest-native-xr. Give it its own icon (recipe 6) and remove the example's features and manifest entries you do
   not use. Keep it small: one feature, then the next.
6. **`mcx-app check /root/apps/<name>`** (about 1 s): fix every `file:line:col: error:` and check again.
7. **`mcx-app build /root/apps/<name>`** (about 2 s after the first build): `RESULT: APK ...`.
8. **`mcx-app verify /root/apps/<name>`**: every `PROBLEM:` is a missing manifest entry, an unresolved symbol, a
   missing icon or a wrong device list: fix and build again until `RESULT: verify ok`.
9. **Tell the user in one sentence what the app will do**, then `mcx-app install /root/apps/<name>`. It asks the user
   to tap Install (new app) or goes through silently (an update of an app MetaCodex installed). Wait for `RESULT:`.
10. **`mcx-app launch /root/apps/<name>`**: the app opens, MetaCodex goes to the background. Tell the user exactly what
    to look at and try (controls, what should appear), and how to come back: **"the left menu button (or look at
    your left palm and pinch) opens the app's menu: Back to MetaCodex, or Report to MetaCodex to send me a picture
    and its log"**. Every app on `mcx::App` has that menu built in (the template and the examples do).
11. **When they are back**: MetaCodex brings this thread forward. A **report** arrives as a draft in this thread's
    composer, "Feedback on <Label> <version>:", with two attachments (a picture of the app, `log <Label>`): the user
    adds what they saw and sends it; read the picture and the log file, then fix. Without a report:
    `mcx-app logs /root/apps/<name>` and ask what they saw. Then 6-10 again (`mcx-app run <dir>` = build + install +
    launch).

**Done** = `verify ok`, installed, launched, the user saw it work, and you told them the app's name in the Library.

## Updates, versions, the key

- Same package + same key + `android:versionCode` not lower = an update; MetaCodex's own installs update without a
  dialog. Raise `android:versionCode` (and `versionName`) when you hand the user a new version; never lower it.
- Every app is signed with `~/.android/metacodex-debug.keystore` (and its private cache `...keystore.signcache.pem`).
  Never delete, regenerate, copy or show either: a new key means every app must be uninstalled first.

## Rules

- Never remove or replace the app menu (Back / Report / Restart / Quit): it is the user's way back. Add items with
  `app.menuItems`; build every app on `mcx::App` (quest-native-xr), never on a bare OpenXR loop.
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
| `mcx-app launch [dir\|package]` | starts the app |
| `mcx-app run [dir]` | build + install + launch |
| `mcx-app dev [dir]` / `mcx-app runner` | module apps in the Runner (quest-native-dev-loop) |
| `mcx-app logs [dir\|package] [-f]` | what the app logged (`LOG(...)` / `mcx::Log(...)`) |
| `mcx-app info [dir\|package]` | installed version, installer of record |

Install states, failure codes and what to tell the user: `references/install.md`. Tools and folders:
`references/environment.md`.
