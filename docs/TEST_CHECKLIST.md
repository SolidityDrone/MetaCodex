# Headset test checklist (MetaCodex 0.7, 2026-11-08)

Tick each line on the Quest. "→" is what should happen. Items marked **(MQH)** need MQH reachable at
127.0.0.1:38990 from the headset: until MQH runs on the headset (roadmap step 1-2), run it in WSL
(`tools/mqh-dev.sh start`) and forward the port: `adb reverse tcp:38990 tcp:38990` (works over Wi-Fi adb).
With that setup, threads run on the PC, not on the headset; the panels and flows are what you test.

Helpers on the PC: `tools/qterm` (control port), `adb logcat -s Unity`, `files/session_log.txt` on the device.

## 0. Install and first start

- [ ] `adb install -r unity/Builds/MetaCodex.apk` → installs (version 0.3.x)
- [ ] Start from the Library → passthrough, no boundary prompt (boundaryless), the dock in front; the terminal runs hidden (TERMS on the menu lists it, a tap shows it)
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

- [ ] Dock: two rows of 8 (Term Terms Files Git Agents Threads Web Scripts / Gesture Vault Talk Setup Keys Select Center Voice), a grab bar on top: pinch it and move → the dock moves
- [ ] New look: rounded dark-glass panels, blue primary buttons, clean type, no scanlines / grid / glowing frames
- [ ] Terminal `–` hides it (TERMS lists it as hidden; TERM shows it again); `✕` on the last terminal hides it instead of ending it
- [ ] Hover over a slider without pinching → it does not move; pinch and drag → it moves
- [ ] SELECT on → pinch-drag a rectangle → "Selected …" and the tool turns itself off; SELECT on, then SELECT again (dock or Settings) → off
- [ ] Each dock button opens/closes its panel; new panels open where you look, beside the others, not overlapping
- [ ] Open many panels (10+) → when no gap is left within a head turn, extra panels open further away in a free gap (smaller, tilted to you), and only when even that is full cascade in front (closer, higher, a bit to the side), never exactly on another
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
- [ ] DISPLAY: distance, carousel radius, UI scale; "Bend panels around you" bends panels in FIXED too (terminals stay flat)
- [ ] FLOAT with 10+ panels open → evenly spaced around you, none overlapping (the circle grows); drop a panel on another → it slides to a free place
- [ ] LOOK: Sharpness, Panel Opacity, Foveated rendering, Sounds, Volume (no themes, accents, scanlines or grid)
- [ ] Foveated rendering ON → the edges of the view get softer (VrApi `Fov=`); OFF (default) → sharp everywhere
- [ ] Settings survive an app restart

## 5. Files, Git, Browser

- [ ] FILES: /root, QUEST, / ; tap folders to open; CD HERE / VIEW / EDIT / PATH ⇥ act in the focused terminal
- [ ] GIT: SCAN finds repos under /root (clone one first: `git clone https://github.com/SolidityDrone/MetaCodex /root/MetaCodex`)
- [ ] GIT: branch, ahead/behind, changes, branches, metro history; FETCH, PULL, COMMIT… (keyboard), CHECKOUT (tap a branch)
- [ ] WEB: the in-app browser window (section 23); ☰ LINKS: bookmarks add/remove; LOCAL SERVERS lists running ports
- [ ] WEB: start `python3 -m http.server 8000` in a terminal → LINKS > SCAN shows :8000 → opens in the WEB window
- [ ] Horizon system pages: HANDS > HORIZON HANDS ⚙, ABOUT > APP PERMISSIONS ⚙, FILES > FILES APP open them (section 35)

## 6. Lua scripts

- [ ] SCRIPTS: + EXAMPLE → `double-clap` script; double clap → TERMINALS panel toggles
- [ ] Edit `/mnt/quest/scripts/double-clap.lua` in the terminal (nano) → saved changes apply within a second
- [ ] A broken script (syntax error) → ERROR in the list, toast; others keep working
- [ ] DISABLE / ENABLE / DELETE a script; state survives a restart
- [ ] `mcx-ui script list`, `mcx-ui script eval 'return 1+1'` → 2

## 7. Gestures studio and plugins

- [ ] MQH > GESTURES > ALL: YOURS, SYSTEM (7 built-ins), EXAMPLES folded; for the steps below + ADD `core-poses` and `swipes` first (since section 34 nothing is installed by itself)
- [ ] LIVE tab: finger bars move with your fingers (1 = straight); palm / thumb direction text; active gestures listed
- [ ] Make a thumbs up → LIVE shows `thumbs-up` (and `grab`); GESTURES list marks it ● ACTIVE
- [ ] Thumbs down, victory (✌), horns (🤘), call-me (🤙) → each recognised (LIVE / toast via a binding)
- [ ] Swipe an open hand fast left / right → swipe-left / swipe-right in LIVE's recent events
- [ ] Detector cost (LIVE, right column) stays well under 1 ms
- [ ] TEST ▶ on any gesture → simulated event (toast "Simulated …")
- [ ] BIND: pick `victory`, action "GIT", + ADD BINDING → making ✌ opens GIT; ON/OFF and DELETE work
- [ ] RECORD: name "finger-gun", RIGHT, hold a finger gun, RECORD IN 3 s → "saved" + summary; the pose then fires; it appears under plugin `my-poses`
- [ ] MQH > PLUGINS > + NEW PLUGIN… "test-plugin" → folder in /mnt/quest/plugins; both palms up shows a toast
- [ ] MQH > PLUGINS: enable/disable/reload/delete; detail shows poses, detectors, log
- [ ] Ask an agent on the headset (claude or opencode in a terminal): "make me a gesture plugin: when I do a thumbs up with my right hand open git; push my palm away to close it" → plugin appears and works (the same request passed in the Editor test)
- [ ] `mcx-ui plugin list`, `mcx-ui gesture list`, `mcx-ui gesture bind clap toggle:files`, `mcx-ui action open:git`

## 8. Agents and sign-in (MQH)

- [ ] AGENTS lists providers (Claude Code, Codex, Pi, Cursor, opencode if installed) with READY / SIGN IN / INSTALL
- [ ] Select Claude Code → SIGN IN ▶ → a new terminal runs `claude auth login`
- [ ] The terminal prints a link → toast "Link in …" → `⇗` on that terminal opens it in the Horizon browser
- [ ] Sign in in the browser; the redirect back to localhost completes the login (or paste the code with ⎘) → AGENTS ⟳ shows READY + account
- [ ] Same for opencode (`opencode auth login`) and Codex (`codex login --device-auth`: device code flow)
- [ ] GET installs a missing agent; UPDATE (installed agents only) updates it through MQH (or run `mcx-setup` in a terminal)
- [ ] DISABLE (or ENABLE: the button says the one that applies) switches a provider in MQH

## 9. Threads (MQH)

- [ ] THREADS: + PROJECT… → DEFAULT → name "My Test" → the project is listed under DEFAULT (`…/default/my-test`)
- [ ] + NEW THREAD…: providers, then models for the chosen provider; PERMISSIONS EDITS/AUTO/FULL; WORKSPACE CHECKOUT/WORKTREE; no FIRST MESSAGE field
- [ ] START → THREAD opens as "New thread · <project>" (NOT STARTED), the keyboard on the composer; first message "list the files and summarise the README" → SEND → the thread is made, its title is the start of that message, the timeline fills (you, agent, commands, tools)
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

## 13. Voice and the local AI service (0.4)

Reference: `docs/voice.md`. The models are not in the APK: install them once (530 MB, Wi-Fi).

- [ ] Dock: two rows of 8, with ◉ TALK (highlighted) and ♫ VOICE
- [ ] VOICE panel → "AI service: ● running" within a few seconds (the first start unpacks the runtime: a few seconds more)
- [ ] INSTALL MODELS → three rows go from – to a percentage to ✓ (speech→text 78 MB, commands LLM 364 MB, text→speech 81 MB)
- [ ] First TALK → Horizon OS asks for the microphone → ALLOW
- [ ] TALK, say "open git" → HUD above the dock: ● LISTENING with a level bar, then ✓ Opened GIT; GIT opens
- [ ] "close all windows" → every panel closes (dock and terminals stay)
- [ ] "open the providers page" → AGENTS opens; "show me the source control" → GIT
- [ ] "find the thread where we …" (words from a thread title) → THREAD opens on it (MQH)
- [ ] "switch to the workspace called <project>" → THREADS shows that project (MQH)
- [ ] "use the floating layout" / "fixed mode" → layout changes; "recenter" → panels come back
- [ ] A phrasing no template knows ("I need to see which AI providers are signed in") → ◆ THINKING, then the right panel (LLM path, about 2 s; the first one after start a few seconds more)
- [ ] "run ls" → a confirmation dialog in VR; RUN types it into the terminal
- [ ] Silence after TALK → "no speech heard" after ~6 s; TALK twice → stops and processes what was said
- [ ] VOICE > Spoken replies ON → the reply is also spoken (needs the voice model)
- [ ] VOICE > TYPE… "close all windows" → same as saying it; SAY… → speaks the text
- [ ] ~~LANGUAGE IT, say "apri git" → transcribed in Italian~~ (English only since 2026-11-12): VOICE has no LANGUAGE row; say "open git" → GIT opens
- [ ] Frame rate while talking: the status line stays at about 72 fps; VOICE shows the service's RAM
- [ ] VOICE: pick a voice (F1-F5, M1-M5) → a sample sentence in that voice; spoken replies use it
- [ ] Speed: "open git" → done right after you stop talking (~0.7 s pause + ~0.6 s); an unusual phrasing → ◆ THINKING under a second
- [ ] In a terminal: `mcx-ai status`, `mcx-ai say "hello" /tmp/h.wav && mcx-ai transcribe /tmp/h.wav` → "Hello."
- [ ] `mcx-ui voice run "open the vault"` → JSON result, VAULT opens; `mcx-ui voice list` → commands
- [ ] A plugin with `voice.command{...}` (skill quest-scripting) → its phrase works by voice; `mcx-ui voice match "<phrase>"`
- [ ] Gestures > BIND: `palm-up` (left) → `voice:toggle` → hands-free TALK
- [ ] Kill the service (`mcx-ai log` shows its pid; or restart the app) → it restarts by itself

## 14. 0.6: threads, workspace, views, vault git, focus, system

Threads and the workspace (MQH):
- [ ] Dock: two rows of 9 (… SYSTEM / TALK, VOICE, VAULT, GIT, FOCUS, SELECT, KEYS, CENTER, SETUP)
- [ ] THREADS: one list, each project folder in capitals with its threads under it (newest first); the project row says how many run / need you; a tap folds it; a tap on a thread opens it
- [ ] THREAD opens with the WORKSPACE module on its right; drag the thread → the module follows; drag the module somewhere else → it stays there relative to the thread; close the thread → the module closes
- [ ] FLOAT layout: the module rides right beside the thread on the ring; nothing overlaps; drag it away → it snaps back beside the thread
- [ ] Ask the agent to edit two files → CHANGES: a tile per file appears and lights up while it is edited (green added, amber changed, red deleted); grey tiles = uncommitted files the thread did not touch
- [ ] Tap a tile → the diff below (green / red lines); ▲ ▼ scroll; @ ASK puts the path into the composer
- [ ] GIT tab: branch, ↑↓, status, branches, history; COMMIT… / PUSH work in the thread's folder
- [ ] FILES tab: the folder tree (tap folders to open), preview of a file, EDIT opens nano in a terminal
- [ ] Chips under the composer: MODEL ▾ (models of the provider, then effort) → the next message uses it; ACCESS ▾ (Edits / Auto / Full access); the workspace chip; the CONTEXT ring with % (tap: compact or fork); ■ STOP
- [ ] ⑂ FORK… → "Same agent" → a new thread with the same conversation opens; FORK to another provider → a new thread in the same folder that knows what was said (it summarises where things stand)

Agent views:
- [ ] WORKSPACE > VIEWS > DEMO → a "What agents can show" panel: stats, a line chart, a diagram, a donut, a checklist, choices and a physics shelf in front with balls and boxes
- [ ] Push a ball with your index fingertip → it rolls; ❚❚ PAUSE / ▶ RUN / ⟲ RESET work; move the panel → the scene moves with it
- [ ] Hover a chart → the value shows in its top-right corner; hover a diagram node → its edges light up
- [ ] Ask a thread agent "show the architecture of this repo as a diagram" → a view opens by itself; the timeline shows `[▣ …]` instead of the code block
- [ ] An agent view with choices → tap one → it arrives in the thread as your message
- [ ] In a terminal: `mcx-ui view schema`, then `mcx-ui view show spec.json` (the skill's example) → the view opens; `mcx-ui view wait <id>` returns your tap

Vault > GIT:
- [ ] GIT on the dock (or voice "open git") → VAULT opens on its GIT tab
- [ ] TOKEN… (a GitHub token) → TEST → "● <your login>"; IDENTITY… sets name / email
- [ ] GIT USES IT → `git push` to a github.com https remote works without asking for a password (the vault logs the request)
- [ ] → AGENTS → threads get GH_TOKEN (`gh auth status` in a thread)

Ambient Focus:
- [ ] FOCUS on the dock → the room fades except the floor near you; the panels stay bright; FOCUS again → back
- [ ] Settings > FOCUS: RADIUS, INTENSITY, SOFTNESS change it live; BLACKOUT → black around you, only the windows
- [ ] "focus mode on" / "focus mode off" by voice
- [ ] Frame rate with focus on stays about 72 fps

SYSTEM:
- [ ] SYSTEM > TOOLS: tiles for what is installed (git, node, npm, python3, curl, …) with versions; missing ones dimmed ("+ install")
- [ ] Tap a missing tool (e.g. jq) → Install → a terminal runs the install; ⟳ CHECK → it shows as installed
- [ ] PACKAGES… → Debian packages you chose and npm globals
- [ ] STORAGE: the headset bar (Linux, AI models, shared, apps + system, free) and Linux by folder; tap a cache → CLEAN; LINUX LIMIT below the current size → warning

## 15. 0.7: the owner's 0.6 bug list

Look:
- [ ] Every panel has the console look: chamfered corners (big cut top-left and bottom-right), dark glass with a faint hex pattern, a steel frame, a cyan energy bar under the title, titles and buttons in capitals (Rajdhani)
- [ ] Buttons read as command cards (lit rim); sliders and progress as segmented energy bars; nothing looks washed out or neutral

Opacity:
- [ ] Settings > LOOK: PANEL OPACITY at 100 % → no part of a window is see-through: buttons, rows, tiles, the header
- [ ] Put one window in front of another at 100 % → no text, icon or button of the window behind shows through
- [ ] Around a window's edge and where its shadow falls on another window → no see-through line ("fissure")
- [ ] PANEL OPACITY below 75 % → windows behind show through faintly again (as intended)

Bending (Settings > LOOK > Bend panels, fixed layout):
- [ ] Walk closer / farther, drag a panel → it curves smoothly (no steps or jumps)
- [ ] Point at a bent window's edges → the reticle sits on the curved surface, buttons near the edges react where the beam meets them; pokes with the fingertip work on bent panels
- [ ] A text that changes fast (Debug / Perf, a running terminal-linked thread) → it stays on the curve, never flashes flat

Ambient Focus (branch feat/spatial-dome, no Space Setup needed):
- [ ] First FOCUS → Horizon OS asks for "spatial data" (allow it); FOCUS tab: "depth: live", then "floor: measured under your feet (… cm from the Guardian floor)"
- [ ] FOCUS on → a dome standing on the floor around you: walls, furniture and your hands near you stay visible, far ones fade (no dome "starting above")
- [ ] Walk around: the dome follows you; nothing flickers at its edge while you stand still
- [ ] RADIUS changes the dome's size live; SOFTNESS its edge; INTENSITY the darkness
- [ ] BLUR above 0 → the room outside the dome is a soft image (darker with INTENSITY); at once all round you, also at the edges of your view and behind you (there frosted colours until the camera has looked, never the sharp room); 0 → darkness only
- [ ] Hands always visible, also at BLACKOUT and with BLUR, and in front of a window
- [ ] The cut-out around near things (your hands, a chair, a person) is clean and steady: no flicker, no blocky edge (FOCUS: "camera: live …")
- [ ] Someone walks in front of a window, or you hold an object up → it stays visible in front of the window ("Real things in front of windows" on); off → the window covers it
- [ ] Same with a window that has stood alone for 3 s or more (it is then marked clear): the person or object shows in front of it within about half a second
- [ ] Both FOCUS depth settings on (Sharp depth, Real things in front): VrApi FPS near 72 with a few windows open; dictate into a thread chat several times → no lag during or after speech-to-text (the camera keeps running)
- [ ] Turn Ambient Focus off and on again within a minute → no seconds of low frame rate (the depth and the camera stay on, idle, for 60 s)
- [ ] Palm towards you + pinch (Horizon OS quick actions, left-hand settings) → the windows stay where they are (0.7.3: the gesture hides nothing); when a system menu has opened and taken the app's focus they step aside, and come back when it closes
- [ ] Without the "spatial data" permission → the old floor-plane darkening still works
- [ ] Frame rate with focus + blur on stays about 72 fps (Perf menu)

SYSTEM > TOOLS:
- [ ] Known tools show their own logo on their brand colour (git, node, python, rust, go, claude, …) with the version; tools without a logo show a glyph
- [ ] In a terminal: `cat /mnt/quest/system/tools.json` and `mcx-ui system tools` → the same list with versions and paths

Voice:
- [ ] "Open ambient focus" → Settings opens on the FOCUS tab (also when heard as "fogus")
- [ ] "Turn on ambient focus and set intensity to 0" → focus on, intensity 0, the slider moves
- [ ] "Set the dome radius to two meters", "radius 50 centimeters", "set the blur to fifty percent", "make the windows bigger", "volume a bit less", "turn off the sounds" → each changes that setting
- [ ] "Show the installed tools" → SYSTEM on TOOLS; "how much disk space is left" → SYSTEM on STORAGE

## 16. 0.7.3: UI scale, tools, vault, blur, wrist menu

- [ ] Open and close panels for a while, change a slider, say a voice command: no panel changes size by itself (they keep the UI SCALE)
- [ ] VOICE: everything inside the panel; FEMALE and MALE voices in two aligned rows on the right
- [ ] Settings > LOOK has no SHARPNESS; text is as sharp as before at 1.6
- [ ] SYSTEM > TOOLS: groups with a header each (AI AGENTS, JAVASCRIPT · TYPESCRIPT, PYTHON, NATIVE CODE, ANDROID · QUEST, GIT, TERMINAL UTILITIES, EDITORS); every tile has its logo or a pictogram, centred on its badge, nothing stretched or cut; no php / ruby / lua / deno
- [ ] ANDROID · QUEST > ndk → install: downloads the NDK r27d for arm64 (~120 MB), checks its sha256, `/opt/android-ndk`; a new terminal has `ANDROID_NDK_HOME`; the tile shows 27.3.13750724
- [ ] VAULT: "key: Android Keystore · tee" (or strongbox); on this development build a warning about `run-as`
- [ ] Settings > DAEMON > TOKEN typed → `grep -c mqhToken files/settings.json` shows an empty value; the MQH connection still works
- [ ] BLUR 50 %: turn the head slowly: the soft room is there all around where you have looked, no rectangle edge, it does not move with the head
- [ ] BLUR on: say a few voice commands: speech-to-text about as fast as with BLUR off (VOICE history, asr ms)
- [ ] Settings > DISPLAY > Main menu on the wrist: the dock goes; look at the back of the left wrist: the core appears; touch it with the right index finger: the rings unfold; touch FILES, SETUP, TALK; look away: it folds and hides; off again: the dock comes back
- [ ] Wrist menu: frame rate about 72 while it is open (Perf menu)
- [ ] Palm towards you + pinch: nothing vanishes; the Horizon OS quick actions menu is visible over the windows (if it is not, tell: it would need another way than hiding the windows)

## 17. 0.7.4-0.7.6: native apps, wrist menu, MQH, captures, session context

- [ ] Native apps: in a terminal `mcx-app doctor` → all ok; `mcx-app run /root/apps/x` → Install asked once, the app opens; again → no prompt
- [ ] Wrist menu: hold the left wrist as a watch; the rings stay whole, the hand behind them never cuts them; the right fingertip shows over a blade while pressing; windows still let hands and things in front show
- [ ] Dock / wrist: one MQH entry (no SCRIPTS / GESTURE); MQH opens on SKILLS; the tabs PLUGINS, SCRIPTS, GESTURES, DAEMON work; Settings has no DAEMON tab
- [ ] MQH > SKILLS: the shipped skills say READ-ONLY and REMOVE refuses them; + NEW SKILL opens NEW THREAD, START puts the brief in the draft's composer (SEND starts it); ⌕ FIND finds skills, ⇣ INSTALL adds one under YOURS, REMOVE removes it
- [ ] SELECT, then pinch-drag over a window at low PANEL OPACITY: CLIPBOARD has the capture, opaque, nothing of the room or the hands; no hitch at the moment of the capture (72 fps)
- [ ] CLIPBOARD from the wrist (CLIP), the dock and by voice ("open the clipboard", "show the screenshots"; Italian removed: English only since 2026-11-12); 21 captures with CLIPBOARD's KEEP at 20 → the oldest is gone
- [ ] A thread: + → REFERENCE CAPTURE → the list inside the thread panel; pick two → two chips; + → ATTACH FILES → a Linux file; SEND: the agent sees the images and the file
- [ ] Ask by voice, with a thread waiting for an approval: "what is this command I'm asked to accept, is it safe?" → spoken answer naming the thread, the command and its risks, never "it's safe"
- [ ] Ask by voice: "do we have the rust toolchain?", "is this build stuck?" (during a long `cargo build`), "what has this thread done so far?" (pointing at THREAD) → short spoken answers; "open settings" still opens Settings

## 18. After 0.7.6 (owner's feedback): questions, dialogs, captures, the new wrist menu

- [ ] Voice: "how many captures we took so far?" / "how many screenshots do we have?" (Italian removed: English only since 2026-11-12) → a spoken count (and the newest's time); CLIPBOARD does not open. "open the clipboard" and "can I open the clipboard?" still open it
- [ ] CLIPBOARD > CLEAR ALL pressed three times quickly: one dialog, parallel to CLIPBOARD, centred over it a few cm in front (never through it, never half off its edge); the extra taps delete nothing; CANCEL closes it
- [ ] Any other window's dialog (MQH > SKILLS > REMOVE, a thread's dialogs) opens over that window, parallel to it; a dialog asked by voice opens in front of you
- [ ] SELECT, pinch-drag over a window: the orange frame goes as soon as the capture is taken; the capture in CLIPBOARD has no orange border
- [ ] Wrist menu, opening: look at the back of the left wrist → the watch face (the time, ticks, a warm seconds point) builds up with the projector rays from the wrist; touch it → a shock ring runs out, the inner tiles swing in one way and the outer the other, icons draw with a scan line, names type in; nothing pops in or out at once
- [ ] Wrist menu, the left side: press FOCUS, CENTER, KEYS, SELECT with the right hand reaching over the left: the menu stays where it is and stays open, even when the left hand is lost for a moment; it does not run off
- [ ] Wrist menu: slide the fingertip across the face on the way to the left tiles → nothing is pressed, the menu stays unfolded; a straight poke into a tile runs it once
- [ ] Wrist menu: move the left arm around while looking at it → it stays on the wrist, no jitter when still, no lag when moving; turn the wrist away → it waits a moment, folds and fades; turn it back while it fades → it comes back from where it was
- [ ] Settings > DISPLAY > wrist menu off while it is open → it folds and fades away, the dock comes back

## 19. Window transitions and the void (owner 2026-11-09)

- [ ] Open any window (dock, wrist, voice): a spark runs out into a line, corner brackets lock on, two scan lines unfold it, the glass builds from a lattice of light top-down, texts flicker in; it ends exactly as before (no offset, no wrong size, text sharp)
- [ ] Close it: a surge, it collapses into a bright line, shrinks to a point, a flare; nothing left behind
- [ ] Open and close quickly several times (dock button twice fast): it reverses smoothly, never jumps, never stays half open
- [ ] FILES then FILES again from the dock while it is open: it pings (brackets flash round it), no re-opening
- [ ] Dialogs (CLIPBOARD > CLEAR ALL) and toasts come and go with the transition; the voice HUD with the quick one
- [ ] A terminal (TERM, hide with –) and a 3D view squash and unfold with their frame
- [ ] Bent windows (Settings > DISPLAY, bending on): the brackets and lines follow the curve
- [ ] Frame rate: open three windows at once and close them: no drop below 72 fps (Perf menu Snapshot)
- [ ] Ambient Focus on (FOCUS preset): outside the dome deep blue-black space, a slow nebula, a few twinkling stars, a faint grid on the floor and walls just past the edge, a ring of light where the dome meets the floor; BLACKOUT: space around you, the windows stay; with BLUR 50 %: the blurred room with the grid and the ring
- [ ] Ambient Focus while turning the head and walking: the nebula and stars stay with the room (no swimming), no flicker of the grid at the walls' edges

## 20. Cast windows and the new look (owner 2026-11-10)

Section 19's transition lines (the spark, the collapse into a line) are replaced by the cast below.

- [ ] Open a window (wrist menu, dock or voice): brackets lock on where it will be with a faint outline, a warm-white line sweeps across and the window is built behind it (a glowing honeycomb whose cells close and cool into glass, then buttons and text, hot for a moment); it ends exactly at rest, no pop
- [ ] Close it: the line runs back and takes the window with it, the brackets let go. Open and close quickly: it reverses from where it is, never jumps
- [ ] A terminal (TERM) is built with its frame: its text and background appear behind the line, nothing shows before it
- [ ] FILES then FILES again while it is open: it pings (a flash, the brackets close in round it), it is not cast again
- [ ] The glass: move your head sideways in front of a window: the hexagon lattice shifts behind the surface (it is inside the glass), a second grid of points deeper; a soft sheen crosses the glass
- [ ] Bring a fingertip slowly towards a window: a pool of light forms on the lattice under it and tightens as you come closer; the ray's cursor carries a light too
- [ ] Lists (MQH > GESTURES, FILES, THREADS): quiet rows; the chosen row floods with cyan from the left; hovered rows and buttons show a light crossing them
- [ ] Headers: a bright block before the title, the line under it steps up two thirds along, a pulse runs in it; a ruler down the left edge of large windows; a thin conduit along the bottom right with a pulse
- [ ] Small cards (messages, the voice HUD) have no corner brackets; windows and dialogs do
- [ ] Frame rate with five windows open: 72 fps (Perf menu Snapshot); the same while a window is cast

## 21. The orb (owner 2026-11-10)

- [ ] At start a sphere of energy ignites up to the right of where you face (about half a metre away): a white-hot core, plasma turning inside dark glass, three thin rings (two gold), small lights circling it; it reads in a bright room; no square of light round it. The first time a message says what it is
- [ ] It has depth: move your head near it, the plasma and the rings shift like real things inside and round a glass ball
- [ ] Glance left and right: it stays where it is in the room. Turn your chair a quarter turn: after a moment it comes along round you, never through your face. Walk two steps: it comes along
- [ ] Open windows until one would be behind the orb: it moves to a free place; close that window: a few seconds later it goes back
- [ ] It casts the windows: a fan of light opens from the orb to the window's nearest edge and rides on the scan line; its rings turn to face the window. A window to the left of the orb is built from its right edge, one below it from its top. Closing: a spark flies home to the orb
- [ ] Voice (TALK): while it listens the orb is gold and its rings face you, the HUD card stands beside it; while it thinks it is violet and spins; a spoken answer comes from the orb's direction, the orb pulses with the words and ripples leave it
- [ ] Any message (SELECT on and off gives two): a card beside the orb, towards the middle of your view, joined to it by a thin rail; a second message takes that place and the first moves up; the orb flashes in the message's colour (green ok, amber warning, red error); afterwards a larger gold spark circles the orb for each message not read yet
- [ ] Touch the orb with a fingertip: its menu opens beside it (TALK, SPEAK, PLACE, CLEAR and the messages so far, the newest first, with their age; "N NEW"); the gold sparks go; touch it again: it closes. Point at the orb with a ray and pinch: the same; the window behind the orb is not clicked
- [ ] Pinch the orb between thumb and index and move the hand: it follows; let go: it stays there and keeps that place by you when you turn. Its menu > PLACE: it goes back to choosing its own place
- [ ] Settings > LOOK > Orb off (or voice "turn the orb off"): it fades out; windows are cast from their left edge; messages float in the upper view. On again: it ignites
- [ ] Frame rate with the orb and five windows: 72 fps; bring the orb close to your face (pinch it there): still 72 fps

## 22. Smart positions (owner 2026-11-10)

- [ ] The first window opens in front of you; the next ones right beside it, none over another
- [ ] SYSTEM > STORAGE > AI MODELS: VOICE opens right beside SYSTEM (also when you look elsewhere). THREADS > a thread: it opens beside THREADS
- [ ] Close a window you had dragged somewhere and open it again: it is back there. Put another window in that place first: it opens beside instead. Turn right round and open it: it opens in front of you
- [ ] Look down at the wrist menu held to your left and press FILES: it opens in front of your body, not off to the left
- [ ] With UI SCALE 0.8 and DISTANCE 1.3 m, open six windows: one goes above the row (tilted towards you) rather than far to the side; none goes below
- [ ] Stand 70 cm from a wall and open a window (depth on: Settings > FOCUS > Real things in front): it opens in front of the wall, not inside it

## 23. 0.7.10: the orb's artifacts, hands in front of windows, flat glass, performance (owner 2026-11-10)

- [ ] The orb, for a minute (idle, touch it for its menu, move round it, put a hand near and behind it): no flat cream blocks, stripes or half discs on it, in any frame; it looks as before (core, plasma, rings), with four motes
- [ ] A hand in front of a window shows, also with Settings > FOCUS > "Real things in front of windows" OFF (your headset had it off): poke the orb's menu and a big window from in front, the finger stays visible over the glass
- [ ] A hand behind a window stays hidden behind it (the cut-out does not punch through an opaque window)
- [ ] The glass of the windows: the honeycomb and the points are on the surface; moving the head sideways, nothing shifts inside the glass any more
- [ ] Hovering and pressing buttons in a big window (MQH, SETTINGS): no stutter; the light on a button still follows the hover and the click
- [ ] Frame rate (Perf menu Snapshot) with five windows open and the orb in view: 72 fps

The orb's billboard (same version):

- [ ] The orb: touch it → a small billboard next to it (TALK, SPEAK, PLACE, LOG); LOG shows "n NEW" when messages came; it opens LOG · MESSAGES (the list, CLEAR ALL asks first)
- [ ] ~~Wrist menu > TABLE: OPEN KEYBOARD shows the keyboard; TURN ON opens Settings~~ (TABLE removed 2026-11-13, section 43)

The in-app browser, WEB (same version):

- [ ] WEB on the dock or the wrist: a window opens beside the others on a start page (bookmarks, local servers); the app stays on, everything else stays visible
- [ ] Type `localhost:8000` (with `python3 -m http.server 8000` running in a terminal) → GO: the page shows in the window; tap a link with a pinch and with a fingertip; drag the page to scroll; ◀ ▶ ⟳ work; the title and "loading %" show under the toolbar
- [ ] Tap a text field on a page, ⌨, type, done: the text is in the field; ⏎ submits, ⌫ deletes
- [ ] A Vite / dev server link printed in a terminal (`⇗` on it) opens in the WEB window; a sign-in link (`claude auth login`) opens in Horizon's browser instead, as before
- [ ] ⇗ in WEB opens the page in Horizon's browser; on a PC with USB adb, `chrome://inspect` lists the page (console, DOM)
- [ ] Moving the WEB window, closing and reopening it: the page is still there; with "Bend panels" on it stays flat
- [ ] Frame rate with WEB open on a scrolling page: 72 fps; closed: no cost

## 24. 0.7.11: MQH by itself, agents to GET, an API key pasted, MetaCodex's own keyboard (owner 2026-11-10; keyboard checks struck: removed 2026-11-13, section 43)

- [ ] Start the app and wait half a minute: MQH > DAEMON says "running in this headset's Linux", a message says "MQH is running in this headset", AGENTS fills in by itself (nothing to connect)
- [ ] AGENTS > GET OPENCODE: a terminal installs it (about a minute); the tile turns into "✓ 1.19.x"; opencode shows in the providers
- [ ] ⚿ API KEY: pick your provider, copy your key (in WEB or Horizon's browser), ⎘ PASTE, SAVE: "✓ … set for the agents"
- [ ] NEW THREAD with opencode: it answers (with your key, no terminal login)
- [ ] GET CLAUDE CODE and GET CODEX install the same way; MORE · COMING SOON cannot be pressed
- [ ] ~~Any text field: MetaCodex's keyboard floats in front of you~~ (removed)
- [ ] ~~Rest both hands flat on a table: the keyboard lies down under them, the touchpad on its right~~ (removed)
- [ ] ~~Type on the table: lift a finger and tap ...~~ (removed)
- [ ] ~~KEYS on the dock or TABLE on the wrist: the keyboard types into the terminal~~ (removed; see section 43)
- [ ] ~~Touchpad: the cursor moves over that window~~ (removed)
- [ ] Frame rate with MQH running: 72 fps

## 25. THREAD as a chat, the agent's state, voice into the composer (owner 2026-11-11)

- [ ] Open an OpenCode thread and send a task: your message in a bubble on the right; under it `◐ Working` with the steps appearing one by one (thoughts, `$ commands`, `✎ files`)
- [ ] While it thinks: "Thinking…" with its newest sentence under it; a tap on a thought opens its text
- [ ] The status line under the chat says what it does now and for how long (`Running npm test · 0:45   turn 3:12 · live`)
- [ ] The answer appears in full under the work and grows while it is written (a caret at its end); the work header closes to `⚙ Worked 4:31 · …` when it is done, a tap opens it again
- [ ] Ask for something long (several commands): when the steps push your prompt off the top, it stays pinned there; a tap on it scrolls back to it
- [ ] Scroll up while it works: the view holds still, `↓ NEW` shows; a tap goes back to the end
- [ ] The meta row shows the plan and the usage windows (OpenCode Go: 5h · week · month); the CONTEXT ring's picker lists them with their resets
- [ ] ◉ beside SEND: say a message, pause: the words are typed into the composer (not sent); SEND sends them. The same ◉ in a new thread's draft (the first message)
- [ ] TALK: "what is this thread doing?" / "is it stuck?" (the Italian one removed: English only since 2026-11-12): a short spoken answer at once (no waiting for the model)
- [ ] Stop a thread's provider mid-turn or wait on a silent step: after ~2 min the status line turns amber ("may be stuck")
- [ ] A thread finishes while its window is open: the frame pulses three times and the answer glows; the orb casts “✓ … answered: …”
- [ ] A thread finishes while you do something else (its window closed): within a few seconds the orb casts “✓ … answered”; a tap on the callout opens the thread
- [ ] A thread asks for an approval in the background: the orb casts “… needs you” (amber)
- [ ] Frame rate with a working thread open: 72 fps

## 26. CONTROL and the calmer main menu (owner 2026-11-11)

- [ ] The wrist menu: 6 inner tiles (TALK, TERM, FILES, THREADS, WEB, CONTROL) and 7 outer (TERMS, CLIP, FOCUS, SELECT, KEYS, TABLE, CENTER); easier to hit, nothing missing you use daily
- [ ] CONTROL: one window with the rail on its left (ten categories, each with its status: fixed, sound, on / off, ready, running, keys)
- [ ] Tap AGENTS, MQH, VAULT, VOICE, SYSTEM in the rail: each takes the same place, the rail stays still; DISPLAY / LOOK & SOUND / FOCUS / HANDS / ABOUT only change SETTINGS' page
- [ ] Drag the window by its header: the rail comes along; pinch the rail's bar and move it: the window comes along
- [ ] ✕ on the window closes the rail too; CONTROL on the menu brings back the last category; pressed again it closes
- [ ] Voice: "open the control panel", "open agents", "open git", "open the settings" (Italian removed: English only since 2026-11-12) open the right page with the rail
- [ ] THREADS > ◆ AGENTS opens CONTROL on AGENTS
- [ ] With a terminal open, TERMS from the menu opens right beside it; with a thread open, CLIP opens beside the thread
- [ ] FLOAT: CONTROL and its rail take one place on the ring, nothing overlaps them

## 27. Workspaces: several threads in one project (owner 2026-11-11)

- [ ] Start a thread in a project's CHECKOUT; while it works, open NEW THREAD on the same project: WORKTREE is chosen, the summary says who works in the checkout
- [ ] Start that one in a WORKTREE: both work at once; THREADS shows the project's folders (▣ checkout, ⎇ the worktree's branch) with each thread under its own
- [ ] Send a message to a second CHECKOUT thread while the first works: it does not start; THREAD says "⏸ Not started: “…” is working in this checkout" with ▶ SEND NOW; when the first is done it starts by itself
- [ ] SEND NOW: asks first, then both work (THREADS shows 2 WORKING ⚠ on the checkout)
- [ ] ⎇ WORKSPACES: the worktree shows its branch, base, ahead/behind and changes; MERGE INTO MAIN on a clean branch merges (the checkout on main and clean); on a conflicting one it lists the files and ASK ITS AGENT sends the rebase request
- [ ] REMOVE a finished worktree: its threads are archived and the folder goes; the branch is still in `git branch`
- [ ] Voice "open the worktrees"; "what is this thread doing?" on a held thread says why it has not started

## 28. The orb's menu: plain squares, even gaps (owner 2026-11-13)

The thread cards that stood beside the menu are gone. Their owner said they were bad UX.

- [ ] Touch the orb: four squares on an arc round its left side, TALK, SPEAK, PLACE, LOG from the top; no cards beside them
- [ ] The squares have no frame: no outline box and no shadow box round them, only each square with its label
- [ ] The gap between two neighbouring squares is the same all round the arc, about 8 mm at the menu's size; no square touches the orb's rings
- [ ] Each square works: TALK starts and stops listening, SPEAK toggles spoken answers, PLACE gives the orb its own place, LOG opens LOG · MESSAGES (`✉ n NEW` while messages are unread)
- [ ] The menu opens and closes with the light: a line of light crosses each square (there is no frame to light)
- [ ] The labels read at arm's length without leaning in, the same size as before
- [ ] Frame rate with the menu open and the orb in view: 72 fps


## 29. Native apps: builds off the frame loop, `mcx-app check`, the Runner (branch feat/native-horizon-building-and-install)

Needs a MetaCodex build with this branch (overlay, MQH bundle, `MetaCodexAiService`). Plan and numbers:
docs/native-build-and-install.md.

- [ ] A terminal: `mcx-app doctor` → `ok` for mcx-build, mcx-buildd, aapt2, openssl, python3; `builds  through mcx-buildd (cores 0-2)`
- [ ] `mcx-app new /root/apps/t1 --module --label "T1"`, then `mcx-app check /root/apps/t1` → `RESULT: check ok` in about 1 s
- [ ] Break a line in `src/app.cpp` (remove a `;`), `mcx-app check` → `app.cpp:<line>:<col>: error: ...` and `RESULT: 1 error(s)`; fix it
- [ ] `mcx-app runner` → Android asks to install "Runner" once: tap Install → `RESULT: installed`; "Runner" is in the Library
- [ ] `mcx-app dev /root/apps/t1` → the Runner opens with three turning cubes (`RESULT: running in the Runner`, about 3 s)
- [ ] Back in MetaCodex, change a cube colour in `src/app.cpp`, `mcx-app dev` again → in the Runner the colour changes without the app restarting (about 2 s)
- [ ] `mcx-app run /root/apps/t1` → Install once; "T1" opens by itself (its own Library icon) with the same cubes; Meta button > Quit; force-stop MetaCodex (Settings > Apps); open T1 from the Library: it still runs
- [ ] Wear the headset, open the Perf menu, and in a terminal: `for i in 1 2 3 4 5; do touch /root/apps/t1/src/app.cpp; mcx-app build /root/apps/t1; done` → 72 fps the whole time (a build ~2 s), voice commands still answer
- [ ] During those builds say "what is this build doing?" or use a thread: MQH answers normally
- [ ] Settings > Apps > MetaCodex shows a running service / notification "Local services running" (the AI service is now a foreground service)
- [ ] Open T1 (MetaCodex in the background) and stay in it 10 minutes; come back: MQH threads, the agents and voice still work without a restart (DAEMON says running, not "starting again")
- [ ] Start `mcx-app build /root/apps/t1` from a thread and take the headset off for 2 minutes; back on: the build finished (log `RESULT: APK ...`)
- [ ] Night Vision: `cp -r /opt/metacodex/quest-native/examples/nightvision /root/apps/nv`, `mcx-app build /root/apps/nv`, `mcx-app verify /root/apps/nv` → `RESULT: verify ok`; `mcx-app install` + `launch` → Android asks for spatial data once: Allow
- [ ] Night Vision: the room in green; near things bright, far ones dark, outlines glowing; grain and scanlines; the HUD says `depth ... LIVE`, `room N objects`, `72 Hz`
- [ ] Right pinch (or right trigger) cycles PHOSPHOR / EDGES / RANGE; left pinch (or A) turns the camera off (depth image alone) and on; stick up/down changes the gain; open the system menu: nothing reacts until you close it
- [ ] Night Vision has its own icon (green goggles) and label in the Library; it runs with MetaCodex force-stopped
- [ ] Switch between MetaCodex and an app built here 5 times; then Perf menu / `adb shell dumpsys meminfo com.pnncl.metacodex | grep "GL mtrack"`: MetaCodex's GPU memory does not climb by hundreds of MB per switch (11-11: 4.87 GB after ~6 switches + a sleep, lmkd then killed the AI process)
- [ ] With MetaCodex in the background (an app built here in front) start a long build from a thread: `mcx-buildd status` shows `"budget": "paused (background budget)"` once it passes 15 % CPU over 5 min, and MetaCodex's AI process is not killed (no `excessive cpu` line in logcat)

## 30. The way back from built apps (2 minutes; needs the MetaCodex build after 0.7.14)

- [ ] In a thread ask for any small native app (or `mcx-app new /root/apps/t2 --label "T2"` + `mcx-app run /root/apps/t2`): it opens
- [ ] Left controller's menu button (or look at the left palm and pinch): the title says "opened from MetaCodex"; Back to MetaCodex, Report to MetaCodex, Quit, Close menu; point and pull the trigger (or pinch) to pick
- [ ] Back to MetaCodex: MetaCodex is there at once (no loading screen, windows where they were), a toast "Back from T2", the launching thread in front; wait 60 s: no "T2 isn't responding" dialog; T2 is not in the recent apps (it closed)
- [ ] Open T2 from the Library: the menu says "opened outside MetaCodex", only Quit (no MetaCodex items); Quit closes it, Home
- [ ] Launch T2 from MetaCodex, press the Meta button > Home, reopen T2 from the Library: the menu has no MetaCodex items
- [ ] Launch T2 from MetaCodex, open MetaCodex from the dock (no Back): wait 60 s: no "isn't responding" dialog; T2 still in the recent apps, opens again fine
- [ ] Open the app again, menu > Report to MetaCodex: back in MetaCodex, the thread's composer says "Feedback on T2 1.0 (1):" with two chips (▣ the app's picture, ⎘ log T2); ◉ to say what you saw, SEND: the agent sees the picture and the log
- [ ] Open the app again, quit it from the system menu (right hand / Meta button > Quit): does MetaCodex come forward by itself? (tell: best effort, Android may refuse)
- [ ] After 5 app switches: `adb shell dumpsys meminfo com.pnncl.metacodex | grep -E "GL mtrack|TOTAL PSS"` (or `tools/gpu-switch-test.sh <package> 10` from the PC with the headset free); note the numbers

## 31. Launches by request, Native / General projects (0.7.16)

- [ ] THREADS > + PROJECT… > EXISTING…: a folder, then PROJECT TYPE: NATIVE APP, DEFAULT or RESEARCH; the project is listed under that type (§ 41)
- [ ] A Native project's folder has `.metacodex/project.json`, `.claude/skills/quest-native-*` (links) and AGENTS.md / CLAUDE.md with the native block; a General one has none of them (`ls -la <folder> <folder>/.claude 2>&1` in a terminal)
- [ ] In a General project ask an agent "what are your rules for apps?": no word about launch requests or the app menu
- [ ] In a Native project, with the thread in its most permissive approval mode, ask for a small app: the agent builds and verifies, then a dialog APP READY: <app>: LAUNCH / LATER appears in front of you (and a toast in MESSAGES); nothing opens by itself
- [ ] LATER: the app does not open; say "launch the app" (Italian removed: English only since 2026-11-12): it opens; its menu's Back brings you to that thread
- [ ] A brand-new app: the dialog says INSTALL first; INSTALL -> Android's dialog -> then APP READY with LAUNCH
- [ ] Say "don't open the app" while one waits: the agent's `mcx-app status` says declined

## 32. Thread state after app switches, the orb's arcs, the orb off the watched window, the centred thread + sidebar (branch fix/thread-sidebar-and-orb, next build)

- [ ] A thread working; launch a built app, wait until the agent is done, Back: the thread says idle / done at once (no "WORKING", no "writing the answer")
- [ ] A thread working: STOP: within 2 s the header leaves WORKING and the chat stops "writing"
- [ ] The orb: messages always rise on its right; its menu (TALK, SPEAK, PLACE, LOG) always sits on its left, level with it, whichever way you look; never swapped
- [ ] Idle, not looked at, no message: the orb is a little smaller; look at it or bring a hand close: it brightens and grows back; touching it still opens the menu
- [ ] Touch the orb: TALK, SPEAK, PLACE, LOG sit on a small arc hugging its left side, as plain squares (see section 28); every square faces you; labels readable at arm's length without leaning in
- [ ] Two or three messages in a row: they stack on an arc round the orb's right side, the newest lowest, the line of light runs along the arc; never on its left
- [ ] Read a window straight ahead, the orb up to its right, and touch the orb: the orb and its menu glide (no jump) to a place where nothing of them covers that window, still in view and reachable; close the menu: a moment later the orb goes back to its own place
- [ ] While the menu is up, reach for a square: nothing moves under the finger; look at the menu itself: the orb does not run away from it
- [ ] FIXED layout, a thread in front: tap the sidebar handle: the thread and its sidebar glide together until the pair is centred in front of you; fold it: the thread glides back. With another window just to its left: the pair moves only up to that window, never over it. Move the thread, then fold: it stays where you put it

## 33. THREAD's sidebar (branch fix/thread-sidebar-and-orb, next build; docs/thread-sidebar.md)

- [ ] Open a thread: a handle `›` with PROJECT and the number of changed files on THREAD's right edge; tap it: the sidebar opens at the thread's right edge, tops level, bent with it (one curved screen); `‹` folds it
- [ ] Drag THREAD: the sidebar comes along; pinch the sidebar's header: both move; FLOAT: the pair keeps one slot, nothing overlaps it
- [ ] Open the sidebar on GIT in thread A, keep it folded in thread B: switching between them brings back each one's state; B never shows A's files
- [ ] CHANGES in a repo: every file the agent changed, also through commands (sed, npm), untracked new files included; the folder's other changes under ALSO CHANGED IN THIS FOLDER; the summary says against HEAD (or since the base branch in a worktree)
- [ ] Tap a file: the diff reads like VS Code: old / new numbers, + / − gutter, green / red lines, coloured code, `⋯ N unchanged lines` (tap: they open); readable at arm's length without leaning in
- [ ] A long line: drag sideways to read it; WRAP breaks it over rows instead; ▲ PREV / ▼ NEXT walk the files; `@ ASK` puts the path in the composer
- [ ] While the agent edits the open file: the list and the diff update by themselves within a few seconds, the view keeps its place
- [ ] A big file (a lock file, a generated JSON) and an image: the big one shows only its changed part and says so, the image says binary; nothing stalls
- [ ] FILES: the project's own folder (not /), breadcrumb `project › src`, a tap on a part goes back up, never above the project; changed files coloured; a file opens with line numbers and colours
- [ ] GIT in a repo: branch, ahead / behind, status, recent commits; in a folder with no repo only NOT A GIT REPOSITORY + INIT GIT; INIT GIT works and the tab becomes a repo
- [ ] STATUS: model, access, context %, what runs now and for how long, approvals waiting, usage, folder, MQH
- [ ] A Bluetooth keyboard after touching the sidebar: ↑ ↓ / Page / Enter / Esc move and open in it; touch the terminal and the keys go there again
- [ ] Voice: "open the sidebar on the git tab", "close the thread sidebar" (Italian removed: English only since 2026-11-12), "show the status tab of this thread", and in a folder with no repo "make this folder a git repository"

## 34. Gestures and scripts: a clean start (branch feat/polish)

Replaces the section 7 steps that expect `core-poses` / `swipes` to be there, "Open vault" in BIND, + NEW PLUGIN… in RECORD, PLUGINS in the studio, and the SCRIPTS gesture monitor.

- [ ] Fresh install (or the app's data cleared): MQH > GESTURES > ALL shows YOURS (0) with one dim line "none · RECORD a pose or ASK AN AGENT", SYSTEM (7) (pinch click, grab move · spin, point ray, open open hand, palm-up palm up, clap two hands, select select tool), ▸ EXAMPLES (2) folded; no ⟳ REFRESH button
- [ ] Tap EXAMPLES: it opens (▾ EXAMPLES) with `core-poses` and `swipes`, each "+ ADD"; tap it again: it folds
- [ ] Pick a SYSTEM gesture: TEST ▶ and BIND → work; SOURCE and DELETE POSE are greyed out and do nothing
- [ ] Pick `core-poses` under EXAMPLES: the right side lists its 5 gestures and says "Not installed"; + ADD (the only lit button) → toast "Example core-poses added …", the row says ADDED and its gestures appear indented under it; YOURS stays (0)
- [ ] Make a thumbs up after + ADD → LIVE shows `thumbs-up`; MQH > PLUGINS lists Core poses
- [ ] `mcx-ui plugin examples` lists both with installed=True/False; `mcx-ui plugin example swipes` → ok, swipes appear under EXAMPLES
- [ ] RECORD a pose "finger-gun" → it appears under YOURS (✋ pose · my-poses); SOURCE and DELETE POSE are lit for it; DELETE POSE asks, then removes it
- [ ] RECORD's right side is one dim line "Plugins: MQH > PLUGINS (+ NEW, or ask an agent)"; no + NEW PLUGIN… and no OPEN IN TERMINAL there
- [ ] MQH > PLUGINS > + NEW PLUGIN… "test-plugin" → both palms up shows the toast "Both palms up" and opens nothing
- [ ] BIND: 12 presets in this order: TALK, TERMINALS, NEW TERMINAL, THREADS, FILES, WEB, GIT, KEYBOARD, SELECT, RECENTER, FOCUS, CTRL-C; bind `clap` to WEB → a clap opens the WEB window (not LINKS); bind `clap` to TALK → a clap starts listening, another stops; DELETE the bindings
- [ ] A binding saved before this build with an old preset (e.g. `toggle:browser`, `spin:40`) still runs
- [ ] MQH > SCRIPTS on a fresh install: one dim line "No scripts · + EXAMPLE, or ask an agent (skill quest-scripting)"; no plugin rows, no GESTURE MONITOR; + EXAMPLE → `double-clap` with badge ON; double clap toggles TERMINALS
- [ ] Update over an old install that had the starters, never edited, no binding on them: after the first start `core-poses` and `swipes` are gone from MQH > PLUGINS and /mnt/quest/plugins (`ls -a` shows `.examples-v2` and still `.starter-installed`); my-poses and your own plugins are still there
- [ ] Update over an old install where a binding names `victory` (or a script or plugin reacts to `thumbs-up`): `core-poses` stays (under EXAMPLES, still working); an edited starter (one pose file changed) stays and shows under YOURS
- [ ] Restart the app after + ADD of an example: it is still installed (the cleanup runs only once)
- [ ] Ask an agent on the headset for "a thumbs up with my right hand opens git" on a fresh install: its plugin has its own `poses/thumbs-up.json` and works without + ADD

## 35. Menus and layout polish (branch feat/polish)

Main menu (dock: Settings > DISPLAY, main menu on the wrist off; wrist: on):

- [ ] Dock: TALK TERM FILES THREADS WEB CONTROL / TERMS CLIP FOCUS SELECT KEYS CENTER, the wrist's order without TABLE; the wrist still has TABLE on the outer ring
- [ ] Dock: open FILES, THREADS, WEB, TERMS, CLIP from it: each tile stays selected (lit) while its window is open and goes dark when it closes (within a quarter second); CONTROL lit while CONTROL shows; FOCUS and SELECT lit while on; KEYS lit while the terminal keyboard is up (also on the wrist); TALK lit while listening
- [ ] Watch the fps overlay with the dock open for a minute: 72 fps, no hitch every quarter second
- [ ] ✕ on the last terminal: the toast says "TERMS on the menu shows it"

CONTROL:

- [ ] The rail reads THIS HEADSET: ◧ DISPLAY, ◈ LOOK & SOUND, ◐ FOCUS, ✋ HANDS / AI: ♫ VOICE, ◆ AGENTS, ⬡ MQH, ⚿ VAULT / LINUX & APP: ⬢ SYSTEM, ✦ ABOUT; row names in the same narrow capitals as CONTROL and the group captions; nothing cut off ("LOOK & SOUND" whole)
- [ ] With no agent signed in, AGENTS' status on the rail says "sign in" (not "set up")
- [ ] Click every category on the rail one after another: every window has the same size, the top-left corner and the rail never move, no window grows over a neighbour
- [ ] Voice or a binding with `ui.open("display")`, "look", "hands", "about" (e.g. `mcx-ui action open:about` from a terminal): CONTROL opens on that page; close it; CONTROL on the menu brings that page back
- [ ] HANDS: the button row reads SELECT AREA ⌖ | GESTURE STUDIO ▸ | HORIZON HANDS ⚙; the line at the top ends "GESTURE STUDIO: your own gestures."; GESTURE STUDIO opens MQH on GESTURES in HANDS' place; HORIZON HANDS opens Horizon's hand settings
- [ ] DISPLAY in FIXED: FLOAT RADIUS dimmed with "FLOAT only" under its name; switch LAYOUT to FLOAT: FLOAT RADIUS bright, DISTANCE dimmed with "FIXED only"; no CLIPBOARD SIZE row any more
- [ ] ABOUT: no raw log lines; LICENCES ▸ opens FILES at /usr/share/doc/metacodex (LICENSES.md there); APP PERMISSIONS ⚙ opens Horizon's settings for MetaCodex
- [ ] MQH: the title reads MQH · SKILLS · PLUGINS · SCRIPTS · GESTURES; the last tab is STATUS (URL, token, sync, ⟳ RESTART); SKILLS' right side has no ", as bb does"; its three buttons are not cut off
- [ ] MQH > PLUGINS: pick a plugin: the first button says DISABLE when it is on, ENABLE when it is off; with nothing picked it is dim
- [ ] Stop MQH (or wait for a failure): AGENTS says "MQH > STATUS > RESTART tries again"
- [ ] AGENTS: pick an installed agent: the second row reads DISABLE (or ENABLE) | NEW THREAD → | UPDATE; pick one that is not installed: no UPDATE; no OPEN LINK button; the hint says ⇗ on the sign-in terminal opens the link
- [ ] SYSTEM > TOOLS: the bottom row is ⟳ CHECK | PACKAGES… (no TERMINAL)

Work windows:

- [ ] LINKS (WEB's ☰): the title is LINKS; the bottom row has only ⟳ SCAN
- [ ] FILES: FILES APP at the end of the top row opens Horizon's Files app
- [ ] CLIPBOARD: a KEEP slider above its buttons; set 5 with more captures: the oldest go, the line says "n of 5"; the preview still shows whole
- [ ] Orb menu: ✥ PLACE and ✉ LOG (no more ⌖ / ☰ there); with unread messages ✉ n NEW
- [ ] LOG: the window is titled LOG · MESSAGES; CLEAR ALL (red, as big as other buttons) asks "Clear all n messages?"; CANCEL keeps them, CLEAR ALL empties the list
- [ ] Pinch the orb to a new place, let go; quit the app (with MQH sync on) and start it again: the orb is where you left it; menu > PLACE, restart: it chooses its own place
- [ ] A thread whose host is not connected: THREAD's header says OFFLINE (not NO HOST)

## 36. Spoken replies and the voice effect (branch feat/polish)

- [ ] Ask a session question with a 2-sentence answer: the first words come about 3-4 s after the text appears (was 8-10 s), with a short pause between sentences; only 2 sentences / about 200 characters are spoken and the full answer stays on the HUD.
- [ ] While a reply speaks, press ■ STOP on the voice HUD (also try TALK, and a new question): the voice stops at once and no later sentence plays.
- [ ] Ask "is it stuck?" on a working thread: durations are spoken as "3 minutes 20 seconds", not "three men". ~~With VOICE > LANGUAGE IT an Italian reply says "3 minuti" with the Italian voice.~~ (English only since 2026-11-12)
- [ ] VOICE panel: the service button reads ▶ START or ■ STOP from the state; a tap on a COMMANDS row shows Say “…” first and at most one short line, never an id like open_panel.
- [ ] VOICE > SOUND shows OFF / AI / SHIP / COMM with AI selected on a fresh install. Pick each one: it says "Systems online. All panels ready." in that sound. AI is the same voice, slightly computer-like with a small room; SHIP is clearly synthetic with a longer tail; COMM is a narrow radio with a squelch at the start and the end; OFF is the plain voice.
- [ ] All four are equally loud, never crackle or clip, and the first word starts without the old half-second of silence.
- [ ] Say "make your voice sound like a radio" -> COMM is selected and replies sound like a radio; say "give the assistant its normal voice back" -> OFF; "switch the voice effect to starship" -> SHIP (the Italian one removed: English only since 2026-11-12).
- [ ] VOICE > SPEECH: FAST (3 steps) is selected on a fresh install. Ask the same question with FAST and with CLEAR: FAST starts a little sooner; say if FAST ever drops or swaps a word you notice (then CLEAR becomes the default).
- [ ] Faster voice (3 steps, shorter first clip, warm-up waits, agents at low priority): with an agent working, ask a session question. The first sound comes sooner than before (about 12 s measured before).
- [ ] The same answer: no gap between the first clip and the next one (only the normal short pause).
- [ ] With the voice effect on (AI, SHIP, COMM), FAST speech stays easy to understand; say if a word is lost.
- [ ] Restart the app: the chosen SOUND and SPEECH are kept.
- [ ] Watch fps during a long spoken reply with SHIP: 72 fps holds (the effect runs off the main thread).

## 37. Fast recaps, streamed answers, the tools list (0.7.17)

- [ ] Ask "give me a recap of the <name> thread" (a thread that is not open): "Let me look." at once (about 1.5 s after you stop talking), the HUD says ◆ LOOKING, then "The <name> thread is idle / working / waiting for you." and one more sentence; it is the thread you named, not the open one
- [ ] Same with "summarize this thread", "catch me up", "what has it done so far" on the open thread: the first real sentence within about 4 s
- [ ] "How many captures did we take so far?" answers the count (not a recap)
- [ ] A question the app needs the model for ("why did the build fail?"): "Let me look.", then the answer is spoken sentence by sentence while the HUD text still grows (◆ ANSWERING); STOP or TALK mid-answer: the voice stops and no later sentence plays
- [ ] During a long spoken answer: 72 fps holds, the voice has no gaps between words
- [ ] Change VOICE > SOUND or SPEECH, then ask a recap: "Let me look." already has the new sound (or, the very first time, the answer comes without it)
- [ ] VOICE has no LANGUAGE row; Italian phrases do nothing (English only)
- [ ] "never mind", "what can you do for me?": nothing opens or toggles
- [ ] SYSTEM > TOOLS: a grouped plain list (no logos, no gh); ABOUT > LICENCES opens /usr/share/doc/metacodex

## 38. The local coder (branch local-coder, 0.7.18)

- [ ] AGENTS: the GET row has LOCAL CODER · GET 430 MB; tap: it counts up to 100 %, then "✓ ON HEADSET"; tap again: a hint how to use it
- [ ] NEW THREAD with Claude Code, model "Local coder (this headset, slow)": ask anything small (a script, a fix, a small native app in a NATIVE project): it works in minutes; a Claude Code thread on a normal model still uses your account as before
- [ ] Without restarting the app after GET: NEW THREAD > OpenCode lists "⌂ Local coder (this headset, slow)" as the first row
  (LOCAL badge, "on this headset · offline · slow"), above OpenCode's own models; Claude Code the same. Reopen the list once
  if the first look still shows the old list (MQH refreshes it in the background)
- [ ] The same task with OpenCode on that row: the turn runs on the local model (the AI service log shows coder requests);
  Codex does not offer the local coder
- [ ] While a local-coder thread works: TALK shows "⏸ LOCAL CODER WORKING" and does not record; 15 s after the turn ends TALK works again; 72 fps all the time
- [ ] Say "remove the local coder" (confirm): the tile says GET again, the model is gone from the threads' model lists after a restart of MQH

## 39. Stable frame rate: rendering options (branch perf/stable-fps, 0.7.18; docs/perf.md)

- [ ] 72 fps (VrApi FPS, no stale frames) with 6+ windows open, during a long spoken reply, a native build in a terminal and a local-coder turn
- [ ] Text at the edges of the view: sharp while nothing heavy runs; under load it may soften a little (dynamic foveation), never flickers
- [ ] Ambient Focus, real things in front of windows (occlusion) and bent windows look as before (symmetric projection)
- [ ] Move the head and hands fast: windows stay still in the room, the hand ray stays on the fingers (late latching)

## 40. Stone-metal frames and buttons (branch feat/stone-ui)

- [ ] Every window (not the small cards) has carved stone-metal corner pieces with a blue groove and a light node on each cut, slim rails between them, a carved plate with a blue gem over the top centre; the blue only spills inward onto the glass
- [ ] Buttons are dark blue slate plates with an engraved channel: dim blue at rest, bright when a finger or the ray is on them, full for the primary (CD HERE) and red for a danger button; list rows and ghost buttons look as before; text stays easy to read
- [ ] A window opening: the cast's brackets hand over to the stone corners without a jump; bent windows: the pieces bend with them
- [ ] 72 fps with many windows open (the look is computed in the existing UI shader, no textures)

## 41. Wrist menu glass follows PANEL OPACITY; no SHADOWS (branch fix/look-shadows)

- [ ] Settings > LOOK: PANEL OPACITY at 50 % → the wrist menu's tiles and watch face are clearly more see-through than at 100 %; its lines, icons, ticks and sparks look as bright as before
- [ ] PANEL OPACITY at 100 % → the wrist menu looks as it did before this change
- [ ] PANEL OPACITY at 50 %: a hand behind the wrist menu is still hidden by its glass (the depth is unchanged)
- [ ] Change PANEL OPACITY (the slider, or voice: "set the opacity to 60 percent") with the wrist menu open: its glass changes at once, the panels too, no restart
- [ ] Settings > LOOK: the rows are PANEL OPACITY, FOVEATED, SOUNDS, VOLUME, the orb; no SHADOWS slider and no gap where it was
- [ ] Windows and buttons have no dark band round their edges; the accent glow round a window's edge looks as before
- [ ] An old settings file that still has "glow" loads: the app starts with its other settings (PANEL OPACITY keeps its value)

## 42. Projects by type, threads made by their first message (branch fix/projects-threads)

- [ ] THREADS shows NATIVE, DEFAULT, RESEARCH (dim headers, in that order); OTHER only when a project is outside `/root/metacodex-projects/` and has no type; a tap on a header folds it, a tap on a project folds it; threads stay under their project (and its worktree rows)
- [ ] An empty type says "no … project yet: tap to make one"; the tap asks the name of a project of that type
- [ ] + PROJECT… asks NATIVE APP / DEFAULT / RESEARCH / EXISTING…, then the name on the keyboard (empty field, the hint in its caption); "Weather App" makes `/root/metacodex-projects/native/weather-app`
- [ ] In a terminal: `cd /root/metacodex-projects/native/weather-app && git status && git config user.name && cat .metacodex/project.json && ls -la .claude/skills`: a git repository on `main`, an identity (yours when a global one is set, else MetaCodex), type native, the quest-native links
- [ ] A RESEARCH project: AGENTS.md and CLAUDE.md hold the research note (notes in Markdown, sources cited, no app build), no `.claude/skills`; ask its agent "what are your rules here?": it names those rules, nothing about apps
- [ ] The same name again in the same type: a toast says the folder exists; nothing is made
- [ ] Offline, never signed in to git: in a new project ask the agent to commit a file: the commit works
- [ ] An old project with `"type": "general"` in `.metacodex/project.json` shows under DEFAULT
- [ ] NEW THREAD > START, then close THREAD (✕) without a message: no new thread in THREADS, also not after ⟳ or a restart
- [ ] NEW THREAD > START, then open another thread, or NEW THREAD > START again: the first draft is gone, nothing made
- [ ] In the draft: attach a capture (+ or CLIPBOARD > TO THE THREAD) and send the first message: the thread has the text and the capture
- [ ] In the draft: change the model or the access on the chips before the first message: the new thread uses them
- [ ] WORKSPACES > a worktree > NEW THREAD HERE… > START > first message: the thread works in that worktree
- [ ] Voice: "start a thread to fix the login page": a thread is made at once with that message (last agent and model chosen); "new thread": the draft opens and waits
- [ ] Voice: "create a research project called solar panels": made under RESEARCH; "new project": the type dialog; "make a native app project": the name keyboard

## 43. The system keyboard again; our table keyboard removed (branch fix/system-keyboard)

- [ ] A text field (the WEB address, a search, an API key in VAULT): the headset's system keyboard opens with the field's text; what you type shows in the field once (no doubled letters); Done keeps it; Cancel puts the old text back; a secret field shows dots
- [ ] The first time the keyboard opens in a session, a toast: "the headset can lay this keyboard on a table. Settings > Experimental > Surface keyboard"; it does not show again until the app restarts
- [ ] With Settings > Experimental > Surface keyboard on: rest both hands on a table when the keyboard opens; note where Horizon OS shows it (on the table or floating)
- [ ] Terminal: KEYS on the dock or the wrist, the frame's ⌨, the key bar's ⌨: the keyboard opens; type `ls`: the terminal shows `ls` once (not `lsls`), each letter at once; a Backspace deletes one letter; Done runs the command; the same button again closes the keyboard
- [ ] Voice "show the keyboard" opens it; said again, it stays open; "open the table keyboard" opens the same keyboard
- [ ] WEB: tap a text field on a page, ⌨, type, Done: the text is in the page's field once
- [ ] The wrist menu has no TABLE tile; KEYS is lit while the keyboard is open for the terminal
- [ ] A Bluetooth keyboard still types into the terminal and into a focused field; the key bar (Esc, Tab, arrows, Ctrl, Alt) works

## 44. The orb speaks: status answers, thread alerts; "it"; the start splash (fix/fix-list, 2026-11-13)

- [ ] TALK: "give me a status update on the ongoing threads" → the orb says the answer (not only the text); also "how are my threads doing"
- [ ] A thread you are not looking at finishes → the orb says "The thread on <project> is done." with the callout; an approval asked → "An agent on <project> needs your approval."; a question → "... is asking you something."
- [ ] An alert while you talk or while the orb speaks: said after, not over it
- [ ] VOICE > "Say when a thread is done or needs you" off → callouts only, nothing said
- [ ] "close the settings panel", then within 2 minutes "reopen it" → SETTINGS opens again; "close it" closes it; VOICE > LAST shows “reopen it” → “open settings”
- [ ] Ask "what happened while I was away?": no orb placement, focus or menu settings in the answer; threads, approvals, builds are there
- [ ] App start: the MetaCodex logo appears about a metre in front of you over the room (passthrough visible around it), comes in behind a scan line, stays fixed in the room (upright, facing you; turn your head: it stays), then breaks up into light after about 3 s; nothing of it stays


## 45. No stutter while an agent codes (branch perf/gc-cpu)

- [ ] Open a thread with a long conversation and let the agent work for 10 minutes: no stop of 1-3 s, 72 fps (Perf menu Snapshot)
- [ ] The session log: `GC: incremental` at start; no `[slow callback]` line from the chat; no `[slow timeline]` above 40 ms
- [ ] Scrolled up in the chat while it works: the view holds still, ↓ NEW shows; a tap on a header opens and closes it as before
- [ ] An approval row shows and goes: the chat keeps its place and text
- [ ] The sidebar on CHANGES while it works: new files and counts show within a few seconds
- [ ] A script saved in the terminal still applies within about a second
