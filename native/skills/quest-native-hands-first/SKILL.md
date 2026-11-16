---
name: quest-native-hands-first
description: MANDATORY for every native Quest app made in MetaCodex, read before writing any code - apps are mixed reality (the room visible through passthrough) and use the hands only (no controllers) unless the user's request explicitly asks for controllers or for full VR. Owns the hand rules that every other quest-native skill links to - the pinch (either hand), the app menu, the headset's system gestures, the cameras' view, the HAND_TRACKING permission, the HUD check - plus the mcx::Config input fields, the manifest entries, the triage "pinch does not work" and the check before calling an app done. Use for every new app, every change to an app's input, and every review.
---

# Hands first, mixed reality first (strict)

MetaCodex is used with the hands, in the room. A native app made here follows the same rule. This skill wins over any
other skill, example or habit that says "support both" or starts from controllers. This skill owns the hand rules;
the other quest-native skills point here.

## Hard rules

1. **A pinch accepts EITHER hand.** An action that uses a pinch takes the pinch that starts (`pinchStarted`) on
   whichever hand is tracked. Never only `hand[1]`. The only two-hand action is "both hands pinch" (scale, turn), and
   it is named as such.
2. **Two gestures are not the app's.** The left hand's system gesture (palm towards your face, then pinch: the
   headset's quick actions) and the app menu gesture (the same posture with the pinch, left hand) are never used for an
   action of yours. While a hand's `systemGesture` flag is set, `pinchStarted` is false: a pinch with the palm towards
   you does not count, and your code needs no extra check for it.
3. **The way back is the app menu, and the headset's system menu always works.** The app menu opens with the menu gesture
   of the left hand (palm towards your face, then pinch) or with the left controller's menu button. It needs the hand
   aim extension (`XR_FB_hand_tracking_aim`): without it the menu opens with the controller only. Its items: Back to
   MetaCodex, Report to MetaCodex, Quit. The headset's own system menu (Horizon OS: the Meta button, or the left hand's
   system gesture) opens over any app and always works, even when the app does not respond. The app says both on its
   first screen, in one short line, for example "Menu: left hand, palm towards your face, pinch. The headset menu always
   works."
4. **Hands must be in view of the cameras.** A pinch counts only when the thumb and the index are both visible: the hand
   in front of the chest, fingers up, in view. A hand out of view (at the side, behind, low, or turned so that the
   thumb or the index hides) is not tracked: nothing happens and nothing jumps. A pinch with the palm towards your face
   is the system gesture: the app does not count it. Turn the back of the hand towards you and pinch.
5. **Before "done": list the hands, check the permission and the HUD.** List every action and its hand input. Check
   that the app has the permission: after its first start, `mcx-app verify <dir>` must report `HAND_TRACKING granted,
   hand trackers ready`. The HUD shows `hands L ok/- R ok/-` (a tracked hand, or `-`), `PINCH` while a pinch counts, and
   `palm` when a palm faces you. The HUD is the check, not the app: the app's controls are on its own screen. An app whose
   only screen is the HUD is right only when the user asked for one. A new app keeps the hands line of the nightvision
   example's HUD while it is in development.
6. **A toggle changes on the pinch edge.** A toggle reads `pinchStarted` (once per pinch), never `pinching` on every
   frame, and never the joint distance. Check `h.tracked` first. A toggle that does not change on a pinch is a bug in the
   app, not in the headset.

## The defaults (do not ask the user about them)

| | Default | Change it only when |
|---|---|---|
| Reality | **Mixed reality**: `cfg.passthrough = true`, the app clears to alpha 0 so the room shows | the request explicitly says VR, immersive, a full world, or "hide the room" |
| Input | **Hands only**: `cfg.hands = true`, `cfg.controllers = false` | the request explicitly names controllers ("controller", "Touch", "trigger", "joystick", "thumbstick", "A/B buttons") |

- Never ask "hands or controllers?" or "VR or mixed reality?": the answer is hands and mixed reality.
- When the user asks for controllers, add them **and keep every action working with the hands** too.
- An example that reads controllers as well (the nightvision example: trigger, stick, A, grip) is a demo: its every
  action also works with the hands. A new app copies only the controller code its request needs.

## Hand interactions (mcx_xr.h: `app.hands.hand[0]` left, `[1]` right)

| Action | Hand input | Fields |
|---|---|---|
| select, press, place, toggle | index pinch of EITHER hand: loop over both hands | `h.pinchStarted && !h.systemGesture` (this frame), `h.pinching` (held) |
| press a button or panel in reach (40-60 cm) | poke with the index fingertip of either hand | `h.Joint(XR_HAND_JOINT_INDEX_TIP_EXT)` against the button's plane |
| point at something far | the aim ray of the hand that points | `h.aimValid`, `h.aim` (pose: position + forward) |
| grab and move | pinch on the object (either hand), move, release | pinch start inside the object, follow `Joint(XR_HAND_JOINT_INDEX_TIP_EXT)` while `h.pinching`; one grabber per hand |
| scale or turn (the two-hand action) | both hands pinching: the distance and angle between them | both `pinching` and not `systemGesture` |
| the app menu and the system gestures | MetaCodex's and the headset's: never an action of yours | `h.menuGesture` is the menu's (your code does not read it); `h.systemGesture` is only read to show the `palm` state (rule 5) |

- Always check `h.tracked` (a hand out of view does nothing, never jumps).
- Every target is at least 3 cm big and 40-60 cm away when poked; far targets use the aim ray.
- Show feedback for every hand action: a highlight on hover (ray or fingertip near), a change on pinch.
- Text in the app never names controllers or buttons: say "pinch", "touch", "point".
- The UI kit (mcx_ui.h) already loops over both hands for pinch and poke; your own code must do the same.

## Manifest (AndroidManifest.xml)

- `<uses-permission android:name="com.oculus.permission.HAND_TRACKING" />`
- Hands only: `<uses-feature android:name="oculus.software.handtracking" android:required="true" />`
  (hands and controllers on request: `android:required="false"`).
- Mixed reality: `<uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />`.
- No controller-only feature or requirement. `mcx-app verify` must say `RESULT: verify ok`.
- HAND_TRACKING is granted when the app is installed (install-time on Horizon OS, verified on the Quest 3S). The app
  asks at run time only as a fallback (when it is not granted). If the app's log says `not granted`, the fix is a
  reinstall: the user uninstalls the app in the Library, then MetaCodex installs it again. `mcx-app verify` reports the
  state of the last start.

## Pinch does not work: check in this order

1. **The permission.** `mcx-app verify <dir>` after the first start: `HAND_TRACKING granted, hand trackers ready`. If
   not: reinstall (rule 5). Nothing else is checked until this passes.
2. **The hands are tracked.** On the HUD, `hands L ok` or `hands R ok` when the hand is in view (fingers up, in front of
   the chest). `-` means not tracked: bring the hand into view; the cameras need the thumb and the index.
3. **Which hand pinches.** Either hand works (rule 1). Try the other one before you change the app.
4. **The pinch faces the cameras.** The HUD shows `palm` when the palm faces you: then no pinch counts (rule 4). Turn the
   back of the hand towards you and pinch again.
5. **The app's toggle.** It reads `pinchStarted` (the edge), not `pinching` (held) on every frame, and it reads input only
   while `f.focused`. Check with `mcx-app logs <dir>`: the app logs its state changes (for example `mode ...`,
   `passthrough on`); no line means the code did not reach the toggle.

If all five pass and the pinch still does nothing, ask the user what the HUD shows at the moment of the pinch.

## Before you say it is done

List in your reply every action of the app and its hand input (one line each), for example "place a marker: index
pinch of either hand". Then `mcx-app verify <dir>` must say `RESULT: verify ok` and, after the first start, `HAND_TRACKING
granted, hand trackers ready`. An action without a hand input is a bug: fix it before you install.
