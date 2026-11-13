# Install MetaCodex on a Quest 3 / 3S

You need: the headset in developer mode, a USB-C cable, and Android's `adb` (platform-tools) on your computer.

1. Plug the headset into the computer. In the headset, accept "Allow USB debugging".
2. Run the script in this folder:
   - Windows: `install-metacodex.bat`
   - macOS / Linux: `./install-metacodex.sh`
   It installs the app and grants its permissions (microphone, spatial data, headset camera), so the app asks nothing.
3. In the headset: Library > Unknown sources > MetaCodex.

At the first start the app unpacks its Linux and its voice models (about a minute, offline). Everything else works
at once: TALK, the orb, the windows, MQH. Coding agents (OpenCode, Codex, Claude Code) install from CONTROL > AGENTS >
GET when you want one; the tools for native Quest apps install the first time you make one.

Without the script: `adb install -r -g MetaCodex.apk` (the app then asks for its permissions at the first start).
