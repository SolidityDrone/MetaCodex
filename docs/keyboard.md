# The table keyboard

MetaCodex's own keyboard (`UiSurfaceKeyboard`): a normal keyboard laid on a real table and typed on with ten
fingers, or floating in front of you until your hands rest on a table. Horizon OS's keyboard is never opened.

State 2026-11-11: written, compile-checked, engine tests green (no Editor needed). **Not yet run in the Editor
self-test (Windows had 1.3 GB free, the test needs 3 GB) and not yet on the headset.** The thresholds below are
first values from a simulated typist, not from a person's hands.

## 1. How Meta's Surface Keyboard gets its precision

Sources: Meta's help page for the Experimental feature, hands-on reviews (UploadVR, heise, Viewport 360,
Gizmodo), and the research it comes from: TouchInsight (Meta Reality Labs + ETH Zurich, UIST 2024,
arXiv 2410.05940), StegoType (Meta, UIST 2024), "Decoding Surface Touch Typing from Hand-Tracking" (2020).
Meta has not confirmed that the shipped feature is TouchInsight; its published numbers (37 words a minute, 3 %
errors) are that paper's.

1. **The table is calibrated.** Both hands rest flat for a few seconds; the keyboard is anchored to that plane.
   Meta's tips: good light, a steady surface, hands at a constant height, recalibrate when accuracy drops.
2. **A network decides which finger touched, and when.** Hand tracking at 30 poses a second; the last 41 frames of
   all ten fingertips, in the keyboard's coordinates, go into a network trained on 5.3 million recorded presses.
   Per frame it says "this finger touched" or "none" (touch F1 0.99, finger F1 0.96). Not a height threshold.
3. **A press is a likelihood, not a point.** Even with that network the place is off by 6.3 mm on average (index
   5 mm, little finger 8 to 11 mm; the plain tracked tip: 10.6 mm), against keys 19 mm apart: the key under the
   touch is the wrong one more than one time in four.
4. **A language model picks the key.** Character 6-gram + word 3-gram, beam search. Character model alone: about
   7 % errors; with word-level correction of the word being typed: 2.9 %. Physical keyboard, same people: 0.8 %.
5. Limits Meta states or reviewers found: fingers hide each other from the cameras (false keys; left Shift the most
   common), lifting the fingers higher helps, no Esc / Tab / Ctrl / arrows, English only.

## 2. What MetaCodex does (same structure, rules instead of a trained network)

There is no training set here, so the network's two jobs are written as rules, each for a failure of the old
"fingertip below a height" keyboard. Code: `unity/Assets/MetaCodex/Runtime/Typing/` (no Unity types).

| Part | File | What it does |
|---|---|---|
| Layout | `KeyboardLayout.cs` | ANSI 75 % board at real size: 19.05 mm pitch, real key widths and stagger, Esc + F1-F12, arrows as an inverted T, Page Up / Down / Home / End. 82 keys. `paste` and `hide` sit where a board has its system keys. US English. |
| Which finger, when | `TapDetector.cs` | A floor per finger (the height its tip is tracked at when it touches), a press = lifted, fast stroke, arrives. Slow sinking = come to rest. Neighbours dragged down by the pressing finger, and a hand put down, type nothing. Speeds over 42 ms (tracking is 30 Hz). A lone press fires at once (before: always 55 ms later). |
| Which key | `KeyDecoder.cs` | Likelihood over the keys round the tip + English letter statistics (what was typed before), capped so a press within 5 mm of a key's centre is never overruled. A constant offset per finger is learned from the presses and removed. |
| Letter statistics | `Resources/Typing/letters.bytes` | 22 KB, made by `tools/typing/build_letter_model.py` from this repo's own documentation (counts only, no text). |
| The keyboard | `UiSurfaceKeyboard.cs` | Plain rounded caps on a dark deck (shader kinds 7 and 8), touchpad on the right, the lit key under a lifted finger, sticky Shift / Ctrl / Alt, Caps Lock, repeat for Backspace / Delete / arrows. |

What is being typed into decides how much the statistics weigh (`KeyContext`): a text field fully, the terminal
and web pages half (and Tab / Ctrl / Esc / arrows are likelier there), a masked field (token, API key) not at all.

**Gestures off near the keyboard.** While a hand is within 5 cm of the keyboard on the table and under 12 cm over
it, the gesture engine does not see that hand (`GestureEngine.Muted`): no ray, no pinch click, no poke on a
window, no grab, no wrist menu, no clap, no custom gesture. It is free again 0.35 s after it left. The hand
cut-outs still show it.

## 3. Hand tracking: what was wrong in this app

| Finding | Effect | Done |
|---|---|---|
| `handTrackingFrequency = HIGH` (Fast Motion Mode) since the first build. Meta: it "may increase jitter, impacting high accuracy interactions like direct touch or typing"; with passthrough "virtual hands may appear more responsive than passthrough hands"; meant for fitness and rhythm games. | Jittery fingertips for poking and typing; hand cut-outs running ahead of the hands you see. | Back to the default (LOW, 30 Hz): `MetaCodexSetup`, project config, manifest. Takes effect with the next build. |
| A pinch (thumb near index) was read as a click while typing. | "I click around when trying to type." | The mute zone above. |
| `OVRHand.PointerPose` is in tracking space and was used as world space. | None today (the rig sits at the origin); wrong rays if the rig is ever moved. | Converted through the rig's tracking space. |

Checked and correct: bone update order (OVRHand -90, OVRSkeleton -80, OVRCameraRig -50, app scripts 0: fresh
poses every frame), hand scale applied (`_updateRootScale: 1`), OpenXR skeleton joints, hand anchors and bones
from the same OVRPlugin state.

Not changed, candidates if hands still feel off after the next build:
- Hand cut-outs are straight capsules from knuckle to tip: a curled finger does not fit them. Use the real joints.
- Cut-outs are drawn where the hand is now; passthrough shows it about 40 ms ago. Delay the cut-outs to match.
- Meta: "check confidence before acting on pinch data". The router does not. Needs headset data first: gating on
  confidence can also make clicks fail.

## 4. Tests

`tests/typing/run-typing-tests.sh` (WSL, no Editor, about 15 s): 60 checks. Unit checks of the layout, the decoder
and the detector, then a simulated typist under tracking noise. **The typist is a model, not a person**: it
shows the rules do what they were written for and guards against regressions; it does not predict error rates
in the headset. 12 sittings x 380 keys, errors in % of keys meant:

| Typist | Tracking | Height threshold (before) | This engine | Key delay before -> now |
|---|---|---|---|---|
| ten fingers, lifting 20 mm | default 30 Hz | 14.3 % | 0.9 % | 87 -> 53 ms |
| ten fingers, hovering 13 mm | default 30 Hz | 61.2 % | 1.6 % | 84 -> 46 ms |
| ten fingers, hovering 8 mm | default 30 Hz | 93.8 % | 7.3 % | 93 -> 40 ms |
| ten fingers, resting on the table | default 30 Hz | 78.3 % | 3.4 % | 87 -> 48 ms |
| ten fingers, 60 words a minute | default 30 Hz | 63.1 % | 2.7 % | 78 -> 46 ms |
| two index fingers, hand bobbing | default 30 Hz | 27.5 % | 1.1 % | 85 -> 46 ms |
| hovering 13 mm | heights drift 5 mm | 33.8 % | 3.9 % | 95 -> 45 ms |
| resting on the table | heights drift 5 mm | 44.8 % | 10.7 % | 97 -> 49 ms |

The old keyboard's large numbers are mostly missed keys: it needed a 13 mm lift over the flat-hand height.
`MCX_TYPING_SHOW=1` prints what was meant and typed.

Editor self-test: section `keyboard` of `MetaCodexUiTest` is rewritten for the new keyboard (real size, mute
zone, lit key, follower finger, hand put down, touchpad, masked field). Compiles; not run yet.

## 5. Headset checks (after the next build)

1. Rest both hands flat on a table: the keyboard appears under them at real size, plain keys, touchpad on the right.
2. Type a sentence with all fingers, lifting each finger about 1 cm. Count wrong, extra and missing letters.
3. Rest the fingers on the keys: nothing is typed. Put a hand down on the keys: nothing, or one letter.
4. Lift a finger over a key: that key lights before you press.
5. With a hand on the keyboard, pinch: nothing is clicked, no ray, no wrist menu. Lift the hand away: rays are back.
6. Poke and pinch windows as before: are fingertips and rays steadier (default tracking instead of Fast Motion)?
7. Terminal: Esc, Tab, Ctrl+C (ctrl, then c), arrows, Shift + Page Up scrolls back.

## 6. Limits and next steps

- **Thresholds are first values.** Tunables: `TapDetector` (ContactTol 4 mm, ArmHeight 6.5 mm, MinSpeed 0.07 m/s,
  HoldTime 40 ms, InhibitTime 60 ms), `KeyDecoder` (Sigma 5.8 mm, PriorWeight 0.6, Protect 5 mm).
- A finger must lift about 7 mm to type again. Two presses of one hand closer than about 60 ms: the second is dropped.
- **No word-level correction yet.** That is the step that took Meta from about 7 % to 3 %. For text fields it can
  replace the word at the space when its presses were ambiguous; it needs an English word list in the APK (and
  an entry in `docs/LICENSES.md`). Not for the terminal.
- **A typing recorder** (fingertips + keys to a file on the headset, opt-in, never in masked fields) would let
  the thresholds be set from the owner's own typing instead of the model.
- Key size is fixed at real size; a size setting is easy to add if larger keys type better.
