---
name: quest-native-quality
description: MANDATORY with quest-native-hands-first - read before writing code for any native Quest app made in MetaCodex. The framing that turns a one-line request into a curated, high-quality app instead of a few lines thrown into the build. Use when the user asks for a native app, game, tool, toy or demo; asks for something polished, beautiful, curated or good-looking; when a native app looks plain, has bad menus or small text, feels wrong on first use, or needs a review. Sets the brief (DESIGN.md), the six build passes with their checks, the self-review checklist, the anti-patterns and how to talk to the user.
---

# Quality: a curated native app, not a few lines in a build

Goal: the person puts the headset on, sees one clear thing to do, does it with the hands, and sees a result that
looks designed. Get there with a brief, six passes that each end in a check and a look, and a review before you
call the app done. The build, install and launch steps are in quest-native-apps. This skill frames them and does
not repeat them.

## 1. Write the brief first: DESIGN.md in the app folder

Write it before any code. Keep it to one screen. Update it when the plan changes.

```
Purpose: <one sentence: what the app is for>
Moments: arrive: <...>  act: <...>  leave: <...>   (the three things the user must feel)
Look: palette and mood from quest-native-ui-design; light, materials and shadows from quest-native-rendering
Actions: <action> | <hand input: pinch, poke, aim ray or grab> | <feedback, and in which frame>
Content: <each primitive or model>; for a model, its source and licence
States: start, playing, paused, done, error: what the user sees in each
```

## 2. Build in six passes

Each pass ends the same way: `mcx-app check <dir>`, `mcx-app build <dir>`, `mcx-app verify <dir>` until it says
`RESULT: verify ok`, then `mcx-app run <dir>` (install and launch request). You cannot see the headset, so the look
belongs to the user: ask one short question per pass.

| Pass | Build | Check before the next pass | Ask the user |
|---|---|---|---|
| 1 Greybox | scale in metres, placement, comfort; plain shapes | panels 1.0-1.5 m away, used with the aim ray; things to touch 40-60 cm from the hand; nothing head-locked | "Can you reach and see everything?" |
| 2 Interaction | every action in DESIGN.md with its hand input | each action works and changes something in the frame the pinch starts (`pinchStarted`) | "Try each action: did each one react?" |
| 3 Look | lights, materials, contact shadows, UI theme, motion | no flat unlit colour; no pure white or black; text uses the theme | "Does it look like one app, with the room lights on and off?" |
| 4 Polish | start screen, first-use hint, transitions, empty and error states, icon, name | all five states exist; icon and label are set | "Start to finish once: anything odd?" |
| 5 Performance | quest-native-performance: frame logger, levels, budgets | `missed` 0 over 30 s of normal use | "Use it for a minute; send a report if it stutters." |
| 6 Self-review | section 3 | every item is yes | the final message |

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

## 4. Use the user's report

A report arrives as a draft with a picture and the log (`log <Label>`).

1. Read the picture first. Write one line per problem: position, size, colour, text or motion.
2. Read the log: the newest `frames` line, the state lines, and the last line before any crash.
3. Fix the cause, then re-run the pass that owns it. Tell the user what changed, in one or two sentences.

## 5. Anti-patterns and their fixes

| Anti-pattern | Why it fails | Fix |
|---|---|---|
| A lone cube, or one shape as the whole app | Nothing to do, nothing to look at | Build the three moments: a floor or table, a focus object, a panel, a light |
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
