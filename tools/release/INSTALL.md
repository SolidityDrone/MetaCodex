# Install MetaCodex on your headset

You need: the headset in developer mode, a USB-C cable, and Android's `adb` (platform-tools) on your computer.

1. Plug the headset into the computer. In the headset, accept "Allow USB debugging".
2. Run the script in this folder:
   - Windows: `install-metacodex.bat`
   - macOS / Linux: `./install-metacodex.sh`
   It installs the app and grants its permissions (microphone, spatial data, headset camera), so the app asks nothing.
3. In the headset: Library > Unknown sources > MetaCodex.

The first start is a one-time setup of about 20 seconds, offline: the app unpacks its Linux and its AI models, and
the Whisp gives a short guided tour meanwhile. Nothing is downloaded. Coding agents install from CONTROL > AGENTS > GET
when you want one (with your own key or sign-in); the tools for native headset apps install the first time you make
one.

Without the script: `adb install -r -g MetaCodex.apk` (the app then asks for its permissions at the first start).
