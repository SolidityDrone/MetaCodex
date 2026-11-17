---
name: quest-panel-apps
description: Make a 2D app for Meta Quest 3 / 3S (Horizon OS) on this headset - a plain Android app in Java that the headset shows as a window (a panel) beside the user's other windows, with its own Library icon - from a one-line request to an installed app, built, checked, installed and launched by MetaCodex itself (mcx-app; no PC, no Gradle, no Android Studio). Use whenever the work happens in a Native app project of kind 2D panel, or the user wants a windowed, flat or "normal" app, a utility or tool (zip or unzip files, a file converter, notes, a timer, a calculator, a list, a form, a viewer, a small client of a web API) that runs on its own outside MetaCodex. For 3D or mixed-reality apps use quest-native-apps; for something that lives inside MetaCodex use quest-plugins.
---

# 2D panel apps: from "I want an app that ..." to a window in the Library

> **Always.** Keep `Mcx.java` (the template's) and its three calls: `Mcx.start(this)` first in `onCreate`,
> `Mcx.topBar(this, Mcx.label())` at the top of the window (BACK TO METACODEX and REPORT appear there when MetaCodex
> launched the app), `Mcx.log(...)` for every user action, every result and every error (`mcx-app logs` reads it;
> you cannot read logcat from here; a crash writes its stack trace there too).
>
> **Always ask before taking the user out of MetaCodex.** Check, build and verify freely. To launch the app, or for a
> first install, use only `mcx-app launch` / `mcx-app install` / `mcx-app run`: MetaCodex shows the user LAUNCH /
> INSTALL / LATER and the command returns at once with `RESULT: requested ...`. Then say one short sentence ("it's
> ready: tap LAUNCH in MetaCodex; it opens as a window and MetaCodex waits in the background") and continue or wait;
> `mcx-app status <dir>` tells you pending / launched / declined. This holds in every approval mode. Never adb,
> `am start`, `pm` or any other route.

## What a 2D panel app is (Horizon OS, checked with Meta's docs on 2026-11-17)

- A normal Android app (an Activity with Android views), not an OpenXR app. The headset shows it as a **window**: the
  user moves it by its bar, resizes it from its corners, and can keep it open next to other windows. Input arrives as
  ordinary touch / click events: the hand's pinch with its ray, a controller's trigger, and on some devices Look and
  Pinch (gaze to aim, pinch to click; the system draws the hover and sends standard touch events).
- **It runs outside MetaCodex.** Opening it puts MetaCodex (an immersive app) in the background: the user sees the
  home environment with the app's window. BACK TO METACODEX in the app's top row (from `Mcx`) brings MetaCodex back and
  closes the app; REPORT also sends you a picture of the window and its last 60 log lines. The headset's own menu and
  app library always work too.
- Built here with `javac` (Java 17 language), D8 (Java bytecode to Android's dex) and aapt2, signed with the same key
  as every app MetaCodex builds. No Gradle, no AndroidX, no Kotlin, no Jetpack Compose, no Google Play Services (the
  headset has none). Plain Java libraries can go in `libs/*.jar`.
- The manifest rules (the template follows them; `mcx-app verify` checks them): `android.intent.action.MAIN` +
  `android.intent.category.LAUNCHER` and no VR category; a `<layout android:defaultWidth="1024dp"
  android:defaultHeight="640dp" android:minWidth="360dp" android:minHeight="225dp" />` in the activity; resizable;
  `minSdkVersion` 32, `targetSdkVersion` 34; `android.hardware.vr.headtracking` not required; `com.oculus.supportedDevices`;
  no eye tracking permission; no permission from Meta's prohibited list.

## Which kind of app?

Decide yourself from the request; ask only when it truly could be either.

| The user wants | Make | Why |
|---|---|---|
| a tool with buttons, lists, text, files, forms, settings; something they keep beside other windows | **2D panel app** (this skill) | fastest to build and to use; flat UI is what Android views do well |
| 3D objects, the room, passthrough effects, hands as input, a game in the room | **immersive app** (quest-native-apps; a Native app project of kind immersive) | needs OpenXR and a 3D scene |
| something that only lives inside MetaCodex (a window in its room, a menu tile, a gesture, a reaction) | **app plugin** (quest-plugins) | runs at once in MetaCodex, no install, no leaving MetaCodex |
| a website, or a page several devices open | a **web app** served from this Linux, opened in MetaCodex's WEB window (a Default project) | no install; but no file picker of the headset, and it stops with the Linux |

A 2D app's project is fixed to that kind: if the request needs 3D, say so and suggest a new Native app project of kind
immersive (THREADS > + PROJECT > NATIVE APP > IMMERSIVE).

## The flow (do it in this order)

1. **Understand the request.** Write down, for yourself, what the window shows and every action the user can do; ask
   at most one short question, only about what you cannot decide. Read `references/design.md` before the first line of
   UI code.
2. **The folder.** Your thread's project folder (`/root/metacodex-projects/native/<name>`, made by MetaCodex's
   + PROJECT > NATIVE APP > 2D PANEL): `mcx-app new .` fills it with the 2D template and keeps its `.git`, `AGENTS.md`
   and `CLAUDE.md`. Package `com.metacodex.user.<name>`, label = a short title (`--label "Zip Tool"`). Never reuse
   another app's package; never `com.pnncl.metacodex`, `com.oculus.*`, `com.meta.*`.
3. **`mcx-app doctor`.** `WAIT`: the first-use setup is running. `MISSING javac` or `MISSING ... android.jar`:
   `nice -n 10 mcx-setup quest`. `note no D8 yet`: fine, the first build fetches it (21 MB, once).
4. **Start from the right base** (`mcx-app new` copies it, sets package and label, moves the Java files into the
   package's folder):

   | The app | Start with |
   |---|---|
   | works with the user's files (pick, convert, pack, view, save) | `mcx-app new . --example zipper --label "<Label>"`: file picker, a worker thread with progress, saving to a file the user chooses; replace the zipping |
   | anything else | `mcx-app new . --label "<Label>"` (the template: a window with a counter card) |

5. **Write it** in `src/<package path>/`: `MainActivity.java` (replace the card; keep `Mcx.start`, `Mcx.topBar`,
   `Mcx.log`), more classes next to it as needed, resources in `res/` (`R` is generated in the manifest's package).
   Build the views in code with the `Mcx` helpers (`Mcx.text`, `Mcx.button`, `Mcx.round`, `Mcx.dp`, the colours) or
   from XML layouts in `res/layout/` (`setContentView(R.layout.main)`); slow work (files, network, big loops) on a
   worker thread, never the UI thread (`references/android-apis.md`). Give it its own icon:
   `res/drawable/icon.png`, 192 x 192, a simple glyph on a dark rounded square.
6. **`mcx-app check`** (javac only): fix every `<file>.java:<line>: error: <message>` and check again.
7. **`mcx-app build`**: `RESULT: APK ...`. A changed source takes about 1.5 s when the builder's JVM is warm (it stays
   up 5 minutes after a build), a few seconds more when it starts; nothing changed 0.1 s.
8. **`mcx-app verify`**: every `PROBLEM:` is a manifest rule or a launcher class that is not in the APK: fix it and
   build again until `RESULT: verify ok`. `note:` lines are advice.
9. **Self-review** before the first install (`references/design.md`, the checklist): the user's first look must
   already be a finished, tidy window.
10. **Tell the user in one sentence what the app does**, then `mcx-app install`. A new app:
    `RESULT: requested install of ...` (MetaCodex offers INSTALL, then Android's dialog, then LAUNCH). An update of an app
    MetaCodex installed goes through silently (`RESULT: installed`).
11. **`mcx-app launch`**: `RESULT: requested launch of ...`. Say: "ready: tap LAUNCH in MetaCodex. The app opens as a
    window and MetaCodex waits in the background; Back to MetaCodex in the app's top row brings you here, Report also
    sends me a picture and its log." Then what to try.
12. **When they are back**: a Report arrives as a draft in this thread ("Feedback on <Label> <version>:") with the
    window's picture and the log; read both, fix. Without a report: `mcx-app logs` and ask what they saw. Then 6-11
    again (`mcx-app run` = build + install + launch request). Raise `android:versionCode` for every version you hand
    over.

**Done** = `verify ok`, installed, launched, the log shows the actions the user tried with no `CRASH`, the user saw it
work, and you told them the app's name in the Library and every action it has.

## Common requests

| Request | Base | Notes |
|---|---|---|
| "an app to zip / unzip files" | zipper | unzip: `ZipInputStream` + `ACTION_OPEN_DOCUMENT_TREE` for the target folder (`references/android-apis.md`) |
| a file converter or viewer (text, CSV, JSON, images) | zipper | open with the picker; images: `BitmapFactory` + `ImageView`; save with `ACTION_CREATE_DOCUMENT` |
| notes, a to-do list, a journal | template | keep the data in `getFilesDir()` (JSON with `org.json`) or `SharedPreferences`; an `EditText` opens the system keyboard |
| a timer, stopwatch, countdown | template | `Handler.postDelayed` ticks; big digits (64sp+); keep running with the window in the background only while the app lives |
| a calculator, unit converter | template | a grid of 60dp buttons that wraps; the result line large |
| a small client of a web API | template | INTERNET is in the template's manifest; `HttpURLConnection` on a worker thread; show loading and errors in the window |

## Rules

- Builds only through `mcx-app check|build` (they go to cores 0-2, off MetaCodex's frame loop); never `javac`, `java`,
  `d8`, `aapt2` or `apksigner` by hand.
- Java 17 language; the Android API of `android.jar` 34. javac checks `android.*` against Android 14 but `java.*`
  against Java 17: use only `java.*` classes and methods Android 14 has (java.io, java.nio.file, java.util,
  java.util.concurrent, java.util.zip, java.net, java.text, java.time). Avoid newer ones (e.g. `java.net.http`,
  virtual threads): they compile and then crash on the headset.
- No permission you do not use; never storage permissions (the file picker needs none); never eye tracking.
- Show the user what you will install before `mcx-app install`; one app change per install; ask after each launch.
- Uninstalling is the user's (Library > the app > ... > Uninstall).

## Commands

| Command | Does |
|---|---|
| `mcx-app doctor` | tools present (JDK, D8, android.jar, aapt2)? may MetaCodex install apps? |
| `mcx-app new <dir> [--panel \| --example zipper] [--package <id>] [--label <name>]` | a new 2D app (in a 2D panel project `--panel` is the default) |
| `mcx-app check [dir]` | javac only, no APK |
| `mcx-app build [dir]` | incremental build -> `<dir>/build/<dir name>.apk` |
| `mcx-app verify [dir]` | the manifest rules, the launcher class in `classes.dex`, permissions |
| `mcx-app install [dir\|apk]` | Android's installer, waits up to 5 min for the user |
| `mcx-app launch [dir\|package]` | asks the user to open the app (LAUNCH in MetaCodex); returns at once |
| `mcx-app run [dir]` | build + install (+ launch request) |
| `mcx-app status [dir\|package]` | the request: pending / launched / declined / installing / failed |
| `mcx-app logs [dir\|package] [-f]` | what the app logged (`Mcx.log`, crashes) |
| `mcx-app info [dir\|package]` | installed version, installer of record |

The window and design rules: `references/design.md`. Files, threads, network, storage, what Java and Android have
here: `references/android-apis.md`. Errors, verify problems, install failures, a crash: `references/debug.md`.
Install states and failure codes (shared with immersive apps):
`/opt/metacodex/quest-native/skills/quest-native-apps/references/install.md`.
