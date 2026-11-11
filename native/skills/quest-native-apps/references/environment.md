# What is installed, and where

MetaCodex runs `mcx-setup quest` by itself once per Linux install (log `/root/.metacodex/first-use.log`).

| What | Where |
|---|---|
| Android NDK r27d (clang 18, arm64 host build) | `/opt/android-ndk` (`ANDROID_NDK_HOME`) |
| android.jar, Android SDK Platform 34 | `/opt/android-sdk/platforms/android-34/android.jar` |
| OpenXR 1.1.65: Khronos headers (Meta's extensions included) + Android loader | `/opt/openxr/include/openxr/`, `/opt/openxr/lib/arm64-v8a/libopenxr_loader.so` |
| aapt2, zipalign, apksigner, keytool, openssl | Debian packages, on PATH |
| the native XR layer `mcx_xr.h` (+ `mcx_xr.cpp`, linked by `mcx-build`) | `/opt/metacodex/quest-native/xr` |
| examples (`mcx-app new --example <name>`) | `/opt/metacodex/quest-native/examples/nightvision` |
| the template (Hello Native: three turning cubes) | `/opt/metacodex/quest-native/template` |
| the module template, the Runner and the module header `mcx_module.h` | `/opt/metacodex/quest-native/module-template`, `/opt/metacodex/quest-native/runner` |
| the build pipeline / builder daemon | `mcx-build`, `mcx-buildd` (on PATH); shared cache `~/.cache/mcx-build` |
| the signing key of every app built here | `~/.android/metacodex-debug.keystore` (never delete) |
| your apps | `/root/apps/<name>` |

An app folder: `AndroidManifest.xml` (package, label, versionCode, permissions, features), `src/` (every `.cpp`,
`.cc`, `.c` is compiled), optional `res/` (e.g. `res/drawable/icon.png`), `assets/` (packed into the APK), and
`build.conf` (`KEY=value` lines: `CXXFLAGS=`, `CFLAGS=`, `LDLIBS=` extra flags, `OPT=-O2`, `PCH=0`,
`TYPE=app|module|runner`). Output: `build/<folder name>.apk`.

How a build runs: `mcx-app` builds in its own shell when that shell may use cores 0-2 (agents under MQH), otherwise
it hands the job to `mcx-buildd` (cores 0-2, one job at a time: `queued behind N job(s)`). Only changed sources
compile; aapt2 runs only when the manifest, `res/` or `assets/` changed; signing takes milliseconds.
`mcx-buildd status` shows the daemon, the running job and the CPU budget.
