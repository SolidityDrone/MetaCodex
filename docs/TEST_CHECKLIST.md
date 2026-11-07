# Headset test checklist (MetaCodex 0.3, 2026-11-07)

Tick each line on the Quest. "→" is what should happen. Items marked **(MQH)** need MQH reachable at
127.0.0.1:38990 from the headset: until MQH runs on the headset (roadmap step 1-2), run it in WSL
(`tools/mqh-dev.sh start`) and forward the port: `adb reverse tcp:38990 tcp:38990` (works over Wi-Fi adb).
With that setup, threads run on the PC, not on the headset; the panels and flows are what you test.

Helpers on the PC: `tools/qterm` (control port), `adb logcat -s Unity`, `files/session_log.txt` on the device.

## 0. Install and first start

- [ ] `adb install -r unity/Builds/MetaCodex.apk` → installs (version 0.3.x)
- [ ] Start from the Library → passthrough, no boundary prompt (boundaryless), terminal + dock in front
- [ ] First start installs the Linux userland (progress in the status line) → a shell prompt in the terminal
- [ ] Frame rate: status line shows about 72 fps with the shell idle
- [ ] `mcx-ui status` inside the terminal → host=Ready, fps, terminals=1

## 1. Terminal and Linux

- [ ] Type with the system keyboard (`⌨` on the frame) → text appears; Enter runs
- [ ] Bluetooth keyboard (if paired) → typing, arrows, Ctrl-C work
- [ ] Key bar: Esc, Tab, Ctrl, arrows, ⌨, paste → each does its job
- [ ] `vim`, `less`, `htop` (or `top`) draw correctly; colours; resize-free
- [ ] `+` on the frame → a second terminal opens beside; tapping a frame focuses it (the key bar follows)
- [ ] `✕` closes a terminal; TERMS panel lists terminals; FOCUS / CLOSE work
- [ ] `⎘` on the frame pastes the clipboard into that terminal
- [ ] `ls /mnt/quest` shows `plugins/`, `scripts/`, `control.token`
- [ ] `ls ~/.claude/skills` shows quest-gesture-plugins, quest-scripting, metacodex-context, Unity and Meta skills (46)

## 2. Dock, panels, hands

- [ ] Dock: two rows (NEW TERMS FILES GIT AGENTS THREADS WEB / SCRIPTS GESTURE VAULT SETUP KEYS SELECT CENTER)
- [ ] Each dock button opens/closes its panel; new panels open where you look, beside the others, not overlapping
- [ ] Open many panels (10+) → when no gap is left within a head turn, extra panels cascade in front (closer, higher, a bit to the side), never exactly on another
- [ ] Ray + pinch clicks buttons; poke with the index fingertip clicks too
- [ ] Pinch a panel header and move → panel follows; release → stays (fixed layout)
- [ ] Close the hand (fist) while pointing at a panel → panel moves with the hand
- [ ] SELECT, then pinch-drag on a panel → a dashed rectangle; toast "Selected … cm"
- [ ] CENTER (⟲) → terminal and dock in front of you again
- [ ] Sounds: click, open, close, toast blips (if they annoy you: Settings > LOOK > Sounds)

## 3. Layouts

- [ ] Settings > DISPLAY > LAYOUT FLOAT → panels arrange on a circle around you, side panels bend, terminal flat
- [ ] Walk a few steps → the circle follows horizontally; nodding/looking down does not move panels up/down
- [ ] Fist in empty space + swipe sideways → the carousel spins and slows down smoothly
- [ ] LAYOUT FIXED → panels stay world-locked, flat
- [ ] SAVE LAYOUT… "desk", move/close panels, turn around, LOAD NEXT LAYOUT → panels come back around you

## 4. Settings

- [ ] GESTURES: click mode PINCH / POKE / BOTH changes how buttons react
- [ ] GESTURES: move panels by PINCH HEADER / CLOSED HAND / BOTH
- [ ] GESTURES: POINT RAY ALWAYS / POINTING POSE (ray only when you point)
- [ ] GESTURES: PINCH AT slider, clap on/off, clap distance, "Custom gestures from plugins" on/off
- [ ] DISPLAY: distance, carousel radius, UI scale, bend toggle
- [ ] LOOK: THEME presets COMMAND / STEALTH / ALERT / SOLAR / CLEAN change colours, glow, opacity, motion
- [ ] LOOK: ACCENT cycles through 9 colours; GLOW, PANEL OPACITY, MOTION sliders; Scanlines, Grid, Sounds, Volume
- [ ] After a THEME or ACCENT change (panels rebuild), the ray and pinch keep working right away (no stuck input)
- [ ] Settings survive an app restart

## 5. Files, Git, Browser

- [ ] FILES: /root, QUEST, / ; tap folders to open; CD HERE / VIEW / EDIT / PATH ⇥ act in the focused terminal
- [ ] GIT: SCAN finds repos under /root (clone one first: `git clone https://github.com/SolidityDrone/MetaCodex /root/MetaCodex`)
- [ ] GIT: branch, ahead/behind, changes, branches, metro history; FETCH, PULL, COMMIT… (keyboard), CHECKOUT (tap a branch)
- [ ] WEB: type a URL → GO opens the Horizon browser; bookmarks add/remove; LOCAL SERVERS lists running ports
- [ ] WEB: start `python3 -m http.server 8000` in a terminal → SCAN shows :8000 → opens in the browser
- [ ] WEB: HANDS ⚙ / APP ⚙ / FILES APP open the Horizon system pages

## 6. Lua scripts

- [ ] SCRIPTS: + EXAMPLE → `double-clap` script; double clap → TERMINALS panel toggles
- [ ] Edit `/mnt/quest/scripts/double-clap.lua` in the terminal (nano) → saved changes apply within a second
- [ ] A broken script (syntax error) → ERROR in the list, toast; others keep working
- [ ] DISABLE / ENABLE / DELETE a script; state survives a restart
- [ ] `mcx-ui script list`, `mcx-ui script eval 'return 1+1'` → 2

## 7. Gestures studio and plugins

- [ ] GESTURE panel > GESTURES: built-ins + `thumbs-up thumbs-down victory horns call-me` (core-poses) + `swipe-left/right/up` (swipes)
- [ ] LIVE tab: finger bars move with your fingers (1 = straight); palm / thumb direction text; active gestures listed
- [ ] Make a thumbs up → LIVE shows `thumbs-up` (and `grab`); GESTURES list marks it ● ACTIVE
- [ ] Thumbs down, victory (✌), horns (🤘), call-me (🤙) → each recognised (LIVE / toast via a binding)
- [ ] Swipe an open hand fast left / right → swipe-left / swipe-right in LIVE's recent events
- [ ] Detector cost (LIVE, right column) stays well under 1 ms
- [ ] TEST ▶ on any gesture → simulated event (toast "Simulated …")
- [ ] BIND: pick `victory`, action "Open vault", + ADD BINDING → making ✌ opens VAULT; ON/OFF and DELETE work
- [ ] RECORD: name "finger-gun", RIGHT, hold a finger gun, RECORD IN 3 s → "saved" + summary; the pose then fires; it appears under plugin `my-poses`
- [ ] + NEW PLUGIN… "test-plugin" → folder in /mnt/quest/plugins; both palms up toggles THREADS
- [ ] PLUGINS tab: enable/disable/reload/delete; detail shows poses, detectors, log
- [ ] Ask an agent on the headset (claude or opencode in a terminal): "make me a gesture plugin: when I do a thumbs up with my right hand open git; push my palm away to close it" → plugin appears and works (the same request passed in the Editor test)
- [ ] `mcx-ui plugin list`, `mcx-ui gesture list`, `mcx-ui gesture bind clap toggle:files`, `mcx-ui action open:git`

## 8. Agents and sign-in (MQH)

- [ ] AGENTS lists providers (Claude Code, Codex, Pi, Cursor, opencode if installed) with READY / SIGN IN / INSTALL
- [ ] Select Claude Code → SIGN IN ▶ → a new terminal runs `claude auth login`
- [ ] The terminal prints a link → toast "Link in …" → `⇗` on that terminal (or OPEN LINK) opens it in the Horizon browser
- [ ] Sign in in the browser; the redirect back to localhost completes the login (or paste the code with ⎘) → AGENTS ⟳ shows READY + account
- [ ] Same for opencode (`opencode auth login`) and Codex (`codex login --device-auth`: device code flow)
- [ ] INSTALL / UPDATE on a missing provider → MQH installs it (or run `mcx-setup` in a terminal)
- [ ] ENABLE / DISABLE toggles a provider in MQH

## 9. Threads (MQH)

- [ ] THREADS: + PROJECT… `/root/MetaCodex` (or another folder) → project listed
- [ ] + NEW THREAD…: providers, then models for the chosen provider; PERMISSIONS EDITS/AUTO/FULL; WORKSPACE CHECKOUT/WORKTREE
- [ ] First message "list the files and summarise the README" → START → THREAD opens, timeline fills (you, agent, commands, tools)
- [ ] Composer: type with the keyboard → SEND → message appears; agent answers
- [ ] With EDITS, a command needing approval → ALLOW ONCE / ALLOW SESSION / DENY buttons appear and work
- [ ] ■ STOP stops a running turn
- [ ] FILES / GIT / TERMINAL jump to the thread's workspace (worktree path for WORKTREE threads)
- [ ] THREADS list shows status (running / error / NEEDS YOU) and refreshes
- [ ] ▲ ▼ scroll the timeline of a long thread

## 10. Vault and secrets

- [ ] VAULT shows `key: Android Keystore · tee` (or strongbox) and `mqh.api-token` (policy ALLOW)
- [ ] + ADD SECRET… `OPENAI_API_KEY` → the secure keyboard hides the value → "sealed in the vault"
- [ ] In a terminal: `mcx-secret get OPENAI_API_KEY` → an approval dialog in VR → ALLOW ONCE → the value prints
- [ ] Again → DENY → the command fails with "denied by the user"; the access log lists both
- [ ] Policy ALLOW → no dialog; DENY → always refused; ONCE → first request only (RE-ARM in VAULT resets)
- [ ] `mcx-secret env OPENAI_API_KEY -- sh -c 'echo ${#OPENAI_API_KEY}'` → prints the length, not the key
- [ ] `→ MQH ENV` on OPENAI_API_KEY (MQH) → toast "set for agents"; an opencode/OpenAI thread can use it
- [ ] `grep -r sk- /root /mnt/quest 2>/dev/null` → the key is in no file of the Linux
- [ ] Restart the app → secrets still there; `mcx-secret list` shows names and policies only

## 11. Daemon settings (MQH)

- [ ] Settings > DAEMON: ⟳ CONNECT → ● online, MQH version, plugin list; tap a plugin → ENABLE/DISABLE
- [ ] Sync on: change the accent → `quest.settings` updated in MQH; change it through the MQH API → app follows within 5 s

## 12. Performance and stability

- [ ] All panels open + float layout + 2 terminals → still about 72 fps (status line); no stutter when gestures run
- [ ] A long `apt install` / `npm install` in a terminal → app stays smooth (run it with `nice -n 10`)
- [ ] Take the headset off and on, reopen the app → terminals and panels come back
