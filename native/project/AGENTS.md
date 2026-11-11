<!-- metacodex:native-app (written by mcx-project for a Native app project; edits inside this block are replaced) -->
# Native Quest app (MetaCodex)

This folder is a native Meta Quest 3 / 3S app built in MetaCodex (C++, OpenXR + Meta's extensions, `mcx_xr.h`).
Before anything, read the skill **quest-native-apps** and follow its flow: `.claude/skills/quest-native-apps/SKILL.md`
(the same at `/opt/metacodex/quest-native/skills/quest-native-apps/SKILL.md`). Features: quest-native-xr; worked
examples: quest-native-recipes; problems: quest-native-debug; the Runner: quest-native-dev-loop.

Always:
- The app runs on `mcx::App` and keeps its app menu; Back to MetaCodex closes the app; Quit closes it. No own exit
  path or OpenXR loop; add menu items with `app.menuItems`. `mcx-app verify` must say `RESULT: verify ok`.
- Check and build freely (`mcx-app check`, `mcx-app build`, `mcx-app verify`). To launch, or for a first install, use
  only `mcx-app launch` / `mcx-app install` (or `mcx-app run`): they ask the user in MetaCodex (LAUNCH / INSTALL) and
  return at once; then tell the user in one short sentence ("ready: tap LAUNCH in MetaCodex") and go on or wait for
  them. Never adb, `am start`, `pm` or any other route.
<!-- /metacodex:native-app -->
