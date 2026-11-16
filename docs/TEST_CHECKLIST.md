# Headset test checklist

Tick each line on the headset. "→" is what should happen.
Helpers on the PC: `tools/qterm` (control port), `adb logcat -s Unity`, and `files/session_log.txt` on the headset.
Frame rate means 72 fps: `adb logcat -s VrApi` shows `FPS=` and stale frames.
A clean first start needs `adb uninstall com.pnncl.metacodex` first.
Commands such as `mcx-ui`, `mcx-app` and `mcx-secret` run in a terminal of the app.

## Install and first start

- [ ] `tools/release/install-metacodex.sh unity/Builds/MetaCodex.apk` with the headset on USB → installs the app (about 1 GB), grants the microphone, spatial data and headset camera permissions, and lets the app install the apps it builds
- [ ] Install with a bare `adb install -r` instead → the microphone, spatial data and headset camera are asked once, together, while the start splash shows; a refused one is asked again by its feature
- [ ] Start from the Library → passthrough, no boundary prompt (boundaryless), the DOCK in front; the terminal runs hidden (TERMS on the menu lists it, a tap shows it)
- [ ] `mcx-ui status` inside the terminal → host=Ready, fps, terminals=1
- [ ] Right after the first start, in the first terminal: `git --version`, `node --version`, `python3 --version`, `curl --version` → all answer (they ship in the app, nothing was downloaded)

First start (clean install, then start):

- [ ] The splash shows at once and animates, with no freeze → the logo about a metre in front of you over the room (passthrough visible around it) comes in behind a scan line, stays fixed in the room (upright, facing you; turn your head: it stays), then breaks up into light once the shell is built; nothing of it stays
- [ ] The guide card opens about 0.3 s after the splash, in front of you (0.4 m wide, as tall as its text) → it never touches the DOCK, the Whisp or an open window, and moves aside smoothly if one comes near
- [ ] The setup strip on top of the card → a spinner while a step runs, the step in plain words (unpacking the models, starting the local AI, unpacking the Linux workstation, starting MQH, teaching the voice your commands), STEP 1 OF 5 … 5 OF 5, one bar with a percentage that never goes back, and "One-time setup: only on this first start, nothing is downloaded"
- [ ] Turn Wi-Fi off before the first start → the setup finishes the same (nothing is downloaded)
- [ ] `ui setup` on the control port, and `setup: all steps over in N s` in `files/session_log.txt` → the five steps with their times; the Linux unpack is the longest step (about 9-12 s); the whole setup takes well under a minute
- [ ] Tap TALK on the DOCK (or the wrist menu, or the Whisp's menu) during the setup → one callout "🔒 TALK unlocks by itself in a few seconds (one-time setup, step 5 of 5)", no listening, no error; tapping again at once gives no second callout
- [ ] THREADS, TERMS and FILES tiles show 🔒 dimmed; tapped → their callout (step 4 or 3); CONTROL > AGENTS is dimmed with "🔒 step 4" and does not open; CONTROL, WEB and CLIP work
- [ ] The locks lift by themselves → TERMS and FILES when the Linux is ready, THREADS and AGENTS when MQH runs, TALK at the end (then TALK listens); a step still not over 5 minutes after the setup began is skipped and its lock lifts anyway
- [ ] The end of the setup → "✓ Setup done in N s: everything is unlocked" for a few seconds, then the strip folds away and the tour goes on in the same card
- [ ] The tour has six beats (HELLO, THE MAIN MENU, THE WHISP, WINDOWS, CONTROL, YOUR TURN; five when the Whisp is off) → each asks for one action and says ✓ when it is done: NEXT ▸ (or by itself after 12 s), open CLIP on the DOCK (or the wrist menu), open the Whisp's menu, move the card by its bar (pinch) or with a closed hand, open CONTROL, then press TALK and say "open files" (the card shows what it heard)
- [ ] A ring of 12 warm dots round the target (CLIP or the wrist, the Whisp, CONTROL, TALK) → it is visible, not hidden by the DOCK, and breathes slowly; the card's frame breathes on the WINDOWS beat; the card sits beside its target and comes back if you turn away
- [ ] SKIP STEP moves on, SKIP TOUR ends the tour → the tour never asks for a 🔒 feature; while the setup runs the card has no ✕ (SKIP TOUR leaves the strip and says how to replay the tour); afterwards ✕ ends the tour
- [ ] The last beat after the setup → no "when the setup is done" text; it says what TALK heard
- [ ] Frame rate during the setup and the tour → steady (no stall over 50 ms; `[hitch]` lines in `files/session_log.txt`)
- [ ] Quit and start again → no card, no tour, nothing locked; CONTROL > ABOUT > TOUR (or the voice command "start the tour") plays the tour again, without the strip

## Performance and stability

- [ ] The shell idle (DOCK, the Whisp, one terminal) → about 72 fps (`FPS=` in `adb logcat -s VrApi`, or `fps=` in `mcx-ui status`)
- [ ] All panels open (6 or more) + FLOAT layout + 2 terminals → still about 72 fps; no stutter when gestures run
- [ ] 6+ windows open, during a long spoken reply, a native build in a terminal and a local coder turn → 72 fps (VrApi FPS, no stale frames)
- [ ] Five windows open → 72 fps; open three windows at once and close them → no drop below 72 fps; the same while a window opens or closes
- [ ] Hover and press buttons in a big window (MQH, SETTINGS) → no stutter; the light on a button still follows the hover and the click
- [ ] A long `apt install` or `npm install` in a terminal (run it with `nice -n 10`) → the app stays smooth
- [ ] Take the headset off and on, reopen the app → terminals and panels come back
- [ ] The headset's Apps settings > MetaCodex → a running service / notification "Local services running" (the AI service is a foreground service)
- [ ] Text at the edges of the view → sharp while nothing heavy runs; under load it may soften a little (dynamic foveation), never flickers
- [ ] Ambient Focus, real things in front of windows and bent windows → all look right with the rendering options (symmetric projection)
- [ ] Move the head and the hands fast → windows stay still in the room, the hand ray stays on the fingers (late latching)
- [ ] Open a thread with a long conversation and let the agent work for 10 minutes → no stop of 1-3 s, 72 fps
- [ ] `files/session_log.txt` after that → `GC: incremental` at start; no `[slow callback]` line from the chat; no `[slow timeline]` above 40 ms

## Main menu: DOCK and wrist menu

The DOCK (the default main menu):

- [ ] The DOCK has 11 tiles in two rows, TALK TERMS FILES THREADS WEB CONTROL / CLIP FOCUS SELECT KEYS CENTER, with TALK highlighted; a grab bar on top: pinch it and move → the DOCK moves
- [ ] Open FILES, THREADS, WEB, TERMS, CLIP and CONTROL from it, then close them again → each tile opens and closes its window, stays lit while the window is open and goes dark within a quarter second after it closes; FOCUS and SELECT are lit while on; KEYS while the terminal keyboard is up (also on the wrist); TALK while listening
- [ ] Watch the DOCK open for a minute → 72 fps, no hitch every quarter second

The wrist menu (CONTROL > DISPLAY > Main menu on the wrist):

- [ ] The wrist menu has the same 11 tiles: 6 inner (TALK, TERMS, FILES, THREADS, WEB, CONTROL) and 5 outer (CLIP, FOCUS, SELECT, KEYS, CENTER) → easy to hit, nothing missing that you use daily
- [ ] Switch the setting on → the DOCK stays until the wrist menu is ready (no freeze), then goes; look at the back of your left wrist: the core appears; touch it with the right index finger: the rings unfold; touch FILES and TALK; look away: it folds and hides; switch the setting off again: the DOCK comes back
- [ ] Hold the left wrist as a watch → the rings stay whole, the hand behind them never cuts them; the right fingertip shows over a blade while pressing; windows still let hands and things in front show
- [ ] Look at the back of the left wrist → the watch face (the time, ticks, a warm seconds point) builds up with the projector rays from the wrist; touch it: a shock ring runs out, the inner tiles swing in one way and the outer the other, icons draw with a scan line, names type in; nothing pops in or out at once
- [ ] The left side: press FOCUS, CENTER, KEYS, SELECT with the right hand reaching over the left → the menu stays where it is and stays open, even when the left hand is lost for a moment; it does not run off
- [ ] Slide the fingertip across the face on the way to the left tiles → nothing is pressed, the menu stays unfolded; a straight poke into a tile runs it once
- [ ] Move the left arm around while looking at it → it stays on the wrist, no jitter when still, no lag when moving; turn the wrist away: it waits a moment, folds and fades; turn it back while it fades: it comes back from where it was
- [ ] Switch the wrist menu off in CONTROL > DISPLAY while it is open → it folds and fades away, the DOCK comes back
- [ ] The wrist menu open → about 72 fps
- [ ] GLASS (CONTROL > WRIST MENU) at 100 % → the wrist menu glass is solid; at 50 % the tiles and the watch face are clearly more see-through, while their lines, icons, ticks and sparks stay as bright; a hand behind the menu is still hidden by its glass
- [ ] Change PANEL OPACITY with the wrist menu open → the panels change, the wrist menu's glass does not (it has its own GLASS)
- [ ] Look at the back of the left wrist → the battery (outline, fill, percentage) and the Wi-Fi bars sit in one row above the time, well inside the core's glass and readable at arm's length; unfold: every tile's name is easy to read and none runs over its tile's edge

CONTROL > WRIST MENU (◔ in THIS HEADSET):

- [ ] Open it from the rail, or say "open the wrist menu settings" → a line says how to open the wrist menu; on the right a copy of the wrist menu stands unfolded on the page; on the left the tiles off the menu, in SYSTEM and APPS; no MOVE / HIDE buttons; the menu on your wrist does not change while you edit
- [ ] Point the ray at a tile of the copy → it lights and its name shows in the copy's core
- [ ] With the ray, pinch FOCUS on the copy and drag it round the outer ring → a click on pick-up; the tile follows the ray; an empty lit slot shows where it lands and the other tiles move aside; the core says OUTER; let go → it lands there (a soft ping); the bottom line says the change is not on your wrist yet
- [ ] Drag a tile from the outer ring to the inner ring (the core says INNER), and one from the inner ring to the outer → each lands where the slot showed it
- [ ] Drag a tile off the copy (past its edge, or onto the tiles on the left) → the core says TAKE OFF; let go → it is in SYSTEM (or APPS) on the left
- [ ] Drag CONTROL off the copy → the tile turns to the warning tone and the core says STAYS; let go → it goes back to its place and a message says why; an inner ring of 3 (dragging one out) or 8 (dragging one in), or a full outer ring of 12, refuses the same way with NO ROOM
- [ ] Drag a tile from SYSTEM or APPS onto the copy → it lands at the slot under the ray; a tap on a tile there instead adds it at the end of the outer ring
- [ ] With the Timers example installed, drag TIMER off the copy → it is in APPS; turn the plugin off in MQH > PLUGINS → TIMERS shows dimmed in APPS, a tap says to turn it on there
- [ ] Tap TIMER on the copy (pinch and let go without moving) → it is chosen: ICON ◂ ▸ steps through the generic icons on the copy; PLUGIN'S gives the timer icon back
- [ ] Lose the hand while dragging (put it down) → the drag ends, the copy shows the layout as before
- [ ] Move MENU SIZE, TEXT SIZE and GLASS; switch BATTERY, NETWORK, CLOCK and "Fold the rings after a tap" → the copy follows (the names once the TEXT SIZE slider rests); at the largest TEXT SIZE long names shrink to fit their tile
- [ ] CANCEL → the copy is your wrist menu again; make a change and close the page, open it again → the change is gone
- [ ] Make changes and press APPLY → your wrist menu is as on the copy (rings, size, names, glass, core); with "Main menu on the wrist" off the DOCK shows the same order without the hidden tiles
- [ ] RESET LAYOUT, then APPLY → the default 6 + 5 tiles come back
- [ ] Voice: "make the wrist menu bigger", "set the wrist menu glass to 60 percent", "hide the battery on my wrist", "turn off the clock on my wrist" → the wrist menu changes at once; "turn off the wrist menu" still switches to the DOCK
- [ ] The page open with its copy → about 72 fps; close it → the copy folds away with the page

## Windows, placement and layouts

Where windows open:

- [ ] Open a window with nothing else open (DOCK, wrist menu or voice) → it opens where you look, at the panel distance (CONTROL > DISPLAY > DISTANCE), its middle just below eye level, turned to you
- [ ] Open more windows → each opens beside the others, none over another; none sits on or under the DOCK (it keeps 3 degrees off the DOCK in your view, and its middle is never lower than the DOCK's top edge)
- [ ] Open a window from another window (a thread from THREADS, a picker, a details window; VOICE from SYSTEM > STORAGE > AI MODELS) → it opens right beside that window, on its right when there is room, also when you look elsewhere; a window of a thread group counts as its group
- [ ] A dialog from a window (CONTROL > MQH > SKILLS > REMOVE, a thread's dialogs) → it opens over that window, parallel to it; a dialog asked by voice opens in front of you
- [ ] With a terminal open, press TERMS on the menu; with a thread open, press CLIP → TERMS opens right beside the terminal, CLIP beside the thread
- [ ] Close a window you had dragged somewhere and open it again → it is back there; put another window in that place first: it opens beside instead; turn right round and open it: it opens in front of you
- [ ] Look down at the wrist menu held to your left and press FILES → it opens in front of your body, not off to the left
- [ ] Open many windows (10 or more) → when no place is free in view, the next ones open a little further away (up to twice the distance, smaller in view); when even that is full, at the nearest free place round you (at most 100 degrees to a side, never behind you), and the Whisp points to it; only when nothing is free anywhere does one open in front of you over the others, a little nearer each time
- [ ] UI SCALE 0.8 and DISTANCE 1.3 m, open six windows → none overlaps another; one goes above the row (tilted towards you) rather than far to the side; none goes below the DOCK
- [ ] Stand 70 cm from a wall and open a window (depth on: CONTROL > FOCUS > Real things in front) → it opens at least 8 cm in front of the wall, not inside it
- [ ] CENTER (⟲, wrist or DOCK) → the layout turns round you as one piece (the terminal and the DOCK are in front of you again); only a window that would cover another one moves

Layouts:

- [ ] CONTROL > DISPLAY > LAYOUT FLOAT → windows arrange on a circle around you, side windows bend, the terminal stays flat
- [ ] Walk a few steps → the circle follows horizontally; nodding or looking down does not move windows up or down
- [ ] Fist in empty space + swipe sideways → the carousel spins and slows down smoothly
- [ ] FLOAT with 10+ windows open → evenly spaced around you, none overlapping (the circle grows); drop a window on another → it slides to a free place
- [ ] LAYOUT FIXED → windows stay world-locked, flat
- [ ] SAVE LAYOUT… "desk", move and close windows, turn around, LOAD NEXT LAYOUT → windows come back around you

The look of a window:

- [ ] Open any window (not a small card) → it has carved stone-metal corner pieces with a blue groove and a light node on each cut, slim rails between them, and a carved plate with a blue gem over the top centre; the blue only spills inward onto the glass; windows and buttons have no dark band round their edges, and the accent glow round a window's edge is still there
- [ ] The glass is dark with a faint hex lattice; titles and buttons are in capitals; sliders and progress bars are segmented energy bars → nothing looks washed out or neutral
- [ ] Buttons are dark blue slate plates with an engraved channel → dim blue at rest, bright when a finger or the ray is on them, full for the primary one (CD HERE) and red for a danger button; list rows and ghost buttons stay flat; text stays easy to read
- [ ] The honeycomb and the points of the glass are on the surface → when you move your head sideways, nothing shifts inside the glass
- [ ] Bring a fingertip slowly towards a window → a pool of light forms on the lattice under it and tightens as you come closer; the ray's cursor carries a light too
- [ ] Lists (MQH > GESTURES, FILES, THREADS) → quiet rows; the chosen row floods with cyan from the left; hovered rows and buttons show a light crossing them
- [ ] Headers → a bright block before the title, the line under it steps up two thirds along, a pulse runs in it; a ruler down the left edge of large windows; a thin conduit along the bottom right with a pulse
- [ ] Look at a small card (a message, the voice HUD) and at a window or dialog → the card has no corner brackets; windows and dialogs do

Opening and closing (a window opens in 0.35 s and closes in 0.22 s):

- [ ] Open a window (DOCK, wrist menu or voice) → brackets lock on where it will be, with a faint outline; two thin rays run from the Whisp to a warm-white line that sweeps across, and the window is built behind the line (a honeycomb whose cells close and cool into glass, then the frame, buttons and text, hot for a moment); there is no filled fan of light; it ends exactly at rest, no pop
- [ ] Close it → the line runs back and takes the window with it, a spark flies home to the Whisp, the brackets let go; nothing is left behind
- [ ] Open and close quickly several times (a DOCK tile twice fast) → it reverses from where it is, never jumps, never stays half open
- [ ] A terminal (TERMS, hide with –) and a 3D view are built and folded with their frame → their text and background appear behind the line, nothing shows before it
- [ ] FILES, then FILES again from the DOCK while it is open → it pings (a flash, the brackets close in round it), it is not cast again
- [ ] Open a dialog (CLIPBOARD > CLEAR ALL) and a callout → the dialog comes and goes with the transition; callouts, the voice card and the Whisp's menu squares have no brackets, open in 0.22 s and close in 0.12 s
- [ ] Bent windows (bending on) → the brackets and lines follow the curve

Opacity and bending:

- [ ] CONTROL > LOOK & SOUND: PANEL OPACITY at 100 % → no part of a window is see-through: buttons, rows, tiles, the header
- [ ] Put one window in front of another at 100 % → no text, icon or button of the window behind shows through
- [ ] Around a window's edge and where it overlaps another window → no see-through line (fissure)
- [ ] PANEL OPACITY below 75 % → windows behind show through faintly (as intended)
- [ ] Windows are flat by default; CONTROL > DISPLAY > Bend panels around you on, in FIXED layout: walk closer and farther, drag a window → it curves smoothly (no steps or jumps); terminals stay flat
- [ ] Point at a bent window's edges → the reticle sits on the curved surface, buttons near the edges react where the beam meets them; pokes with the fingertip work on bent windows
- [ ] A text that changes fast (a working thread) on a bent window → it stays on the curve, never flashes flat

Back from a native app (open the DOCK, THREAD and one more window in FIXED layout first; note where they are against a wall, the desk or a door):

- [ ] Launch a native app from MetaCodex (LAUNCH), use it, close it with "Back to MetaCodex" → every panel is in the same place in the room (a few cm)
- [ ] Same, but turn your head 90 degrees before coming back → the panels still stay in the room
- [ ] `files/session_log.txt` → one line `room anchor: back in the room after the native app: panels moved X cm (most Y cm), turned Z deg, N open`; write X, Y, Z down (X near 0 while the panels still moved: the cause is not the room origin; send the log)
- [ ] Look for `room anchor: ... too far` or `no room reference` in that log → neither shows (if one does, the stage origin does not hold here: send the log; see `docs/ui.md`)
- [ ] Pause and resume with the system Home button, no native app → the panels stay where they were, no line about a move

## Hands, gestures and scripts

Hands:

- [ ] Hover over a slider without pinching → it does not move; pinch and drag → it moves
- [ ] Point a ray at a button and pinch → the button clicks; a fingertip poke on a window does nothing
- [ ] Pinch a window header and move → the window follows; release → it stays (fixed layout)
- [ ] Pinch 2-3 cm above a window's title bar (a near miss) → the window moves as if you pinched the bar; a pinch on another window just above it still clicks that window
- [ ] Close the hand (fist) while pointing at a window → nothing moves
- [ ] SELECT on → pinch-drag a rectangle (also over a window) → a dashed rectangle, "Selected … cm", and the tool turns itself off; SELECT on, then SELECT again (DOCK or CONTROL) → off
- [ ] Palm towards you + pinch (the system quick actions, left-hand settings) → the windows stay where they are (the gesture hides nothing) and the quick-actions menu shows over them; when a system menu has opened and taken the app's focus, the windows step aside and come back when it closes
- [ ] Make a V (index and middle up) for about 1 s and drop it → nothing happens
- [ ] Hold the V for 2 s → Ambient Focus turns on; hold it again for 2 s → it turns off; BIND lists `victory` → FOCUS
- [ ] Hold horns, thumbs up or call-me for 3 s → nothing opens
- [ ] Make a V while the ray or the fingertip is on a window → nothing happens

Gesture studio (CONTROL > MQH > GESTURES):

- [ ] GESTURES > ALL on a fresh start → YOURS (0) with one dim line "none · RECORD a pose or ASK AN AGENT"; SYSTEM (7: pinch click, grab move · spin, point ray, open open hand, palm-up palm up, clap two hands, select select tool); ▸ EXAMPLES (4) folded; no ⟳ REFRESH button
- [ ] Tap EXAMPLES → it opens (▾ EXAMPLES) with `core-poses`, `swipes`, `timers` and `calendar`; `core-poses` already says ADDED (the V shortcut uses it), the others say + ADD; tap it again: it folds
- [ ] Pick a SYSTEM gesture → TEST ▶ and BIND work; SOURCE and DELETE POSE are greyed out and do nothing
- [ ] Pick `swipes` under EXAMPLES → the right side lists its gestures and says "Not installed"; + ADD (the only lit button) → toast "Example swipes added …", the row says ADDED and its gestures appear indented under it; YOURS stays (0)
- [ ] Swipe an open hand fast left / right after + ADD → swipe-left / swipe-right in LIVE's recent events
- [ ] LIVE tab → finger bars move with your fingers (1 = straight); palm and thumb direction text; active gestures listed; the detector cost (right column) stays well under 1 ms
- [ ] Make a thumbs up → LIVE shows `thumbs-up` (and `grab`); the GESTURES list marks it ● ACTIVE; CONTROL > MQH > PLUGINS lists Core poses
- [ ] Thumbs down, victory (✌), horns (🤘), call-me (🤙) → each is recognised (LIVE, or a toast via a binding)
- [ ] TEST ▶ on any gesture → a simulated event (toast "Simulated …")
- [ ] BIND: pick `thumbs-up`, action "GIT", + ADD BINDING; then hold a thumbs up for 2 s → GIT opens; the binding can be switched on and off and deleted
- [ ] BIND presets, in this order: TALK, TERMINALS, NEW TERMINAL, THREADS, FILES, WEB, GIT, KEYBOARD, SELECT, RECENTER, FOCUS, CTRL-C; bind `clap` to WEB → a clap opens the WEB window (not LINKS); bind `clap` to TALK → a clap starts listening, another stops; DELETE the bindings
- [ ] A binding saved with an old preset (for example `toggle:browser` or `spin:40`) → still runs
- [ ] BIND `palm-up` (left hand) to `voice:toggle` → hands-free TALK
- [ ] RECORD: name "finger-gun", RIGHT, hold a finger gun, RECORD IN 3 s → "saved" + summary; the pose then fires; it appears under YOURS (✋ pose · my-poses); SOURCE and DELETE POSE are lit for it; DELETE POSE asks, then removes it
- [ ] RECORD's right side → one dim line "New gestures of your own: MQH > PLUGINS > + NEW PLUGIN > GESTURE, then a thread on it asks an agent"; no + NEW PLUGIN… and no OPEN IN TERMINAL there
- [ ] `mcx-ui plugin examples` → lists all four with installed=True/False and their kind; `mcx-ui plugin example swipes` → `ok, /root/metacodex-projects/plugins/gestures/swipes`; swipes appear under EXAMPLES (ADDED) and THREADS > PLUGINS
- [ ] `mcx-ui gesture list`, `mcx-ui gesture bind clap toggle:files`, `mcx-ui action open:git` → each answers; the binding shows in BIND
- [ ] Ask an agent on the headset (`claude` or `opencode` in a terminal): "make me a gesture plugin: when I do a thumbs up with my right hand open git; push my palm away to close it" → the plugin appears with its own `poses/thumbs-up.json` and works on a fresh install, without + ADD
- [ ] Update over an old install that had the starters, never edited, with no binding on them → after the first start `swipes` is gone from CONTROL > MQH > PLUGINS and from `/mnt/quest/plugins` (`ls -a` shows `.examples-v2` and still `.starter-installed`); `core-poses` stays (the V shortcut uses it); `my-poses` and your own plugins are still there
- [ ] Update over an old install where a binding names `victory` (or a script or plugin reacts to `thumbs-up`) → `core-poses` stays under EXAMPLES, still working; an edited starter (one pose file changed) stays and shows under YOURS
- [ ] Restart the app after + ADD of an example → it is still installed (the cleanup runs only once)

Lua scripts (CONTROL > MQH > SCRIPTS):

- [ ] SCRIPTS on a fresh start → one dim line "No scripts · + EXAMPLE, or ask an agent (skill quest-scripting)"; no plugin rows, no GESTURE MONITOR; + EXAMPLE → `double-clap` with the badge ON; double clap toggles TERMINALS
- [ ] Edit `/mnt/quest/scripts/double-clap.lua` in the terminal (nano) → saved changes apply within a second, also while an agent works
- [ ] A broken script (syntax error) → ERROR in the list, toast; the others keep working
- [ ] DISABLE / ENABLE / DELETE a script → the state survives a restart
- [ ] `mcx-ui script list`, `mcx-ui script eval 'return 1+1'` → 2

## Plugins (projects with a fixed kind)

- [ ] THREADS > + PROJECT… → NATIVE APP · DEFAULT · RESEARCH · PLUGIN · EXISTING…; PLUGIN → APP · GESTURE · BEHAVIOUR; APP, name "Test app" → a toast; THREADS shows it under PLUGINS (…/plugins/apps/test-app); a TEST-APP tile is on the menu and opens a window with +1 and RESET; the count stays after a restart
- [ ] CONTROL > MQH > PLUGINS > SHOW GESTURES > + NEW PLUGIN… → straight to the name (no kind question); "Test gesture" → it is listed under GESTURES with the kind "gesture"; both palms up shows the toast "Both palms up" and opens nothing; SHOW ALL > + NEW PLUGIN… asks the kind
- [ ] In the Linux: `ls -la /root/metacodex-projects/plugins/apps/test-app/.metacodex` → `project.json` is `-r--r--r--`; `cd` there, `git log --oneline` → one commit; `mcx-project type .` → plugins, `mcx-project kind .` → app; `mcx-project set . default` → refused
- [ ] Edit `"kind": "gesture"` into that project's `plugin.json` → it stays under APPS; `mcx-ui plugin check test-app` → `kind: app (fixed …)` and a warning about the kind
- [ ] `mkdir -p /root/metacodex-projects/plugins/apps/bare && cp /root/metacodex-projects/plugins/apps/test-app/{plugin.json,main.lua} /root/metacodex-projects/plugins/apps/bare/` → `bare` is not listed; `mcx-ui plugin check bare` says "not loaded: no marker"
- [ ] An older plugin folder in `/mnt/quest/plugins` (for example core-poses, made by the V shortcut) → still listed, its row says "older folder", NEW THREAD ▸ is dim
- [ ] Pick the project plugin → THREADS under its detail says "No threads yet"; NEW THREAD ▸ → NEW THREAD with that project chosen; START a thread and ask "add a RESET ALL button" → the thread shows in the plugin's THREADS list (newest first, agent, status, last activity); a tap on it opens the thread in its window; ▤ ARCHIVED shows its archived threads
- [ ] No ASK AN AGENT and no FOLDER button in MQH > PLUGINS
- [ ] CONTROL > MQH > PLUGINS: pick a plugin → the first button says DISABLE when it is on and ENABLE when it is off (dim with nothing picked); enable / disable / reload work; the detail shows its kind, folder, poses, detectors and log
- [ ] DELETE a project plugin → a dialog says the folder, its git history and its project with its threads go; DELETE → it leaves the list, THREADS and `/root/metacodex-projects/plugins/apps/`
- [ ] `mcx-project new plugins --kind app "Voice made"` in a terminal → the app makes it (a toast; listed under APPS; in THREADS)
- [ ] Say "make me an app plugin called water log" → made at once; "create a gesture plugin" → the name keyboard; "add a plugin" → the kind dialog
- [ ] `mcx-ui plugin list` → the plugins with their state
- [ ] A plugin with `voice.command{...}` (skill quest-scripting) → its phrase works by voice; `mcx-ui voice match "<phrase>"` shows the command
- [ ] Ask an agent for a small plugin with its own window and a menu button (skill `quest-plugins`) → the window and its tile appear without a rebuild or a restart, and load again at every start
- [ ] CONTROL > MQH > PLUGINS (no PLUGINS row of its own on the rail) → SHOW ALL lists APPS, GESTURES, BEHAVIOURS, MQH and EXAMPLES TO ADD; SHOW APPS lists only apps and the timers / calendar examples; GESTURES > ALL > EXAMPLES lists only gesture examples (core-poses, swipes)
- [ ] Install the Timers example (CONTROL > MQH > PLUGINS > timers > + ADD, or `mcx-ui plugin example timers`) → a toast; a TIMER tile appears on the DOCK (and on the wrist menu's outer ring in WRIST mode); the tile opens the Timers window; THREADS > PLUGINS lists Timers (…/plugins/apps/timers, with AGENTS.md and one commit)
- [ ] In the Timers window, start a short timer with its buttons → the countdown runs; when it ends, the Whisp calls it out with a sound and says it; a tap on the callout opens the window
- [ ] Install the Calendar example the same way → a CALENDAR tile and a window with a month grid; add an entry and a reminder → the reminder calls out at its time
- [ ] Restart the app → both plugins load again, their windows and tiles come back, and their saved timers and entries are kept
- [ ] DISABLE a plugin in CONTROL > MQH > PLUGINS → its window closes, its DOCK tile and its voice commands go away; ENABLE brings them back
- [ ] `mcx-ui plugin check timers` → `loaded`, with its kind (app, fixed), folder, windows (builds, open), tiles, voice commands, timers and no warnings
- [ ] `mcx-ui plugin event timers button start` → ok, the countdown starts in the window; `mcx-ui plugin event nosuch button x` → an error naming the window

## The Whisp

Look and states:

- [ ] Start the app → a small orb of light appears up to the right of where you face, about half a metre away: a white-hot core, plasma turning inside, three thin rings, a halo
- [ ] Idle → calm cyan, it breathes, its light shimmers slowly
- [ ] Listening starts (TALK) → a flash of ice white and a ripple; then it is gold, its rings face you, the light follows your voice
- [ ] It thinks → violet, the plasma and the rings spin up
- [ ] It speaks → the light pulses with every syllable and ripples leave it; a spoken answer comes from its direction
- [ ] A thread waits for your approval or an answer → an amber heartbeat, until you open its menu or the LOG
- [ ] The first start's setup keeps the voice locked → it is dim steel and slow; it wakes when the voice is ready
- [ ] Messages you have not read → up to four of its motes turn gold
- [ ] A window has to open out of view → the Whisp's rings turn to the window for about 2.5 s
- [ ] The Whisp for a minute (idle, touch it for its menu, move round it, put a hand near and behind it) → no flat blocks, stripes or half discs on it, in any frame

Place:

- [ ] Glance left and right → it stays where it is in the room; turn your chair a quarter turn: after a moment it comes along round you, never through your face; walk two steps: it comes along
- [ ] Idle, not looked at, no message → it is a little smaller; look at it or bring a hand close: it brightens and grows back; touching it still opens the menu
- [ ] Open windows until one would be behind it → it moves to a free place; close that window: a few seconds later it goes back; no window covers its menu or its messages while they show, nor the Whisp while it listens, thinks or speaks
- [ ] Pinch the Whisp between thumb and index and move the hand → it follows; let go: it stays there and keeps that place by you when you turn; quit and start again (MQH sync on): it is where you left it; menu > PLACE, restart: it chooses its own place
- [ ] CONTROL > LOOK & SOUND > Whisp off (or voice: "turn the Whisp off") → it fades out; windows are cast from their left edge; messages float in the upper view; on again: it comes back
- [ ] Frame rate with the Whisp and five windows → 72 fps; bring it close to your face (pinch it there): still 72 fps; with its menu open and in view: 72 fps

Casting and messages:

- [ ] It casts the windows → two thin rays run from the Whisp to the window's nearest edge and ride on the scan line; a window to the left of the Whisp is built from its right edge, one below it from its top; closing: a spark flies home to the Whisp
- [ ] Any message (SELECT on and off gives two) → a card beside the Whisp, towards the middle of your view, joined to it by a thin rail; a second message takes that place and the first moves up; the Whisp flashes in the message's colour (green ok, amber warning, red error); messages stack on an arc round its right side, the newest lowest, the line of light runs along the arc; never on its left
- [ ] A thread you are not looking at finishes → the Whisp says "The thread on <project> is done." with the callout; an approval asked: "An agent on <project> needs your approval."; a question: "… is asking you something."
- [ ] An alert comes while you talk or while the Whisp speaks → it is said after, not over it
- [ ] VOICE > "Say when a thread is done or needs you" off → callouts only, nothing said
- [ ] TALK: "give me a status update on the ongoing threads", "how are my threads doing" → the Whisp says the answer, not only the text

Its menu:

- [ ] Touch the Whisp with a fingertip → its menu opens beside it: four plain squares on an arc round its left side, TALK, SPEAK, PLACE, LOG from the top, nothing else beside them; the gold shards go; touch it again: it closes; point at it with a ray and pinch: the same; the window behind it is not clicked
- [ ] The squares have no frame (no outline box, no shadow box) → each is a square with its label; the gap between two neighbouring squares is the same all round the arc (about 8 mm at the menu's size); no square touches the Whisp; every square faces you; the labels read at arm's length without leaning in
- [ ] The menu always sits on the Whisp's left, level with it, whichever way you look → never swapped; messages always rise on its right
- [ ] Each square works → TALK starts and stops listening, SPEAK toggles spoken answers, PLACE makes the Whisp choose its own place again, LOG opens LOG · MESSAGES (`✉ n NEW` while messages are unread)
- [ ] LOG · MESSAGES → the list; CLEAR ALL (red, as big as other buttons) asks "Clear all n messages?"; CANCEL keeps them, CLEAR ALL empties the list
- [ ] The menu opens and closes with the light → a line of light crosses each square (there is no frame to light)
- [ ] Read a window straight ahead, the Whisp up to its right, and touch the Whisp → the Whisp and its menu glide (no jump) to a place where nothing of them covers that window, still in view and reachable; close the menu: a moment later the Whisp goes back to its own place
- [ ] While the menu is up, reach for a square → nothing moves under the finger; look at the menu itself: the Whisp does not run away from it

## CONTROL and settings

CONTROL:

- [ ] Open CONTROL → one window with the rail on its left: THIS HEADSET (◧ DISPLAY, ◔ WRIST MENU, ◈ LOOK & SOUND, ◐ FOCUS, ✋ HANDS), AI (♫ VOICE, ◆ AGENTS, ⬡ MQH, ⚿ VAULT), LINUX & APP (⬢ SYSTEM, ✦ ABOUT); each row has its status (fixed, sound, on / off, ready, running, keys); the names are in the same narrow capitals as the group captions; nothing is cut off ("LOOK & SOUND" whole)
- [ ] Tap WRIST MENU, AGENTS, MQH, VAULT, VOICE, SYSTEM in the rail → each takes the same place, the rail stays still; DISPLAY, LOOK & SOUND, FOCUS, HANDS and ABOUT only change SETTINGS' page
- [ ] Click every category on the rail one after another → every window has the same size, the top-left corner and the rail never move, no window grows over a neighbour
- [ ] Drag the window by its header → the rail comes along; pinch the rail's bar and move it: the window comes along
- [ ] Press ✕ on the CONTROL window → the rail closes too; CONTROL on the menu brings back the last category; pressed again it closes
- [ ] FLOAT layout, CONTROL open → CONTROL and its rail take one place on the ring, nothing overlaps them
- [ ] With no agent signed in, look at the rail → AGENTS' status says "sign in" (not "set up")
- [ ] Voice: "open the control panel", "open agents", "open git", "open the settings" → the right page opens with the rail
- [ ] Voice or a binding with `ui.open("display")`, "look", "hands" or "about" (for example `mcx-ui action open:about` from a terminal) → CONTROL opens on that page; close it; CONTROL on the menu brings that page back
- [ ] THREADS > ◆ AGENTS → CONTROL opens on AGENTS

Pages:

- [ ] Change the DISPLAY rows (LAYOUT, DISTANCE, FLOAT RADIUS = the carousel radius, UI SCALE) → each takes effect at once; "Bend panels around you" bends windows in FIXED too (terminals stay flat); "Main menu on the wrist" switches between the DOCK and the wrist menu
- [ ] DISPLAY in FIXED → FLOAT RADIUS is dimmed with "FLOAT only" under its name; switch LAYOUT to FLOAT: FLOAT RADIUS is bright and DISTANCE is dimmed with "FIXED only"
- [ ] Open and close windows for a while, change a slider, say a voice command → no window changes size by itself (they keep the UI SCALE)
- [ ] LOOK & SOUND rows → PANEL OPACITY, FOVEATED, SOUNDS, VOLUME and the Whisp toggle ("Whisp: it casts the windows, speaks, and shows messages beside it"); no SHARPNESS or SHADOWS slider, no themes, accents, scanlines or grid, and no gap where a slider was; text is sharp (render scale 1.6)
- [ ] LOOK & SOUND > SOUND: HUD (default), HOLO, CORE, CLASSIC → a ping in the new style within a second; clicks, window casts, toasts and the Whisp use it; the choice stays after a restart; voice "use the hologram sounds" picks HOLO
- [ ] FOVEATED on → the edges of the view get softer (VrApi `Fov=`); off (the default): sharp everywhere
- [ ] SOUNDS → click, open, close and toast blips play; off: silent; VOLUME changes how loud they are
- [ ] A settings file that still has "glow" loads → the app starts with its other settings (PANEL OPACITY keeps its value)
- [ ] HANDS → no CLICK or MOVE PANELS choice (pinch only); POINT RAY, the hand rays, PINCH AT and the clap are there
- [ ] HANDS: POINT RAY ALWAYS / POINTING POSE → the ray shows only when you point
- [ ] HANDS: change the PINCH AT slider, clap on / off, clap distance, and "Custom gestures from plugins" on / off → each changes how the hands react
- [ ] HANDS: look at the button row → it reads SELECT AREA ⌖ | GESTURE STUDIO ▸ | the system's hand settings ⚙; the line at the top ends "GESTURE STUDIO: your own gestures."; GESTURE STUDIO opens MQH on GESTURES in HANDS' place; the third button opens the headset's own hand settings page
- [ ] ABOUT → no raw log lines; LICENCES ▸ opens FILES at `/usr/share/doc/metacodex` (`LICENSES.md` is there); APP PERMISSIONS ⚙ opens the headset's own settings page for the app
- [ ] Change a few settings, quit and start the app again → the settings are kept

## Voice

Reference: `docs/voice.md`.

Panel and service:

- [ ] CONTROL > VOICE → "AI service: ● running" within a few seconds (the first start unpacks the runtime: a few seconds more); the service button reads ▶ START or ■ STOP from the state
- [ ] The models in VOICE (speech to text, commands, text to speech) → listed as installed (✓) from the first start; nothing to download
- [ ] Open VOICE → everything fits inside the panel; the FEMALE and MALE voices stand in two aligned rows on the right
- [ ] Kill the service (`mcx-ai log` shows its pid; or restart the app) → it restarts by itself
- [ ] VOICE has no LANGUAGE row; say "open git" → GIT opens (speech is English only)
- [ ] VOICE: pick a voice (F1-F5, M1-M5) → a sample sentence in that voice; spoken replies use it
- [ ] VOICE > Spoken replies ON → the reply is also spoken (needs the voice model)
- [ ] VOICE > TYPE… "close all windows" → same as saying it; SAY… → speaks the text
- [ ] A tap on a COMMANDS row → it shows Say “…” first and at most one short line, never an id like open_panel
- [ ] In a terminal: `mcx-ai status`; `mcx-ai say "hello" /tmp/h.wav && mcx-ai transcribe /tmp/h.wav` → "Hello."
- [ ] `mcx-ui voice run "open the vault"` → JSON result, VAULT opens; `mcx-ui voice list` → the commands

Saying commands:

- [ ] TALK, say "open git" → the HUD above the DOCK: ● LISTENING with a level bar, then ✓ Opened GIT; GIT opens
- [ ] "close all windows" → every panel closes (the DOCK and terminals stay)
- [ ] "open the providers page" → AGENTS opens; "show me the source control" → GIT
- [ ] "find the thread where we …" (words from a thread title) → THREAD opens on it
- [ ] "switch to the workspace called <project>" → THREADS shows that project
- [ ] "use the floating layout" / "fixed mode" → the layout changes; "recenter" → the windows come back
- [ ] A phrasing no template knows ("I need to see which AI providers are signed in") → ◆ THINKING, then the right panel (about 2 s; the first one after the start a few seconds more)
- [ ] "run ls" → a confirmation dialog in VR; RUN types it into the terminal
- [ ] Silence after TALK → "no speech heard" after about 6 s; TALK twice → it stops and processes what was said
- [ ] Speed: "open git" → done right after you stop talking (about 0.7 s pause + 0.6 s); an unusual phrasing → ◆ THINKING in under a second
- [ ] Frame rate while talking → about 72 fps; VOICE shows the service's RAM
- [ ] "never mind", "what can you do for me?" → nothing opens or toggles
- [ ] "close the settings panel", then within 2 minutes "reopen it" → SETTINGS opens again; "close it" closes it; VOICE > LAST shows “reopen it” → “open settings”
- [ ] "Open ambient focus" → CONTROL opens on the FOCUS page (also when heard as "fogus")
- [ ] "Turn on ambient focus and set intensity to 0" → focus on, intensity 0, the slider moves
- [ ] "Set the dome radius to two meters", "radius 50 centimeters", "set the blur to fifty percent", "make the windows bigger", "volume a bit less", "turn off the sounds" → each changes that setting
- [ ] "Show the installed tools" → SYSTEM on TOOLS; "how much disk space is left" → SYSTEM on STORAGE
- [ ] BLUR on, say a few voice commands → speech to text is about as fast as with BLUR off (VOICE history, asr ms)

Questions about the session:

- [ ] "How many captures did we take so far?", "how many screenshots do we have?" → a spoken count (and the newest one's time), not a recap; CLIPBOARD does not open; "open the clipboard" and "can I open the clipboard?" still open it
- [ ] With a thread waiting for an approval, ask: "what is this command I'm asked to accept, is it safe?" → a spoken answer naming the thread, the command and its risks, never "it's safe"
- [ ] Ask "do we have the rust toolchain?", "is this build stuck?" (during a long `cargo build`), "what has this thread done so far?" (pointing at THREAD) → short spoken answers; "open settings" still opens Settings
- [ ] TALK: "what is this thread doing?", "is it stuck?" → a short spoken answer at once (no waiting for the model)
- [ ] Ask "what happened while I was away?" → no placement, focus or menu settings in the answer; threads, approvals and builds are there
- [ ] "Give me a recap of the <name> thread" (a thread that is not open) → "Let me look." at once (about 1.5 s after you stop talking), the HUD says ◆ LOOKING, then "The <name> thread is idle / working / waiting for you." and one more sentence; it is the thread you named, not the open one
- [ ] "Summarize this thread", "catch me up", "what has it done so far" on the open thread → the first real sentence within about 4 s
- [ ] A question the app needs the model for ("why did the build fail?") → "Let me look.", then the answer is spoken sentence by sentence while the HUD text still grows (◆ ANSWERING); STOP or TALK mid-answer: the voice stops and no later sentence plays
- [ ] Ask a session question with a 2-sentence answer, also with an agent working → the first words come about 3-4 s after the text appears, with a short pause between sentences, and no gap between the first clip and the next; only 2 sentences / about 200 characters are spoken and the full answer stays on the HUD
- [ ] While a reply speaks, press ■ STOP on the voice HUD (also try TALK, and a new question) → the voice stops at once and no later sentence plays
- [ ] Ask "is it stuck?" on a working thread → durations are spoken as "3 minutes 20 seconds", not "three men"
- [ ] During a long spoken answer → 72 fps holds and the voice has no gaps between words

Voice effect and speech speed:

- [ ] VOICE > SOUND shows OFF / AI / SHIP / COMM, with AI selected on a fresh install; pick each one → it says "Systems online. All panels ready." in that sound: AI is the same voice, slightly computer-like with a small room; SHIP is clearly synthetic with a longer tail; COMM is a narrow radio with a squelch at the start and the end; OFF is the plain voice
- [ ] Play all four sounds → they are equally loud, never crackle or clip, and the first word starts without half a second of silence
- [ ] Say "make your voice sound like a radio" → COMM is selected and replies sound like a radio; "give the assistant its normal voice back" → OFF; "switch the voice effect to starship" → SHIP
- [ ] VOICE > SPEECH: FAST (3 steps) is selected on a fresh install; ask the same question with FAST and with CLEAR → FAST starts a little sooner; tell if FAST ever drops or swaps a word you notice (then CLEAR becomes the default)
- [ ] With the voice effect on (AI, SHIP, COMM), FAST speech stays easy to understand → tell if a word is lost
- [ ] Change VOICE > SOUND or SPEECH, then ask a recap → "Let me look." already has the new sound (or, the very first time, the answer comes without it)
- [ ] Restart the app → the chosen SOUND and SPEECH are kept
- [ ] A long spoken reply with SHIP → 72 fps holds (the effect runs off the main thread)


### Hey Whisp (on by default)

- [ ] CONTROL > VOICE shows "Hey Whisp" ON on a fresh install (after the first start's setup); TALK works as before
- [ ] During the first start's setup it does not listen (the microphone is not opened)
- [ ] Say "Hey Whisp": within about half a second the Whisp flares gold, a two-note sound plays and the card by it says
  "● LISTENING · Say a command · N s"; wait 3-4 s, then say "open the threads" → THREADS opens
- [ ] While you speak the card says "Hearing you" with dots that move with your voice
- [ ] Say "Hey Whisp, open the files" in one breath: FILES opens at once
- [ ] Say "Hey Whisp" and then nothing: after about 6 s the card says "Didn't catch that", a soft sound, no error
- [ ] Say "hey wisp", "hey whisper", "a whisp" (sloppy): each wakes it; "hey Whisp" alone never runs set_wake_word or
  another command
- [ ] Say "a wisp of smoke" or "hey, this is Tom" near it: no wake
- [ ] Talk near the headset for a minute (not to it): no wake; 72 fps (Perf menu Snapshot) with the switch on and off
- [ ] The TALK button still works with the switch on
- [ ] Take the headset off: the control port's `voice wake` says it waits (off the head); on again: it listens again
- [ ] `voice wake test /mnt/quest/hey.wav` with a recorded "Hey Whisp": a match, and the round trip about 0.3-0.4 s
## Terminal and Linux

Terminals:

- [ ] Type with the system keyboard (⌨ on the frame) → text appears; Enter runs
- [ ] Key bar: Esc, Tab, Ctrl, arrows, ⌨, paste → each does its job
- [ ] Run `vim`, `less`, `htop` (or `top`) → they draw correctly, with colours; resize-free
- [ ] `+` on the frame → a second terminal opens beside; tapping a frame focuses it (the key bar follows)
- [ ] Press `✕` on a terminal and open TERMS → the terminal closes; the TERMS panel lists the terminals; FOCUS / CLOSE work
- [ ] Press `–` on a terminal, then `✕` on the last one → `–` hides it (TERMS lists it as hidden; a tap shows it again); `✕` on the last terminal hides it instead of ending it, and the toast says "TERMS on the menu shows it"
- [ ] Press `⎘` on a terminal's frame → the clipboard is pasted into that terminal
- [ ] Run `ls /mnt/quest` → it shows `plugins/`, `scripts/`, `control.token`
- [ ] Run `ls ~/.claude/skills` → it shows 49 skills: the 6 MetaCodex ones (quest-gesture-plugins, quest-plugins, quest-scripting, metacodex-context, quest-views, worktree-per-thread) and 43 third-party skills

The system keyboard (the app has no keyboard of its own):

- [ ] A text field (the WEB address, a search, an API key in VAULT) → the headset's system keyboard opens with the field's text; what you type shows in the field once (no doubled letters); Done keeps it; Cancel puts the old text back; a secret field shows dots
- [ ] The first time the keyboard opens in a session → a toast: "the headset can lay this keyboard on a table. Settings > Experimental > Surface keyboard"; it does not show again until the app restarts
- [ ] With Settings > Experimental > Surface keyboard on, rest both hands on a table when the keyboard opens → note where the system shows it (on the table or floating)
- [ ] Terminal: KEYS on the DOCK or the wrist, the frame's ⌨, the key bar's ⌨ → the keyboard opens; type `ls`: the terminal shows `ls` once (not `lsls`), each letter at once; Backspace deletes one letter; Done runs the command; the same button again closes the keyboard
- [ ] Voice "show the keyboard" → it opens; said again, it stays open; "open the table keyboard" opens the same keyboard
- [ ] The wrist menu has no TABLE tile → KEYS is lit while the keyboard is open for the terminal
- [ ] A Bluetooth keyboard (if paired) → typing, arrows and Ctrl-C work in the terminal and in a focused field; the key bar (Esc, Tab, arrows, Ctrl, Alt) works

SYSTEM (CONTROL > SYSTEM):

- [ ] SYSTEM > TOOLS → a plain list in groups, each with a header (AI AGENTS, JAVASCRIPT · TYPESCRIPT, PYTHON, NATIVE CODE, the native-app group, GIT, TERMINAL UTILITIES, EDITORS); a tile for each tool that is installed (git, node, npm, python3, curl, …) with its version; missing ones dimmed ("+ install"); no logos; no gh, php, ruby, lua or deno; the bottom row is ⟳ CHECK | PACKAGES… (no TERMINAL button)
- [ ] Tap a missing tool (for example jq) → Install → a terminal runs the install; ⟳ CHECK → it shows as installed
- [ ] PACKAGES… → the Debian packages you chose and the npm globals
- [ ] TOOLS > the native-app group > ndk → install: downloads the NDK r27d for arm64 (about 120 MB), checks its sha256, installs it to `/opt/android-ndk`; a new terminal has `ANDROID_NDK_HOME`; the tile shows 27.3.13750724
- [ ] In a terminal: `cat /mnt/quest/system/tools.json` and `mcx-ui system tools` → the same list with versions and paths
- [ ] STORAGE → the headset bar (Linux, AI models, shared, apps + system, free) and Linux by folder; tap a cache: CLEAN; LINUX LIMIT below the current size: a warning

## Files, git and web

Files and git:

- [ ] Open FILES (the shortcuts for /root, the shared folder `/mnt/quest`, and /) and tap folders → they open; CD HERE / VIEW / EDIT / PATH ⇥ act in the focused terminal; FILES APP at the end of the top row opens the headset's own Files app
- [ ] Clone a repo first (`git clone https://github.com/SolidityDrone/MetaCodex /root/MetaCodex`), then press ⟳ SCAN in VAULT > GIT → it finds the repos under /root
- [ ] Pick a repo in VAULT > GIT → it shows the branch, ahead / behind, changes, branches and the metro history; FETCH, PULL, COMMIT… (keyboard), PUSH, CHECKOUT (tap a branch) and TERMINAL work
- [ ] Voice "open git" (or `mcx-ui action open:git`) → CONTROL > VAULT opens on its GIT tab

WEB:

- [ ] WEB on the DOCK or the wrist → a window opens beside the others on a start page (bookmarks, local servers); the app stays on, everything else stays visible
- [ ] Type `localhost:8000` (with `python3 -m http.server 8000` running in a terminal) → GO: the page shows in the window; tap a link with a pinch and with a fingertip; drag the page to scroll; ◀ ▶ ⟳ work; the title and "loading %" show under the toolbar
- [ ] ☰ LINKS → bookmarks add and remove; LOCAL SERVERS lists running ports; the bottom row has only ⟳ SCAN; start `python3 -m http.server 8000` in a terminal: LINKS > SCAN shows :8000 → it opens in the WEB window
- [ ] Tap a text field on a page, ⌨, type, Done → the text is in the field; ⏎ submits, ⌫ deletes
- [ ] A dev server link printed in a terminal (`⇗` on it) → it opens in the WEB window; a sign-in link (`claude auth login`) opens in the headset's own browser instead
- [ ] ⇗ in WEB → the page opens in the headset's own browser; on a PC with USB adb, `chrome://inspect` lists the page (console, DOM)
- [ ] Move the WEB window, close and reopen it → the page is still there; with "Bend panels" on it stays flat
- [ ] WEB open on a scrolling page → 72 fps; closed: no cost

CLIPBOARD (captures):

- [ ] SELECT, then pinch-drag over a window (also at a low PANEL OPACITY) → CLIPBOARD has the capture, opaque, with nothing of the room or the hands and no orange border; the orange frame goes as soon as the capture is taken; no hitch at the moment of the capture (72 fps)
- [ ] CLIPBOARD from the wrist (CLIP), the DOCK and by voice ("open the clipboard", "show the screenshots") → it opens
- [ ] CLIPBOARD has a KEEP slider above its buttons → with more captures than KEEP (for example 5, or 21 captures at 20) the oldest go and the line says "n of 5"; the preview still shows whole
- [ ] CLIPBOARD > CLEAR ALL pressed three times quickly → one dialog, parallel to CLIPBOARD, centred over it a few cm in front (never through it, never half off its edge); the extra taps delete nothing; CANCEL closes it

## Agents and MQH

MQH:

- [ ] Start the app and wait half a minute → CONTROL > MQH > STATUS says "running in this headset's Linux", a message says "MQH is running in this headset", and AGENTS fills in by itself (nothing to connect)
- [ ] MQH → the title reads MQH · SKILLS · PLUGINS · SCRIPTS · GESTURES; it opens on SKILLS; the last tab is STATUS (URL, token, sync, ⟳ RESTART); MQH has one entry in CONTROL, and the DOCK and the wrist menu have no SCRIPTS or GESTURE tile; SKILLS' three buttons are not cut off
- [ ] Stop MQH (or wait for a failure) → AGENTS says "MQH > STATUS > RESTART tries again"
- [ ] STATUS, sync on: change a setting (for example PANEL OPACITY) → `quest.settings` is updated in MQH; change it through the MQH API → the app follows within 5 s
- [ ] STATUS: type a TOKEN → `grep -c mqhToken files/settings.json` shows an empty value; the MQH connection still works
- [ ] MQH > SKILLS → the shipped skills say READ-ONLY and REMOVE refuses them; + NEW SKILL opens NEW THREAD, START puts the brief in the draft's composer (SEND starts it); ⌕ FIND finds skills, ⇣ INSTALL adds one under YOURS, REMOVE removes it
- [ ] Frame rate with MQH running → 72 fps

Agents:

- [ ] Open AGENTS → it lists the agent providers with READY / SIGN IN / INSTALL
- [ ] Pick one → SIGN IN ▶ → a new terminal runs its login (for example `claude auth login`)
- [ ] The terminal prints a link → toast "Link in …" → `⇗` on that terminal opens it in the headset's own browser
- [ ] Sign in in the browser; the redirect back to localhost completes the login (or paste the code with ⎘) → AGENTS ⟳ shows READY + account
- [ ] Sign in the same way with `opencode auth login` and `codex login --device-auth` (device code flow) → AGENTS shows READY for each
- [ ] Press GET on a missing agent (for example `opencode`) → a terminal installs it in about a minute, the tile turns into "✓" with its version and it shows in the providers (the others the same way); UPDATE (installed agents only) updates it through MQH (or run `mcx-setup` in a terminal); MORE · COMING SOON cannot be pressed
- [ ] Press DISABLE (or ENABLE: the button says the one that applies) → the provider is switched in MQH
- [ ] Pick an installed agent → the second row reads DISABLE (or ENABLE) | NEW THREAD → | UPDATE; pick one that is not installed: no UPDATE; no OPEN LINK button; the hint says ⇗ on the sign-in terminal opens the link
- [ ] ⚿ API KEY: pick your provider, copy your key (in WEB or the headset's browser), ⎘ PASTE, SAVE → "✓ … set for the agents"
- [ ] NEW THREAD with `opencode` → it answers (with your key, no terminal login)

Agent views:

- [ ] Thread sidebar > VIEWS > DEMO → a "What agents can show" panel: stats, a line chart, a diagram, a donut, a checklist, choices and a physics shelf in front with balls and boxes
- [ ] Push a ball with your index fingertip → it rolls; ❚❚ PAUSE / ▶ RUN / ⟲ RESET work; move the panel: the scene moves with it
- [ ] Hover a chart → the value shows in its top-right corner; hover a diagram node: its edges light up
- [ ] Ask a thread agent "show the architecture of this repo as a diagram" → a view opens by itself; the timeline shows `[▣ …]` instead of the code block
- [ ] An agent view with choices → tap one: it arrives in the thread as your message
- [ ] In a terminal: `mcx-ui view schema`, then `mcx-ui view show spec.json` (the skill's example) → the view opens; `mcx-ui view wait <id>` returns your tap

## Threads

Projects and the THREADS list:

- [ ] Open THREADS → it shows NATIVE, DEFAULT, RESEARCH, PLUGINS (dim headers, in that order); OTHER only when a project is outside `/root/metacodex-projects/` and has no type; a tap on a header folds it, a tap on a project folds it; threads stay under their project (and its worktree rows); the project row says how many run / need you; a tap on a thread opens it; each thread shows its status (running / error / NEEDS YOU) and the list refreshes
- [ ] An empty type says "no … project yet: tap to make one" → the tap asks the name of a project of that type
- [ ] THREADS: + PROJECT… → DEFAULT → name "My Test" → the project is listed under DEFAULT (`…/default/my-test`)
- [ ] + PROJECT… asks NATIVE APP / DEFAULT / RESEARCH / EXISTING…, then the name on the keyboard (empty field, the hint in its caption) → "Weather App" makes `/root/metacodex-projects/native/weather-app`; EXISTING… picks a folder, then PROJECT TYPE: NATIVE APP, DEFAULT or RESEARCH, and the project is listed under that type
- [ ] In a terminal: `cd /root/metacodex-projects/native/weather-app && git status && git config user.name && cat .metacodex/project.json && ls -la .claude/skills` → a git repository on `main`, an identity (yours when a global one is set, else MetaCodex), type native, the 13 `quest-native-*` links (`quest-native-art-direction` among them)
- [ ] A Native project's folder → `.metacodex/project.json`, 13 links `.claude/skills/quest-native-*`, and `AGENTS.md` / `CLAUDE.md` with the native block; a DEFAULT one has none of them (`ls -la <folder> <folder>/.claude 2>&1`)
- [ ] In a DEFAULT project ask an agent "what are your rules for apps?" → no word about launch requests or the app menu
- [ ] A RESEARCH project → `AGENTS.md` and `CLAUDE.md` hold the research note (notes in Markdown, sources cited, no app build), no `.claude/skills`; ask its agent "what are your rules here?": it names those rules, nothing about apps
- [ ] The same name again in the same type → a toast says the folder exists; nothing is made
- [ ] Offline, never signed in to git: in a new project ask the agent to commit a file → the commit works
- [ ] An old project with `"type": "general"` in `.metacodex/project.json` → it shows under DEFAULT
- [ ] Voice: "create a research project called solar panels" → made under RESEARCH; "new project" → the type dialog; "make a native app project" → the name keyboard

A new thread:

- [ ] Press + NEW THREAD… → it offers providers, then models for the chosen provider; PERMISSIONS EDITS / AUTO / FULL; WORKSPACE CHECKOUT / WORKTREE; no FIRST MESSAGE field
- [ ] START → THREAD opens as "New thread · <project>" (NOT STARTED), the keyboard on the composer; first message "list the files and summarise the README" → SEND: the thread is made, its title is the start of that message, the timeline fills (you, agent, commands, tools)
- [ ] NEW THREAD > START, then close THREAD (✕) without a message → no new thread in THREADS, also not after ⟳ or a restart
- [ ] NEW THREAD > START, then open another thread, or NEW THREAD > START again → the first draft is gone, nothing is made
- [ ] In the draft: attach a capture (+ or CLIPBOARD > TO THE THREAD) and send the first message → the thread has the text and the capture
- [ ] In the draft: change the model or the access on the chips before the first message → the new thread uses them
- [ ] WORKSPACES > a worktree > NEW THREAD HERE… > START > first message → the thread works in that worktree
- [ ] Voice: "start a thread to fix the login page" → a thread is made at once with that message (last agent and model chosen); "new thread" → the draft opens and waits

The chat:

- [ ] Composer: type with the keyboard → SEND: the message appears; the agent answers
- [ ] Send a task → your message in a bubble on the right; under it `◐ Working` with the steps appearing one by one (thoughts, `$ commands`, `✎ files`)
- [ ] While it thinks → "Thinking…" with its newest sentence under it; a tap on a thought opens its text
- [ ] The status line under the chat → what it does now and for how long (`Running npm test · 0:45   turn 3:12 · live`)
- [ ] Wait for the answer → it appears in full under the work and grows while it is written (a caret at its end); the work header closes to `⚙ Worked 4:31 · …` when it is done, a tap opens it again
- [ ] Ask for something long (several commands) → when the steps push your prompt off the top, it stays pinned there; a tap on it scrolls back to it
- [ ] Scroll up while it works → the view holds still, `↓ NEW` shows; a tap goes back to the end; a tap on a header opens and closes it
- [ ] An approval row shows and goes → the chat keeps its place and text
- [ ] With EDITS, a command needing approval → ALLOW ONCE / ALLOW SESSION / DENY buttons appear and work
- [ ] ■ STOP stops a running turn → within 2 s the header leaves WORKING and the chat stops "writing"
- [ ] Press ▲ ▼ on a long thread → the timeline scrolls
- [ ] Press FILES, GIT or TERMINAL in a thread → each jumps to the thread's workspace (the worktree path for WORKTREE threads)
- [ ] Look at the meta row → it shows the plan and the usage windows (for example 5 h · week · month); the CONTEXT ring's picker lists them with their resets
- [ ] Chips under the composer: MODEL ▾ (models of the provider, then effort) → the next message uses it; ACCESS ▾ (Edits / Auto / Full access); the workspace chip; the CONTEXT ring with % (tap: compact or fork); ■ STOP
- [ ] ⑂ FORK… → "Same agent": a new thread with the same conversation opens; FORK to another provider: a new thread in the same folder that knows what was said (it summarises where things stand)
- [ ] ◉ beside SEND: say a message, pause → the words are typed into the composer (not sent); SEND sends them; the same ◉ works in a new thread's draft (the first message)
- [ ] Stop a thread's provider mid-turn, or wait on a silent step → after about 2 min the status line turns amber ("may be stuck")
- [ ] A thread finishes while its window is open → the frame pulses three times and the answer glows; the Whisp casts "✓ … answered: …"
- [ ] A thread finishes while you do something else (its window closed) → within a few seconds the Whisp casts "✓ … answered"; a tap on the callout opens the thread
- [ ] A thread asks for an approval in the background → the Whisp casts "… needs you" (amber)
- [ ] A thread working; launch a built app, wait until the agent is done, Back → the thread says idle / done at once (no "WORKING", no "writing the answer")
- [ ] A thread whose host is not connected → THREAD's header says OFFLINE (not NO HOST)
- [ ] A thread with a working agent open → 72 fps

The message box (grow it, back to one row on SEND):

- [ ] Open THREAD → the composer is one row; a bar with three dots sits on its top edge; it lights up under the ray or a finger
- [ ] Pinch the bar and drag up → the box grows upwards, the chat above gets shorter by the same amount, `+` `◉` `SEND ▶` stay on the bottom row
- [ ] Poke the bar with a finger and push up, then drag down → the box shrinks, never below one row
- [ ] Drag up as far as it goes → the box stops at about half the window; the chat keeps about 12 cm
- [ ] A long message in a grown box → the text wraps; the last lines show, where the caret is; typing keeps the caret visible
- [ ] Tap the box with a grown height → the keyboard opens and types into it
- [ ] SEND (button) → the box is one row again and the chat has its full height; the message appears in the chat
- [ ] Enter on the keyboard sends → the same reset
- [ ] Grow the box, then open another thread (THREADS list) → one row
- [ ] Grow the box, then + → ATTACH FILES → one row; the grip does nothing while the attach window is open; CANCEL leaves one row
- [ ] Voice: "make the text box bigger" (one step), "shrink the composer" (one step), "reset the text box" (one row) → the box follows; "expand the message box" and "put the message box back to the default size" work too
- [ ] While dragging, the chat changes a few times a second → no stutter; 72 fps with a working thread open

Captures and files in a thread:

- [ ] A thread: + → REFERENCE CAPTURE → the list inside the thread panel; pick two → two chips; + → ATTACH FILES → a Linux file; SEND → the agent sees the images and the file

The sidebar (the details handle `›` on THREAD's right edge):

- [ ] Open a thread → a handle `›` with PROJECT and the number of changed files on THREAD's right edge; tap it: the sidebar opens at the thread's right edge, tops level, bent with it (one curved screen); `‹` folds it
- [ ] Drag THREAD → the sidebar comes along; pinch the sidebar's header: both move; FLOAT: the pair keeps one slot, nothing overlaps it
- [ ] Open the sidebar on GIT in thread A, keep it folded in thread B → switching between them brings back each one's state; B never shows A's files
- [ ] CHANGES in a repo → every file the agent changed, with its +added / −removed counts in green / red, also through commands (sed, npm), untracked new files included; the folder's other changes under ALSO CHANGED IN THIS FOLDER; the summary says against HEAD (or since the base branch in a worktree)
- [ ] Tap a file → the diff reads like a code review: old / new numbers, + / − gutter, green / red lines, coloured code, `⋯ N unchanged lines` (tap: they open); readable at arm's length without leaning in
- [ ] A long line → drag sideways to read it; WRAP breaks it over rows instead; ▲ PREV / ▼ NEXT walk the files; `@ ASK` puts the path in the composer
- [ ] While the agent edits the open file → new files and counts, the list and the diff update by themselves within a few seconds, the view keeps its place
- [ ] A big file (a lock file, a generated JSON) and an image → the big one shows only its changed part and says so, the image says binary; nothing stalls
- [ ] FILES → the project's own folder (not /), breadcrumb `project › src`, a tap on a part goes back up, never above the project; changed files coloured; a file opens with line numbers and colours; EDIT opens nano in a terminal
- [ ] GIT in a repo → branch, ahead / behind, status, recent commits; COMMIT… and PUSH work in the thread's folder; in a folder with no repo only NOT A GIT REPOSITORY + INIT GIT; INIT GIT works and the tab becomes a repo
- [ ] STATUS → model, access, context %, what runs now and for how long, approvals waiting, usage, folder, MQH
- [ ] A Bluetooth keyboard after touching the sidebar → ↑ ↓ / Page / Enter / Esc move and open in it; touch the terminal and the keys go there again
- [ ] Voice: "open the sidebar on the git tab", "close the thread sidebar", "show the status tab of this thread", and in a folder with no repo "make this folder a git repository" → each does it
- [ ] FIXED layout, a thread in front: tap the sidebar handle → the thread and its sidebar glide together until the pair is centred in front of you; fold it: the thread glides back; with another window just to its left the pair moves only up to that window, never over it; move the thread, then fold: it stays where you put it

Workspaces (several threads in one project):

- [ ] Start a thread in a project's CHECKOUT; while it works, open NEW THREAD on the same project → WORKTREE is chosen, the summary says who works in the checkout
- [ ] Start that one in a WORKTREE → both work at once; THREADS shows the project's folders (▣ checkout, ⎇ the worktree's branch) with each thread under its own
- [ ] Send a message to a second CHECKOUT thread while the first works → it does not start; THREAD says "⏸ Not started: “…” is working in this checkout" with ▶ SEND NOW; when the first is done it starts by itself
- [ ] SEND NOW → it asks first, then both work (THREADS shows 2 WORKING ⚠ on the checkout)
- [ ] ⎇ WORKSPACES → the worktree shows its branch, base, ahead / behind and changes; MERGE INTO MAIN on a clean branch merges (the checkout on main and clean); on a conflicting one it lists the files and ASK ITS AGENT sends the rebase request
- [ ] REMOVE a finished worktree → its threads are archived and the folder goes; the branch is still in `git branch`
- [ ] Voice "open the worktrees"; "what is this thread doing?" on a held thread → it says why it has not started

Thread windows and groups (two projects in THREADS: project1 with 3 threads, project2 with 1 thread; FIXED layout first, then FLOAT):

- [ ] Open the 1st thread of project1 → one window THREAD, free
- [ ] Open the 2nd thread of project1 → a new window opens and joins the first one as a group (2x1); no jump, it slides into its place; the group title says "Group 1 · 2 threads"
- [ ] Open the thread of project2 → a free window THREAD 3 beside the group
- [ ] Open the 3rd thread of project1 → it joins the group; the group becomes 2x2 (3 windows); the windows slide, the frame fades to its new size
- [ ] Tap a thread that is open already in THREADS → its window flashes and gets the dot (●); no new window
- [ ] In each grouped window → the chat streams, you can scroll it, type and SEND, ◉ dictates into that window, an approval shows ALLOW / DENY in that window and the answer goes to that thread
- [ ] Two threads working at the same time (one in the group, one free) → both chats update; the session log has no "[slow timeline]" storm
- [ ] Ask "what is this thread doing?" while you point at one window, then at another → the answer is about the window you point at
- [ ] An approval waits in a window that is not focused; ask "is anything waiting for me?" → the answer names that thread
- [ ] RESIZE (bottom right of the group): pull it out and in → the windows get larger and smaller with the group; the top-left corner stays; the text stays readable at the smallest size
- [ ] Pinch the group's title bar and move it → all its windows move with it
- [ ] Pinch the title bar of a grouped window and release it on another window of the same group → the two change places (the target lights up first)
- [ ] Pull a window out of the group (release it outside the group) → it comes out at its full size where you let it go; open its thread again from THREADS: it does not go back into the group
- [ ] Release a free window on a window of the group → it joins at that place
- [ ] Release a free window on another free window → the two make a new group (two groups at once: Group 1 and Group 2)
- [ ] The ✕ of a grouped window (top right, large) → only that window closes; with one window left the group goes and that window is free
- [ ] The details handle (›) of a grouped window → the sidebar opens at the right edge of the group, not inside the grid; drag the sidebar: the group comes with it
- [ ] ⇲ UNGROUP → every window is free at its full size, side by side, sliding to its place
- [ ] The group's ✕ → the group and all its windows close; THREADS still lists the threads
- [ ] CENTER with a group and a free window → the group moves as one window, nothing overlaps
- [ ] FLOAT → the group takes one place on the ring; a window pulled out takes its own place; the TURN bar turns them together
- [ ] Voice: "put the open threads side by side", "break up the group", "switch to the other thread window", "close this thread window", "open the thread about <topic> in a new window" → each does it
- [ ] 6 windows open → the 7th thread opens in the focused window (a message says so)
- [ ] Open 4 thread windows, 2 of them working → 72 fps

Held turns (two threads in one project checkout):

- [ ] Thread A works in the checkout (a long task); send a message to thread B in the same checkout → B shows ⏸ Not started and ▶ SEND NOW
- [ ] ▶ SEND NOW → the question "Start it now?" has SEND NOW, IN A WORKTREE and CANCEL, and its text says what IN A WORKTREE does
- [ ] CANCEL → B keeps waiting; SEND NOW (on another try) → B starts in the checkout, the toast says it shares the folder
- [ ] IN A WORKTREE → the toast says "Sent: the agent moves to its own worktree first"; B's first message in the chat starts with "This project's folder is busy with another agent..." and then your text
- [ ] Let B's agent work → it runs `git worktree list` / `git worktree add` (skill worktree-per-thread), works in `<checkout>-wt-<name>` on `agent/<name>`, and ends with the path, the branch, the base and how to merge
- [ ] A's files in the checkout → they did not change because of B (`git status` in the checkout shows only A's work)
- [ ] Run `ls ~/.claude/skills` and open MQH > SKILLS → worktree-per-thread is there, shown read-only

Archive, restore and delete:

- [ ] Open a thread window, a NEW THREAD draft and an empty window → only the thread window shows ARCHIVE… and DELETE… at the top
- [ ] Two windows open, focus window 1, press ARCHIVE… in window 2 → the question names window 2's thread; ARCHIVE: window 2 closes, window 1 stays; THREADS no longer lists it
- [ ] A thread with a child thread (a sub-thread an agent spawned) → ARCHIVE… archives both; both windows close if open
- [ ] THREADS > ▤ ARCHIVED → the archived threads, newest first, each with RESTORE ▸; ◂ ACTIVE goes back
- [ ] Tap an archived row > RESTORE → it is back in THREADS and opens normally
- [ ] DELETE… on a thread with no child → the question says it has no child threads; DELETE: the window closes, it is in neither list
- [ ] DELETE… on a thread with child threads → the question says how many go too; DELETE removes them all
- [ ] Archive a thread from another client (the MQH CLI: `mqh thread archive <id>`, or a second app) while its window is open → the window closes within about 10 s, with a toast
- [ ] Voice: "archive this chat" (the focused window's thread), "archive the thread about <topic>", "bring back the chat about <topic>", "show my archived chats" → each does it
- [ ] An archived thread that was working → the RUNNING count in THREADS drops, the Whisp does not report it as working

## Local coder

- [ ] AGENTS: the GET row has LOCAL CODER · GET 430 MB → tap: it counts up to 100 %, then "✓ ON HEADSET"; tap again: a hint how to use it
- [ ] NEW THREAD with `claude`, model "Local coder (this headset, slow)" → ask anything small (a script, a fix, a small native app in a NATIVE project): it works in minutes; a thread on a normal model still uses your account
- [ ] Install the local coder, then without restarting the app open NEW THREAD > `opencode` → it lists "⌂ Local coder (this headset, slow)" as the first row (LOCAL badge, "on this headset · offline · slow"), above its own models; `claude` the same; reopen the list once if the first look still shows the old list (MQH refreshes it in the background)
- [ ] The same task with `opencode` on that row → the turn runs on the local model (the AI service log shows coder requests); `codex` does not offer the local coder
- [ ] While a local coder thread works → TALK shows "⏸ LOCAL CODER WORKING" and does not record; 15 s after the turn ends TALK works again; 72 fps all the time
- [ ] Say "remove the local coder" (confirm) → the tile says GET again, and the model is gone from the threads' model lists after a restart of MQH

## Vault and secrets

- [ ] Open VAULT → it shows `key: Android Keystore · tee` (or strongbox) and `mqh.api-token` (policy ALLOW); on a development build a warning about `run-as`
- [ ] + ADD SECRET… `OPENAI_API_KEY` → the keyboard hides the value (dots) → "sealed in the vault"
- [ ] In a terminal: `mcx-secret get OPENAI_API_KEY` → an approval dialog in VR → ALLOW ONCE → the value prints
- [ ] Again → DENY → the command fails with "denied by the user"; the access log lists both
- [ ] Policy ALLOW → no dialog; DENY → always refused; ONCE → first request only (RE-ARM in VAULT resets)
- [ ] `mcx-secret env OPENAI_API_KEY -- sh -c 'echo ${#OPENAI_API_KEY}'` → prints the length, not the key
- [ ] `→ MQH ENV` on `OPENAI_API_KEY` → toast "set for agents"; a thread of a provider that reads that variable can use it
- [ ] `grep -r sk- /root /mnt/quest 2>/dev/null` → the key is in no file of the Linux
- [ ] Restart the app → the secrets are still there; `mcx-secret list` shows names and policies only
- [ ] VAULT > GIT: TOKEN… (a git host token) → TEST → "● <your login>"; IDENTITY… sets name / email
- [ ] GIT USES IT → `git push` to an https remote of that host works without asking for a password (the vault logs the request)
- [ ] → AGENTS → threads get GH_TOKEN (`gh auth status` in a thread)

## Native apps

Toolchain and build loop:

- [ ] Make the first native app (or open SYSTEM > TOOLS) → the native toolchain is offered (about 400 MB download, about 2.5 GB on disk) and installs only when you accept
- [ ] `mcx-app doctor` → `ok` for mcx-build, mcx-buildd, aapt2, openssl, python3; `builds  through mcx-buildd (cores 0-2)`
- [ ] `mcx-app new /root/apps/t1 --module --label "T1"`, then `mcx-app check /root/apps/t1` → `RESULT: check ok` in about 1 s
- [ ] Break a line in `src/app.cpp` (remove a `;`), `mcx-app check` → `app.cpp:<line>:<col>: error: ...` and `RESULT: 1 error(s)`; fix it
- [ ] `mcx-app runner` → the system asks to install "Runner" once: tap Install → `RESULT: installed`; "Runner" is in the Library
- [ ] `mcx-app dev /root/apps/t1` → the Runner opens with three turning cubes (`RESULT: running in the Runner`, about 3 s)
- [ ] Back in MetaCodex, change a cube colour in `src/app.cpp`, `mcx-app dev` again → in the Runner the colour changes without the app restarting (about 2 s)
- [ ] `mcx-app run /root/apps/t1` → Install asked once, "T1" opens by itself (its own Library icon) with the same cubes; run it again: no prompt; system menu > Quit; force-stop MetaCodex (the headset's Apps settings); open T1 from the Library: it still runs
- [ ] Wear the headset, and in a terminal: `for i in 1 2 3 4 5; do touch /root/apps/t1/src/app.cpp; mcx-app build /root/apps/t1; done` → 72 fps the whole time (a build takes about 2 s), voice commands still answer
- [ ] During those builds say "what is this build doing?" or use a thread → MQH answers normally
- [ ] Open T1 (MetaCodex in the background) and stay in it 10 minutes; come back → MQH threads, the agents and voice still work without a restart (MQH STATUS says running, not "starting again")
- [ ] Start `mcx-app build /root/apps/t1` from a thread and take the headset off for 2 minutes → back on, the build finished (log `RESULT: APK ...`)
- [ ] Switch between MetaCodex and an app built here 5 times, then `adb shell dumpsys meminfo com.pnncl.metacodex | grep -E "GL mtrack|TOTAL PSS"` (or `tools/gpu-switch-test.sh <package> 10` from the PC with the headset free) → MetaCodex's GPU memory does not climb by hundreds of MB per switch; note the numbers
- [ ] With MetaCodex in the background (an app built here in front) start a long build from a thread → `mcx-buildd status` shows `"budget": "paused (background budget)"` once it passes 15 % CPU over 5 min, and MetaCodex's AI process is not killed (no `excessive cpu` line in logcat)

Night Vision (an example app):

- [ ] `cp -r /opt/metacodex/quest-native/examples/nightvision /root/apps/nv`, `mcx-app build /root/apps/nv`, `mcx-app verify /root/apps/nv` → `RESULT: verify ok`; `mcx-app install` + `launch` → the system asks for spatial data once: Allow
- [ ] The room in green; near things bright, far ones dark, outlines glowing; grain and scanlines → the HUD says `depth ... LIVE`, `room N objects`, `72 Hz`
- [ ] A pinch of either hand (or right trigger) cycles PHOSPHOR / EDGES / RANGE; both hands pinching together (or A) turns the passthrough off and on; stick up / down changes the gain; the HUD's hands line shows PINCH when a pinch counts, and `palm` when your palm faces you (no pinch counts then) → open the system menu: nothing reacts until you close it
- [ ] Night Vision has its own icon (green goggles) and label in the Library → it runs with MetaCodex force-stopped

The way back from a built app:

- [ ] In a thread ask for any small native app (or `mcx-app new /root/apps/t2 --label "T2"` + `mcx-app run /root/apps/t2`) → it opens
- [ ] Left controller's menu button (or look at the left palm and pinch) → the title says "opened from MetaCodex"; the items are Back to MetaCodex, Report to MetaCodex, Quit, Close menu; point and pull the trigger (or pinch) to pick
- [ ] Back to MetaCodex → MetaCodex is there at once (no loading screen, windows where they were), a toast "Back from T2", the launching thread in front; wait 60 s: no "T2 isn't responding" dialog; T2 is not in the recent apps (it closed)
- [ ] Open T2 from the Library → the menu says "opened outside MetaCodex", only Quit (no MetaCodex items); Quit closes it, Home
- [ ] Launch T2 from MetaCodex, press the system button > Home, reopen T2 from the Library → the menu has no MetaCodex items
- [ ] Launch T2 from MetaCodex, open MetaCodex from the DOCK (no Back) → wait 60 s: no "isn't responding" dialog; T2 is still in the recent apps and opens again fine
- [ ] Open the app again, menu > Report to MetaCodex → back in MetaCodex, the thread's composer says "Feedback on T2 1.0 (1):" with two chips (▣ the app's picture, ⎘ log T2); ◉ to say what you saw, SEND: the agent sees the picture and the log
- [ ] Open the app again, quit it from the system menu → tell whether MetaCodex comes forward by itself (best effort, the system may refuse)

Launches by request (Native projects):

- [ ] In a Native project, with the thread in its most permissive approval mode, ask for a small app → the agent builds and verifies, then a dialog APP READY: <app>: LAUNCH / LATER appears in front of you (and a toast in MESSAGES); nothing opens by itself
- [ ] LATER → the app does not open; say "launch the app": it opens; its menu's Back brings you to that thread
- [ ] A brand-new app → the dialog says INSTALL first; INSTALL → the system's dialog → then APP READY with LAUNCH
- [ ] Say "don't open the app" while one waits → the agent's `mcx-app status` says declined

## Ambient Focus and depth

- [ ] FOCUS on the DOCK → the room fades around you, the windows stay bright; FOCUS again: back; "focus mode on" / "focus mode off" by voice
- [ ] First FOCUS → the system asks for "spatial data" (allow it); CONTROL > FOCUS: "depth: live", then "floor: measured under your feet (… cm from the system's floor)"
- [ ] FOCUS on → a dome standing on the floor around you: walls, furniture and your hands near you stay visible, far ones fade (no dome "starting above")
- [ ] Walk around → the dome follows you; nothing flickers at its edge while you stand still
- [ ] RADIUS changes the dome's size live, SOFTNESS its edge, INTENSITY the darkness; BLACKOUT → black around you, only the windows
- [ ] BLUR above 0 → the room outside the dome is a soft image (darker with INTENSITY), at once all round you, also at the edges of your view and behind you (there frosted colours until the camera has looked, never the sharp room); 0: darkness only
- [ ] BLUR 50 %, turn the head slowly → the soft room is there all around where you have looked, no rectangle edge, and it does not move with the head
- [ ] Put your hands in view at BLACKOUT, with BLUR, and in front of a window → they are always visible
- [ ] The cut-out around near things (your hands, a chair, a person) → clean and steady: no flicker, no blocky edge (FOCUS: "camera: live …")
- [ ] Someone walks in front of a window, or you hold an object up → it stays visible in front of the window ("Real things in front of windows" on); off: the window covers it
- [ ] A hand in front of a window shows, also with "Real things in front of windows" off → poke the Whisp's menu and a big window from in front: the finger stays visible over the glass
- [ ] A hand behind a window → it stays hidden behind it (the cut-out does not punch through an opaque window)
- [ ] The same with a window that has stood alone for 3 s or more (it is then marked clear) → the person or object shows in front of it within about half a second
- [ ] Both FOCUS depth settings on (Sharp depth, Real things in front) → VrApi FPS near 72 with a few windows open; dictate into a thread chat several times: no lag during or after speech to text (the camera keeps running)
- [ ] Turn Ambient Focus off and on again within a minute → no seconds of low frame rate (the depth and the camera stay on, idle, for 60 s)
- [ ] On a first start, after "Setup done" → about 2 s later the depth starts and a hand in front of a window shows over it
- [ ] Without the "spatial data" permission → the floor-plane darkening still works
- [ ] Switch Ambient Focus on (the FOCUS preset) → outside the dome there is deep blue-black space, a slow nebula, a few twinkling stars, a faint grid on the floor and walls just past the edge, a ring of light where the dome meets the floor; BLACKOUT: space around you, the windows stay; with BLUR 50 %: the blurred room with the grid and the ring
- [ ] Turn the head and walk → the nebula and stars stay with the room (no swimming), no flicker of the grid at the walls' edges
- [ ] Frame rate with focus + blur on → about 72 fps
