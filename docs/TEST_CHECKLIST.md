# Headset test checklist (MetaCodex 0.7, 2026-11-08)

Tick each line on the Quest. "→" is what should happen. Items marked **(MQH)** need MQH reachable at
127.0.0.1:38990 from the headset: until MQH runs on the headset (roadmap step 1-2), run it in WSL
(`tools/mqh-dev.sh start`) and forward the port: `adb reverse tcp:38990 tcp:38990` (works over Wi-Fi adb).
With that setup, threads run on the PC, not on the headset; the panels and flows are what you test.

Helpers on the PC: `tools/qterm` (control port), `adb logcat -s Unity`, `files/session_log.txt` on the device.

## 0. Install and first start

- [ ] `adb install -r unity/Builds/MetaCodex.apk` → installs (version 0.3.x)
- [ ] Start from the Library → passthrough, no boundary prompt (boundaryless), the dock in front; the terminal runs hidden (TERM on the dock shows it)
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
- [ ] DISPLAY: distance, carousel radius, UI scale; "Bend panels around you" bends panels in FIXED too (terminals stay flat)
- [ ] FLOAT with 10+ panels open → evenly spaced around you, none overlapping (the circle grows); drop a panel on another → it slides to a free place
- [ ] LOOK: Sharpness, Panel Opacity, Shadows, Foveated rendering, Sounds, Volume (no themes, accents, scanlines or grid)
- [ ] Foveated rendering ON → the edges of the view get softer (VrApi `Fov=`); OFF (default) → sharp everywhere
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
- [ ] LANGUAGE IT, say "apri git" → transcribed in Italian (commands match English phrases; the LLM path may still map it)
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
- [ ] BLUR above 0 → the room outside the dome is a soft image (darker with INTENSITY); at the edges of your view (outside the camera's image) it is just dark; 0 → darkness only
- [ ] Hands always visible, also at BLACKOUT and with BLUR, and in front of a window
- [ ] The cut-out around near things (your hands, a chair, a person) is clean and steady: no flicker, no blocky edge (FOCUS: "camera: live …")
- [ ] Someone walks in front of a window, or you hold an object up → it stays visible in front of the window ("Real things in front of windows" on); off → the window covers it
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
- [ ] MQH > SKILLS: the shipped skills say READ-ONLY and REMOVE refuses them; + NEW SKILL opens NEW THREAD with the brief; ⌕ FIND finds skills, ⇣ INSTALL adds one under YOURS, REMOVE removes it
- [ ] SELECT, then pinch-drag over a window at low PANEL OPACITY: CLIPBOARD has the capture, opaque, nothing of the room or the hands; no hitch at the moment of the capture (72 fps)
- [ ] CLIPBOARD from the wrist (CLIP), the dock and by voice ("open the clipboard", "apri gli appunti"); 21 captures with CLIPBOARD SIZE 20 → the oldest is gone
- [ ] A thread: + → REFERENCE CAPTURE → the list inside the thread panel; pick two → two chips; + → ATTACH FILES → a Linux file; SEND: the agent sees the images and the file
- [ ] Ask by voice, with a thread waiting for an approval: "what is this command I'm asked to accept, is it safe?" → spoken answer naming the thread, the command and its risks, never "it's safe"
- [ ] Ask by voice: "do we have the rust toolchain?", "is this build stuck?" (during a long `cargo build`), "what has this thread done so far?" (pointing at THREAD) → short spoken answers; "open settings" still opens Settings

## 18. After 0.7.6 (owner's feedback): questions, dialogs, captures, the new wrist menu

- [ ] Voice: "how many captures we took so far?" / "quante catture abbiamo fatto?" → a spoken count (and the newest's time); CLIPBOARD does not open. "open the clipboard" and "can I open the clipboard?" still open it
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
