<!-- metacodex:native-app (written by mcx-project for a Native app project of kind 2D panel; edits inside this block are replaced) -->
# 2D panel app for the headset (MetaCodex)

This folder is a 2D app for Meta Quest 3 / 3S built in MetaCodex: Java and plain Android views, shown by the headset as
a window (a panel) beside the user's other windows. It is not an immersive app: no OpenXR, no `mcx_xr.h`, no 3D scene.
The kind is fixed (`.metacodex/project.json`, `"kind": "panel"`); an immersive app needs a project of kind immersive.

Before anything, read the skill **quest-panel-apps** and follow its flow: `.claude/skills/quest-panel-apps/SKILL.md`
(the same at `/opt/metacodex/quest-native/skills/quest-panel-apps/SKILL.md`). Its references: the window and design
rules (`references/design.md`), files and the network (`references/android-apis.md`), errors and install failures
(`references/debug.md`).

Always:
- Start with `mcx-app new .` (the 2D template) or `mcx-app new . --example zipper`; Java only, no Gradle, no AndroidX,
  no Kotlin; plain Java libraries go in `libs/*.jar`.
- Keep `Mcx.java` and its calls (`Mcx.start`, `Mcx.topBar`, `Mcx.log`): they give the window Back to MetaCodex and
  Report, and the log `mcx-app logs` reads. Every user action and every error writes a `Mcx.log` line.
- A window, not a phone screen: it must work from 360dp to about 1280dp wide (it scrolls, rows wrap), buttons at least
  48dp (60dp for the main ones), text 18sp and up, the dark theme of `Mcx`, no pure black or white, a hover state on
  every control. Files only through the system file picker (no storage permission); network needs INTERNET.
- Check and build freely (`mcx-app check`, `mcx-app build`, `mcx-app verify`; `verify` must say `RESULT: verify ok`).
  To launch, or for a first install, use only `mcx-app launch` / `mcx-app install` (or `mcx-app run`): they ask the
  user in MetaCodex (LAUNCH / INSTALL) and return at once; then tell the user in one short sentence ("ready: tap
  LAUNCH in MetaCodex") and that the app opens as a window while MetaCodex waits in the background. Never adb,
  `am start`, `pm` or any other route.
<!-- /metacodex:native-app -->
