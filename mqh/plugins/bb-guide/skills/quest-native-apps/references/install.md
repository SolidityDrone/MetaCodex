# Installing an app: path B (Android's installer, started by MetaCodex)


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

