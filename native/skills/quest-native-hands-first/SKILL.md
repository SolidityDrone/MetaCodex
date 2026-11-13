---
name: quest-native-hands-first
description: MANDATORY for every native Quest app made in MetaCodex, read before writing any code - apps are mixed reality (the room visible through passthrough) and use the hands only (no controllers) unless the user's request explicitly asks for controllers or for full VR. Sets the mcx::Config input fields, the hand interactions (pinch, poke, aim ray, grab), the manifest entries, and the check before calling an app done. Use for every new app, every change to an app's input, and every review.
---

# Hands first, mixed reality first (strict)

MetaCodex is used with the hands, in the room. A native app made here follows the same rule. This skill wins over any
other skill, example or habit that says "support both" or starts from controllers.

## The defaults (do not ask the user about them)

| | Default | Change it only when |
|---|---|---|
| Reality | **Mixed reality**: `cfg.passthrough = true`, the app clears to alpha 0 so the room shows | the request explicitly says VR, immersive, a full world, or "hide the room" |
| Input | **Hands only**: `cfg.hands = true`, `cfg.controllers = false` | the request explicitly names controllers ("controller", "Touch", "trigger", "joystick", "thumbstick", "A/B buttons") |

- Never ask "hands or controllers?" or "VR or mixed reality?": the answer is hands and mixed reality.
- When the user asks for controllers, add them **and keep every action working with the hands** too.
- An example or template that turns controllers on (quest-native-recipes, the nightvision example): set
  `cfg.controllers = false` and remove the controller code paths, unless the request asked for controllers.

## Hand interactions (mcx_xr.h: `app.hands.hand[0]` left, `[1]` right)

| Action | Hand input | Fields |
|---|---|---|
| select, press, place | index pinch (thumb + index) | `h.pinchStarted` (this frame), `h.pinching` (held), `h.pinch[0]` (0..1) |
| press a button or panel in reach (40-60 cm) | poke with the index fingertip | `h.Joint(XR_HAND_JOINT_INDEX_TIP_EXT)` against the button's plane |
| point at something far | the hand's aim ray | `h.aimValid`, `h.aim` (pose: position + forward) |
| grab and move | pinch on the object, move, release | pinch start inside the object, follow `Joint(XR_HAND_JOINT_INDEX_TIP_EXT)` while `h.pinching` |
| scale or turn | both hands pinching: the distance and angle between them | both `pinching` |
| the app menu (Back to MetaCodex, Quit) | left palm towards the face, pinch: `mcx::App` handles it | `h.menuGesture` (do not use it for anything else) |

- Always check `h.tracked` (a hand out of view does nothing, never jumps).
- Every target is at least 3 cm big and 40-60 cm away when poked; far targets use the aim ray.
- Show feedback for every hand action: a highlight on hover (ray or fingertip near), a change on pinch.
- Text in the app never names controllers or buttons: say "pinch", "touch", "point".

## Manifest (AndroidManifest.xml)

- `<uses-permission android:name="com.oculus.permission.HAND_TRACKING" />`
- Hands only: `<uses-feature android:name="oculus.software.handtracking" android:required="true" />`
  (hands and controllers on request: `android:required="false"`).
- Mixed reality: `<uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />`.
- No controller-only feature or requirement. `mcx-app verify` must say `RESULT: verify ok`.

## Before you say it is done

List, in your reply, every action of the app and the hand input that does it (one line each), for example
"place a marker: right index pinch". An action without a hand input is a bug: fix it before you install.
