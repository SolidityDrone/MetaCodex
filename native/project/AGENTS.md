<!-- metacodex:native-app (written by mcx-project for a Native app project; edits inside this block are replaced) -->
# Native Quest app (MetaCodex)

This folder is a native Meta Quest 3 / 3S app built in MetaCodex (C++, OpenXR + Meta's extensions, `mcx_xr.h`): an
immersive app (the project's kind, fixed in `.metacodex/project.json`). A 2D app in a window (Java, Android views) is
the other kind: it needs a project of kind 2D panel and the skill quest-panel-apps
(`/opt/metacodex/quest-native/skills/quest-panel-apps/SKILL.md`).
Before anything, read the skill **quest-native-apps** and follow its flow: `.claude/skills/quest-native-apps/SKILL.md`
(the same at `/opt/metacodex/quest-native/skills/quest-native-apps/SKILL.md`). Features: quest-native-xr; worked
examples: quest-native-recipes; problems: quest-native-debug; the Runner: quest-native-dev-loop. The kits next to
`mcx_xr.h` and their skills: `mcx_gfx.h` (quest-native-rendering: meshes, primitives, lit materials, contact shadows,
glow sprites, particles, trails, easing),
`mcx_ui.h` (quest-native-ui-design: themed panels, buttons, sliders, the visual language), `mcx_physics.h`
(quest-native-physics: rigid bodies, grab and throw), `mcx_model.h` (quest-native-3d-models: glTF models); budgets:
quest-native-performance.

Always:
- **Hands only, mixed reality** (skill quest-native-hands-first, mandatory): passthrough on, `cfg.controllers = false`,
  every action done with the hands (pinch, poke, aim ray, grab); a pinch accepts either hand, and the system gestures and
  the app menu are never an action (quest-native-hands-first, rules 1-3). Controllers or full VR only when the user explicitly
  asks for them; never ask the user which. List each action and its hand input before you say the app is done.
- **A curated app, not a few lines** (skills quest-native-quality and quest-native-art-direction, mandatory): write
  `DESIGN.md` first with its look card, build in passes (blockout, look, motion, UI, polish, self-review), use the kits
  (never raw GL for what a kit does), and go through the self-review before the first install: the first launch must
  already look designed.
- The app runs on `mcx::App` and keeps its app menu; Back to MetaCodex closes the app; Quit closes it. No own exit
  path or OpenXR loop; add menu items with `app.menuItems`. `mcx-app verify` must say `RESULT: verify ok`.
- Check and build freely (`mcx-app check`, `mcx-app build`, `mcx-app verify`). To launch, or for a first install, use
  only `mcx-app launch` / `mcx-app install` (or `mcx-app run`): they ask the user in MetaCodex (LAUNCH / INSTALL) and
  return at once; then tell the user in one short sentence ("ready: tap LAUNCH in MetaCodex") and go on or wait for
  them. Never adb, `am start`, `pm` or any other route.
<!-- /metacodex:native-app -->
