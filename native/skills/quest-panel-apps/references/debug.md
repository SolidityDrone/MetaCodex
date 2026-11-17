# Debugging 2D panel apps

What you can see: the output of `mcx-app` (`RESULT:`, `PROBLEM:`, `note:` lines), the app's log (`mcx-app logs <dir>
[-f]`: every `Mcx.log(...)` line while it runs, from its `starting <label> <version>` line; a crash adds `CRASH in
thread ...` and the stack trace), `mcx-app info`, `mcx-buildd status`. What you cannot: logcat, the screen. So log
every action and result, and ask the user what they see (or ask for a REPORT from the app's top row: it brings a
picture of the window and the last 60 log lines).

## Check and build

| Output | Meaning and fix |
|---|---|
| `<file>.java:<line>: error: cannot find symbol` | a typo, a missing `import`, or a class Android does not have (AndroidX, Kotlin, GMS): use the platform's (`references/android-apis.md`) |
| `error: package R does not exist` / `cannot find symbol ... R` | `R` is generated in the manifest's package: the Java file's `package` line must be the manifest's `package` (`mcx-app new` sets both), or `import <manifest package>.R;` |
| `error: class X is public, should be declared in a file named X.java` | one public class per file, named like the file |
| `RESULT: N error(s) in M file(s)` | fix them all, `mcx-app check` again before building |
| `aapt2 rejected AndroidManifest.xml or res/` | the lines above name the file and line: an XML error (an XML comment must not contain two hyphens in a row), an unknown attribute, a missing `@drawable/...` |
| `D8 failed` | a jar in `libs/` that is not plain Java bytecode (an Android library, Kotlin), or a broken class file |
| `no D8 (...): run mcx-setup d8` | the first 2D build fetches D8; without network it cannot: `mcx-setup d8` once online |
| `note: no warm JVM (...)` | the builder daemon could not keep a JVM (no daemon, low memory): the build starts its own JVMs, a few seconds slower; not an error |
| `stopped ...: MemAvailable N MB < M MB` (exit 4) | the headset is short of memory: tell the user, close windows or apps, build again. Never lower the guards |
| `paused: MetaCodex is in the background ...` | the builder keeps its CPU low while MetaCodex is not in front; it goes on when MetaCodex is back |

## Verify (`mcx-app verify`)

| `PROBLEM:` | Fix in AndroidManifest.xml (or the code) |
|---|---|
| no launcher activity | the activity needs `android:exported="true"` and an intent filter with `android.intent.action.MAIN` + `android.intent.category.LAUNCHER` |
| the manifest starts X, but classes.dex has no such class | `android:name=".MainActivity"` is relative to the manifest's `package`: the class must be `<package>.MainActivity` in `src/<package path>/MainActivity.java` |
| an immersive category (com.oculus.intent.category.VR / IMMERSIVE_HMD) | remove it: with it the headset starts the app as an immersive app with no window |
| NativeActivity or android:hasCode="false" | an immersive native manifest: start from `mcx-app new . --panel` |
| no `<layout android:defaultWidth=... android:defaultHeight=...>` | add the template's `<layout>` to the activity |
| android:resizeableActivity="false" | remove it (windows are resized by the user and the system) |
| minSdkVersion above 34 / targetSdkVersion below 32 | `<uses-sdk android:minSdkVersion="32" android:targetSdkVersion="34" />` |
| android.hardware.vr.headtracking is required | remove that `<uses-feature>` (a 2D app runs without head tracking) |
| eye tracking / prohibited permissions | remove them (Look and Pinch gives the app touch events; the prohibited ones fail the store) |
| the code uses the network ... no INTERNET | `<uses-permission android:name="android.permission.INTERNET" />` (`Mcx.log` needs it too) |
| no com.oculus.supportedDevices / no android:icon | the template's `<meta-data>` / `res/drawable/icon.png` + `android:icon="@drawable/icon"` |

## Install

Install states, failure codes and what to tell the user are the same as for immersive apps:
`/opt/metacodex/quest-native/skills/quest-native-apps/references/install.md`. The usual ones: `failure (3)` the user
cancelled Android's dialog; `failure (7) ... VERSION_DOWNGRADE` raise `android:versionCode`; `failure (5) ...
UPDATE_INCOMPATIBLE` an installed copy has another signing key: the user uninstalls it first.

## At run time

| The user sees / the log says | Likely cause | Fix |
|---|---|---|
| the window opens and closes at once; the log ends with `CRASH in thread main: ...` | an exception in `onCreate` (the trace names the line) | fix it; never catch-and-ignore: log and show the error |
| no log lines at all | the app was opened from the Library before MetaCodex listened, or INTERNET is missing | open it with `mcx-app launch`; `mcx-app verify` |
| `NetworkOnMainThreadException` in the trace | network on the UI thread | move it to the worker (`references/android-apis.md`) |
| "isn't responding" / the window freezes | long work on the UI thread | the worker thread, progress posts at most ten a second |
| `NoSuchMethodError` / `NoClassDefFoundError` for a `java.*` class | a Java 17 API Android 14 does not have | use the Android one (`references/android-apis.md`, Java and Android here) |
| `SecurityException: Permission Denial` opening a URI | a URI kept from an earlier run without `takePersistableUriPermission` | pick it again, or persist the permission when it is picked |
| "This headset has no file picker" in the window | `ActivityNotFoundException` for the picker intent | check the intent (`ACTION_OPEN_DOCUMENT` + `CATEGORY_OPENABLE` + a type) |
| text cut off or buttons overlapping when the window is small | a fixed-size layout | `references/design.md`: scroll, wrap, test at 360dp |
| BACK TO METACODEX is missing | the app was not launched by MetaCodex (opened from the Library) | expected: the headset's menu works; launch it with `mcx-app launch` |
