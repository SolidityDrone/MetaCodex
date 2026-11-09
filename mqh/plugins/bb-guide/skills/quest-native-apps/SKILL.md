---
name: quest-native-apps
description: Build small native Meta Quest apps (C++, OpenXR, OpenGL ES; no Unity, no Gradle, no PC) inside MetaCodex's Linux and install them on this headset with Android's own installer while the user confirms in VR (mcx-app; "path B"). Use when the user wants a separate app on the headset (its own icon in the Library, full screen in VR) - a tool, toy, demo or feature-flag app - and to build, install, launch, read the logs of, or fix the install of such an app. For behaviour inside MetaCodex itself use quest-scripting, quest-gesture-plugins or quest-views instead (they apply live, nothing to install).
---

# Native Quest apps from MetaCodex

A native app here is one APK: C++ compiled with the Android NDK, OpenXR for the headset (Khronos loader), OpenGL ES 3
for drawing, `NativeActivity` (no Java). You build it in this Linux and MetaCodex installs it with Android's
PackageInstaller; **Android asks the user, never you.** One command does each step: `mcx-app`.

## What is installed (MetaCodex runs `mcx-setup quest` once, by itself, at first use)

| What | Where |
|---|---|
| Android NDK r27d (clang 18, arm64 host build) | `/opt/android-ndk` (`ANDROID_NDK_HOME`) |
| android.jar, Android SDK Platform 34 | `/opt/android-sdk/platforms/android-34/android.jar` |
| OpenXR 1.1.65: headers + Khronos Android loader | `/opt/openxr/include/openxr/`, `/opt/openxr/lib/arm64-v8a/libopenxr_loader.so` |
| aapt2, zipalign, apksigner, keytool (JDK) | Debian packages, on PATH |
| the template (Hello Native: three turning cubes) | `/opt/metacodex/quest-native/template` |
| one signing key for every app built here | `~/.android/metacodex-debug.keystore` (made by the first build; **never delete**) |
| your apps | `/root/apps/<name>` |

Always start with `mcx-app doctor`. `WAIT` = the first-use setup is still running (log
`/root/.metacodex/first-use.log`): wait for it, never start a second one. `MISSING` = run `nice -n 10 mcx-setup quest`
(about 400 MB to download, 2.5 GB on disk, a few minutes on Wi-Fi; safe to rerun).

## The loop: exact commands

1. `mcx-app doctor`
2. `mcx-app new /root/apps/<name> --label "<Name>"` (package `com.metacodex.user.<name>`; `--package` to choose)
3. Edit `src/main.cpp`; add more `.cpp` / `.c` files in `src/` if needed (all are compiled). Keep the OpenXR
   session loop of the template and change the scene (`BuildScene`, `DrawEye`). Optional: `res/` (icons, strings),
   `assets/` (data files packed into the APK).
4. `mcx-app build /root/apps/<name>` → `build/<name>.apk`. Compiler errors print here: fix, build again.
5. `mcx-app install /root/apps/<name>` → path B (below). It prints `>>> ASK THE USER: ...` lines: tell the user that,
   in one short sentence, and let the command wait (up to 5 minutes; it polls by itself). It ends with `RESULT: ...`.
6. `mcx-app launch /root/apps/<name>` → the app opens in the headset and MetaCodex goes to the background.
7. When the user is back: `mcx-app logs /root/apps/<name>` (everything the app logged), then ask what they saw.

`mcx-app run <dir>` = steps 4 + 5 + 6. `mcx-app info <dir|package>` = installed version and installer of record.
Before installing, show the user what the app will do and get their OK (they will see Android's dialog anyway).

## Path B: how the install works

- `mcx-app install` → `mcx-ui system install-apk <apk>`: MetaCodex opens a PackageInstaller session, copies the
  APK in (off its render thread) and commits it. Android then decides and asks the user. The APK may be anywhere in
  this Linux (`/root/...`, `/mnt/quest/...`): MetaCodex reads it from its own storage; paths with `/../` are refused.
- **Once per headset**: Android needs the user's **"Install unknown apps"** switch for MetaCodex. While it is off,
  `install-apk` opens that settings page in the headset; the user turns on **"Allow from this source"** and comes
  back; `mcx-app` waits for it (`mcx-ui system can-install`: `yes` | `no: ...` | `unavailable: ...`, opens nothing).
- **Every new app**: Android shows "Do you want to install this app?" and the user taps **Install**.
- **Updates** (same package, same signing key, `versionCode` not lower): when MetaCodex installed the app, Android
  lets the update through **without a dialog** (`success: ... (no prompt: an update of an app MetaCodex installed)`;
  Android 12+, the app must target API 31 or newer on Android 14: the template targets 32). Apps first installed by
  adb or another installer get the dialog again ("Update").
- States, as `mcx-ui system install-status` shows them (only the latest install counts):
  `installing <apk> (session N)` → `waiting for the user to confirm in the headset (session N)` →
  `success: <package>` or `failure (<code>): <message>`.
- **Store builds of MetaCodex cannot install apps**: Meta prohibits `REQUEST_INSTALL_PACKAGES` on the Horizon Store,
  so those builds leave it out and `can-install` answers `unavailable: ...`. Tell the user; never look for a
  workaround. Path B exists in sideloaded MetaCodex builds (developer mode or SideQuest).

| RESULT | Meaning | What to do |
|---|---|---|
| `failure (3)` | the user cancelled or closed Android's dialog | ask; install again only if they want |
| `failure (5)` ...`UPDATE_INCOMPATIBLE` / signatures | an installed copy has another signing key (adb, another machine) | the user uninstalls it (Library > the app > ... > Uninstall), or use a new package name |
| `failure (7)` ...`VERSION_DOWNGRADE` | lower `versionCode` than the installed one | raise `android:versionCode` |
| `failure (7)` ...`NO_MATCHING_ABIS` / `OLDER_SDK` | no `lib/arm64-v8a`, or `minSdkVersion` above 34 | keep the template's ABI and SDK lines |
| `failure (4)` ...`PARSE` | broken or unsigned APK | `mcx-app build` again |
| `failure (6)` | the headset is full | `mcx-ui system storage` |
| switch still off (exit 2) | the user did not turn on "Allow from this source" | ask once more |
| no answer in 5 minutes (exit 2) | the dialog is still open, maybe behind a window | ask the user to look for it |
| `unavailable: ...` (exit 3) | Store build of MetaCodex | not possible in this build |

## Best practices

- **Package names**: `com.metacodex.user.<name>`, lower case, one per app. Never `com.pnncl.metacodex` (MetaCodex),
  `com.oculus.*`, `com.meta.*`, `android.*`.
- **Signing**: every app is signed with `~/.android/metacodex-debug.keystore`; Android accepts an update only with the
  same key. Never delete it, never copy it into a project or a commit.
- **Versions**: the same `versionCode` reinstalls; raise it (and `versionName`) for a release; never lower it.
- **Manifest** (Meta's native manifest page; the template already has it): `minSdkVersion` and `targetSdkVersion` 32,
  `screenOrientation="landscape"`, theme `@android:style/Theme.Black.NoTitleBar.Fullscreen`, the template's
  `configChanges`, `<uses-feature android:name="android.hardware.vr.headtracking" android:required="true"
  android:version="1" />`, categories `com.oculus.intent.category.VR` + `android.intent.category.LAUNCHER`,
  `com.oculus.supportedDevices` = `quest3|quest3s|quest2|questpro`, `exported="true"`, `hasCode="false"`. No
  `noHistory`. For a Store submission of the app itself, Meta's current target rule (Android 14) applies.
- **Permissions**: only what the app uses. `INTERNET` is in the template for `mcx-app logs` only. Never a permission
  from Meta's prohibited list.
  - Hand tracking: `<uses-permission android:name="com.oculus.permission.HAND_TRACKING" />`,
    `<uses-feature android:name="oculus.software.handtracking" />`, extension `XR_EXT_hand_tracking`.
  - Passthrough: `<uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />`,
    extension `XR_FB_passthrough`; submit the passthrough layer (`XrCompositionLayerPassthroughFB`, flag
    `XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT`) **before** the projection layer, clear to alpha 0 where
    the room should show, keep `environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE` (Meta requires it).
- **OpenXR**: list the extensions first (`xrEnumerateInstanceExtensionProperties`) and enable only those present
  (instance creation fails otherwise). Keep the template's loop: `xrPollEvent` every frame; begin the session on
  `READY`, end it on `STOPPING`, quit on `EXITING` / `LOSS_PENDING`; draw only when `shouldRender`. The swapchains are
  sRGB (`GL_SRGB8_ALPHA8`): write linear colours.
- **Frame rate**: MetaCodex keeps rendering while you build: `mcx-app build` runs at nice 10 on cores 0-2; never
  more than 2 parallel compile jobs.
- **Logs**: `LOG("x = %d", x)` (printf style) goes to logcat and to `mcx-app logs`; a crash ends the log, so its
  last line shows how far the app got. You cannot read logcat from here.
- **Small steps**: one feature per app, one change per install, ask the user after each launch.
- While the app runs, MetaCodex (and this shell) may be paused. When the user quits the app (Meta button > Quit) they
  land in Home and reopen MetaCodex from the Library.
- **Uninstall**: the user does it in the Library (the app > ... > Uninstall). MetaCodex has no uninstall command.

## Do not

- Do not install with adb (`adb connect 127.0.0.1:5555`): it needs developer mode, a PC to switch it on after every
  reboot and the user's approval for each new key; every retry shows Android a new prompt. Only when the user asks.
- No `pm install` / `cmd package` (not allowed for apps); never write into other apps' folders.
- Never ask for a MetaCodex rebuild or update to install an app: none is needed.
