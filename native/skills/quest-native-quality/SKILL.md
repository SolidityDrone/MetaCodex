---
name: quest-native-quality
description: MANDATORY with quest-native-hands-first - read before writing code for any native Quest app made in MetaCodex. The framing that turns a one-line request into a curated, high-quality app instead of a few lines thrown into the build. Use when the user asks for a native app, game, tool, toy or demo; asks for something polished, beautiful, curated or good-looking; when a native app looks plain, has bad menus or small text, feels wrong on first use, or needs a review. Sets the brief (DESIGN.md), the six build passes with their checks, the self-review checklist, the anti-patterns and how to talk to the user.
---

# Quality: a curated native app, not a few lines in a build

Goal: the person puts the headset on, sees one clear thing to do, does it with the hands, and sees a result that
looks designed. Get there with a brief, passes that each end in a check, and a review before the first install.
The build, install and launch steps are in quest-native-apps. This skill frames them and does not repeat them.
The look itself (palette, light, materials, motion, what reads over the room) is in **quest-native-art-direction**.

## 1. Write the brief first: DESIGN.md in the app folder

Write it before any code. Keep it to one screen. Update it when the plan changes. Decide every line yourself.

```text
Purpose: <one sentence: what the app is for, in the user's words>
Moments: arrive: <the first second>  act: <the main action>  leave: <the result or the end>
Look card: <look name>; palette body/support/support/accent <4 sRGB hex>; light <key direction, warm or cool>;
           materials <body preset, accent preset, glow where>; motion <arrive, idle, feedback>; UI accent <hex>
Stage: <where: the table / the floor / 1.2 m ahead>; <its size in metres>; <the hero and its size>
Actions: <action> | <hand input: pinch, poke, aim ray or grab> | <feedback, and in which frame>
Content: <each primitive or model and its size in metres>; for a model, its source and licence
States: start, playing, paused, done, error: what the user sees in each
Budget: <draw calls, triangles, sprites>; the numbers in quest-native-performance
```

## 2. Build in passes: blockout, look, motion, UI, polish, self-review

Each pass ends the same way: `mcx-app check <dir>`, `mcx-app build <dir>`, `mcx-app verify <dir>` until it says
`RESULT: verify ok`. Do all six passes before the first `mcx-app run <dir>`: you cannot see the headset, so the
first launch must already be the designed app. Exception: an app with a risky feature (depth, the room, a large
model) may launch once after pass 1; tell the user "this is the blockout, the look comes next".

| Pass | Build | Check before the next pass |
|---|---|---|
| 1 Blockout | the stage in metres from the brief; plain shapes at real size; every action of DESIGN.md wired to its hand input | things to touch 40-60 cm from the hand; panels 1.0-1.5 m; nothing head-locked; each action changes something in the frame the pinch starts (`pinchStarted`) |
| 2 Look | the look card: palette, key light, materials (lit, rim, glow), contact shadows, glow sprites on the accents | no flat unlit colour; no default grey; no pure white or black; the accent on few things; every grounded object has a shadow |
| 3 Motion | the first second (things grow in, staggered), idle motion, feedback on every action, leave and reset; easing and damping from `mcx_gfx.h` | nothing pops in or out in one frame; nothing jitters; motion is slow and has one purpose |
| 4 UI | start panel, pause, result, a first-use hint, a HUD only if needed; the UI accent from the look card (quest-native-ui-design) | text 2.6 cm at 1 m or larger; the panel beside the hero, not over it; Primary on the main action |
| 5 Polish and performance | empty and error states in words, icon, label, version; the frame logger (quest-native-performance) | all five states exist; draw calls and triangles inside the budget of the brief |
| 6 Self-review | section 3, and the critic's look check of quest-native-art-direction | every item is yes; then install and launch |

After the first launch, ask ONE short question about what the user sees ("Does the sun read as the light, and can
you reach the planets?"). Each report goes back to the pass that owns the problem (section 4).

## 3. Self-review: every item must be yes

- [ ] Nothing is head-locked: panels and objects stay in the room or on the table, and move only when the user walks away.
- [ ] Text is at least 2.5 cm tall at 1 m, and taller further away, so it reads from where the user stands.
- [ ] Every action has a hand input (quest-native-hands-first) and feedback in the frame it starts.
- [ ] No debug text on screen: no state names, counters, depth or FPS lines. Those go to `mcx::Log`.
- [ ] Objects are grounded: each rests on a surface, or floats for a reason and has a soft shadow.
- [ ] Lit, not flat: a key light, ambient light and contact shadows.
- [ ] No pure white or pure black anywhere; colours come from the theme.
- [ ] A start screen shows the name and one way to start; the first-use hint shows once.
- [ ] Paused (focus lost), done and error states exist and say what to do next.
- [ ] Passthrough shows where nothing is drawn: alpha 0 from `app.clearAlpha()` where the room should show.
- [ ] The frame budget holds: `missed` 0 over 30 s of normal use (quest-native-performance).
- [ ] The icon is set (`res/drawable/icon.png`, recipe 6 in quest-native-recipes); the label and package are the app's own.
- [ ] `android:versionCode` is raised for each new version you hand the user, never lowered; `versionName` matches your reply.
- [ ] Every model and texture is listed in DESIGN.md with its licence and source.
- [ ] The source is split (section 6) and `main.cpp` only runs the loop.
- [ ] `mcx-app verify` says `RESULT: verify ok`.

Then look at the app as a critic who sees it for the first time, and fix what they would say:
- "Which thing is the point?" One hero, larger or brighter than the rest; the eye goes there first.
- "It is all one colour" or "it looks like a debug build": the palette of the look card, one accent, lit materials.
- "It floats" or "it is the wrong size": real sizes in metres, resting on a real surface, a contact shadow.
- "It just appeared": the first second grows things in; nothing pops.
- "It is dead": one idle motion on the hero; every action answers in its frame.
- "I cannot read it": text sizes from quest-native-ui-design; the panel faces the user, at or below eye level.
- "It is in my face": nothing nearer than 0.4 m unless the user brought it there; the UI does not cover the hero.

Evidence levels: in your messages, say how you know each claim. **seen**: the user saw it, or a report picture shows
it. **built**: `RESULT: verify ok`, not yet seen. **assumed**: neither (for example a colour you could not check).
Never say "it looks great" about something only built: say what the user should look at.

## 4. Use the user's report

A report arrives as a draft with a picture and the log (`log <Label>`).

1. Read the picture first. Write one line per problem: position, size, colour, text or motion.
2. Read the log: the newest `frames` line, the state lines, and the last line before any crash.
3. Fix the cause, then re-run the pass that owns it. Tell the user what changed, in one or two sentences.

## 5. Anti-patterns and their fixes

| Anti-pattern | Why it fails | Fix |
|---|---|---|
| A greybox as the first launch | The first impression is "unfinished"; the user stops trying | All six passes before the first install (section 2) |
| A lone cube, or one shape as the whole app | Nothing to do, nothing to look at | Build the three moments: a floor or table, a focus object, a panel, a light |
| Every object in the MetaCodex cyan | The app has no identity; nothing is the hero | The look card of quest-native-art-direction: cyan is the UI accent of one look, not a default |
| The example's HUD lines left in (depth, hands, room state) | Debug text in the user's view | Remove the HUD; log the state with `mcx::Log` |
| Flat unlit colours | Looks like a debug build | Key light, ambient, contact shadows (quest-native-rendering) |
| UI at 2 m with small text | Unreadable; the eyes strain | Panels at 1.0-1.5 m; text 2.5 cm tall at 1 m, larger further away |
| Panels that follow the head | They swim and cause discomfort | Place once in the room; move only when the user walks away, and glide (the lazy follow in quest-native-recipes, recipe 1) |
| Pure white or pure black | Harsh, clipped, hard to read over the room | The theme's text and glass colours (quest-native-ui-design) |
| No start screen | The user does not know what to do | A start state: the name, one line, one pinch |
| Magic numbers for sizes | Nobody can tune the look | One `config.h`: `constexpr float kPanelWidth = 0.6f; // metres` |
| One giant main.cpp | Hard to change and to review | Split the files (section 6) |
| Debug text on screen | Shows internals to the user | `mcx::Log` |
| No empty or error state | Looks broken when a permission is missing | Say the fix in words: "run Space Setup", "allow spatial data" |
| Template label, package or icon | Looks like a test build | Own label, own package, own icon |

## 6. Code layout

- `src/main.cpp`: the `mcx::App` loop only (Init, Pump, BeginFrame, BeginEye and EndEye, EndFrame, Shutdown).
- `src/scene.cpp` and `scene.h`: what is drawn, placed in metres.
- `src/ui.cpp` and `ui.h`: panels, buttons, text (quest-native-ui-design).
- `src/input.cpp` and `input.h`: hand data to actions (pinch, poke, aim, grab). Read input only while `f.focused`.
- `src/logic.cpp` and `logic.h`: state and rules, no GL calls.
- `src/config.h`: sizes in metres, colours, timings. No other magic numbers.

`mcx-build` compiles every `.cpp` under `src/`, so the split needs no extra steps.

## 7. Talk to the user

- Before `mcx-app install`: one sentence on what the app will do, in the user's words.
- After `RESULT: requested launch of ...`: "ready: tap LAUNCH in MetaCodex. Try <first action>. Look for <what should appear>."
  Keep the menu line from quest-native-apps step 10.
- After a report: say what you changed, in one or two sentences.
- Do not mention controllers or buttons unless the user asked for them (quest-native-hands-first).

Done means: `RESULT: verify ok`, installed, launched, the user saw it work, and the app's name is in the Library
(quest-native-apps).
