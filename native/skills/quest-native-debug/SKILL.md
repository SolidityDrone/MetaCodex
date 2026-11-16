---
name: quest-native-debug
description: Diagnose and fix native Quest apps built in MetaCodex - compile errors from mcx-app check/build (file:line:col), verify PROBLEM lines (missing permission or feature, unresolved symbols, icon, devices), install failures and their codes, an app that shows a black screen, closes at once, ignores hands or controllers, or has no depth or passthrough, permission denied, the builder's memory guard and background CPU budget, and reading the app's log with mcx-app logs. Use when any mcx-app command fails or the user reports that a native app does not work as expected.
---

# Debugging native Quest apps

What you can see: the output of `mcx-app` (`RESULT:`, `PROBLEM:`, `hint:` lines), the app's own log
(`mcx-app logs <dir|package> [-f]`: every `mcx::Log(...)` / `LOG(...)` line while it runs; the last line before a
crash shows how far it got), `mcx-app info`, `mcx-buildd status`. What you cannot: logcat, the GPU, the screen. So
log state changes in the app (the layer already logs: session states, `passthrough: started`, `depth: started`,
`scene: N objects`, `extension X: not offered`, `permission ... requested`) and ask the user what they see.

## Build and check

- Errors: `<file>:<line>:<col>: error: <message>` then `RESULT: N error(s) in M file(s)`. Fix them all, `mcx-app check`
  again (about 1 s) before building.
- `RESULT: link failed (undefined symbols ...)`: a declared function without a body, or a library for `LDLIBS=` in
  `build.conf` (e.g. `LDLIBS=-lvulkan`).
- `mcx-build: missing ... (run: mcx-setup quest)` (exit 3): `nice -n 10 mcx-setup quest`.
- `stopped ...: MemAvailable N MB < M MB` (exit 4): the headset is short of memory: tell the user, close windows or
  apps, build again. Never lower the guards (`MCX_BUILD_MIN_MB`, `MCX_BUILD_KILL_MB`).
- `paused: MetaCodex is in the background ...`: the builder keeps its CPU low while MetaCodex is not in front
  (Android would close MetaCodex's agent process otherwise); it goes on when MetaCodex is back. Not an error.
- `mcx-buildd: queued behind N job(s)`: one build at a time; wait.
- `warning: no builder daemon`: MQH is not running; the build runs in this shell at the lowest priority.

## Verify (`mcx-app verify`)

| `PROBLEM:` | Fix in AndroidManifest.xml |
|---|---|
| passthrough (xrCreatePassthroughFB) needs `com.oculus.feature.PASSTHROUGH` | `<uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />` |
| environment depth / scene ... needs `com.oculus.permission.USE_SCENE` | `<uses-permission android:name="com.oculus.permission.USE_SCENE" />` |
| spatial anchors ... needs `USE_ANCHOR_API` | `<uses-permission android:name="com.oculus.permission.USE_ANCHOR_API" />` |
| hand tracking ... needs `HAND_TRACKING` / `oculus.software.handtracking` | both entries (quest-native-xr) |
| the app does not run on mcx::App ... / own exit / OpenXR path in src/ | rebuild it on the template's loop (`#include <mcx_xr.h>`, `mcx::App`); close with `app.Quit()` |
| `<lib>: unresolved ...` | the code calls something no packed or system library has: link it or remove the call |
| no android:icon | `res/drawable/icon.png` + `android:icon="@drawable/icon"` |
| environment depth runs on Quest 3 / 3S only | supportedDevices `quest3\|quest3s` |
| no com.oculus.supportedDevices | `<meta-data android:name="com.oculus.supportedDevices" android:value="quest2\|questpro\|quest3\|quest3s" />` |

After the static checks, `mcx-app verify` reads the app's last start from its log (the app logs its permission and its
hand trackers at each start). These lines are the hands state on the headset; they are not fixed in the code:

| Line | Cause / fix |
|---|---|
| `hands on the headset (last start): HAND_TRACKING granted, hand trackers ready` | nothing to fix: the hands work |
| `PROBLEM: ... HAND_TRACKING is not granted` | the installed copy lacks the grant (it is install-time): the user uninstalls the app in the Library, then `mcx-app install` again; quest-native-hands-first |
| `PROBLEM: ... HAND_TRACKING granted but no hand trackers` | `mcx-app logs <dir>`: the `hands:` lines and `xrCreateHandTrackerEXT failed: N`; then restart the app |
| `hands on the headset: no log of ...` / `... has no permission line` | the app has not started since the last build: launch it (`mcx-app launch`), or the build predates this check: build, install and launch again |
| `hint: the menu gesture needs XR_FB_hand_tracking_aim ...` | this device gives no hand aim: the app menu opens with the left controller only; the headset's system menu still works |

## Install (`mcx-app install`)

The `RESULT:` and `hint:` lines say what happened; the full table of codes is in quest-native-apps
`references/install.md`. Most common: `failure (3)` the user cancelled (ask before trying again); `failure (5)`
another signing key or installer (the user uninstalls the old copy); `VERSION_DOWNGRADE` (raise `android:versionCode`).

## Launch and install requests

- `RESULT: requested launch of ...` / `requested install of ...`: normal: MetaCodex waits for the user's tap. Do not repeat
  the command and never try another route; `mcx-app status <dir>`: `pending` (not tapped yet: remind the user once),
  `launched`, `declined` (the user said no: ask what to change), `installing`, `failed: ...` (the reason follows).

## The app runs but ...

| Symptom (from the user) | Look for in `mcx-app logs` | Usual cause / fix |
|---|---|---|
| black, nothing at all, back to Home | `OpenXR init failed` / `... failed: -N` / no log | manifest NativeActivity block or OpenXR permissions changed: compare with the example; `android.app.lib_name` must match the library |
| black where the room should show | `passthrough: XR_FB_passthrough not enabled` or no `passthrough: started` | the PASSTHROUGH feature entry; `cfg.passthrough`; clear alpha 0 (`app.clearAlpha()`) |
| no depth effect | `depth: waiting for the spatial data permission` / `provider not created` | the user denied spatial data: Settings > Apps > <Label> > Permissions; USE_SCENE in the manifest |
| room empty | `scene: 0 objects` | no Space Setup done in the headset, or permission missing |
| hands or controllers do nothing | `session state 5` never reached (FOCUSED) | input works only with focus: the system menu is open, or the app reads input outside `f.focused` |
| hands do nothing, the HUD shows `-` for both hands | the `hands on the headset` lines of `mcx-app verify` | HAND_TRACKING not granted (reinstall) or the hand is out of view; quest-native-hands-first, "Pinch does not work" |
| a pinch does nothing, the HUD shows `PINCH` or `palm` | the app's own log lines of its toggle | `palm`: the palm faces the user, so no pinch counts (hands-first, rule 4); otherwise the toggle reads `pinching` on every frame instead of `pinchStarted` (hands-first, rule 6) |
| jerky, hot | | frame budget: quest-native-recipes recipe 5 |
| a line `extension X: not offered` | | the manifest lacks its entry, or the device has no such hardware (face/eye tracking on a 3S) |
| "<App> isn't responding" (Close app / Wait) after Back or in the background | | the app was built with mcx_xr before the fix (it kept waiting for frames after Back): rebuild it (`mcx-app build` + `install`); an app with its own OpenXR loop: rebuild it on `mcx::App` (`mcx-app verify` says so) |
| "the menu does not open" | no `menu opened` in `mcx-app logs` | the menu gesture is the left hand's, palm towards your face, then pinch (or the left controller's button); without the hand aim extension the log says `menu: the menu gesture needs XR_FB_hand_tracking_aim`, and then only the controller opens it; an app not on `mcx::App` has no menu at all (rebuild it). The headset's own system menu always works |
| "the menu opens, but a pinch does not pick an item" | `menu: <item>` appears only when a pinch picks one | a pinch of either hand picks the item the pointing hand points at (the pinching hand points); `palm`: no pinch counts; the app predates this fix (its log has no `permission` line): rebuild and install it |
| "there is no Back to MetaCodex" | `launched from outside MetaCodex` / `reopened from outside MetaCodex` | opened from the Library (or reopened from there): only Quit is offered by design; start it with `mcx-app launch` |
| "Back did nothing" / landed in Home | `back: ... could not be started` | MetaCodex not installed; the user opens MetaCodex from the dock (an app cannot bring MetaCodex forward after a crash: the overlay permission that allows it is prohibited by Meta) |
| a report with no picture | `report: the picture could not be read` | the attachment list has only the log; ask for a description |

Session states in the log: 1 idle, 2 ready, 3 synchronized, 4 visible, 5 focused, 6 stopping, 8 exiting.
