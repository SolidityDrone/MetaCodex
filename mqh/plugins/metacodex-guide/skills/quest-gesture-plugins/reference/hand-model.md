# Hand model (what detectors and poses see)

Source of truth: `unity/Assets/MetaCodex/Runtime/Hands/Gestures.cs` (GestureEngine, PoseSpec).

## Space and units

- Unity world space: metres, **left-handed, +Y up**. World X/Z depend on where tracking started, so
  never compare world X/Z directly: use the head-relative values below.
- **Head yaw frame** (used by `h.rel`, `h.vel`, and the directions left/right/forward/back): x = the
  user's right, y = world up, z = where the user faces, flattened (nodding or looking down does not
  change it).
- Joints come from Meta's OVRSkeleton (OpenXR hand joints `XRHand_*`): wrist, palm, thumb proximal and
  tip, and for index/middle/ring/little the proximal knuckle (MCP) and the tip.

## Fields of a hand table `h` (detectors, `hands.get(hand)`)

| Field | Type | Meaning |
|---|---|---|
| `side` | string | `left` or `right` |
| `tracked` | bool | the hand is tracked (one-hand detectors only ever see tracked hands) |
| `confident` | bool | Meta reports high confidence |
| `pinch` | 0..1 | thumb-index pinch strength (OVR) |
| `pinching` `grabbing` `pointing` `open` `palmUp` | bool | the built-in gestures' current state |
| `curl.thumb` `.index` `.middle` `.ring` `.little` | 0..1 | **0 straight, 1 curled** |
| `x` `y` `z` | m | palm centre, world |
| `rel.x/y/z` | m | palm relative to the eyes, head yaw frame (z ≈ 0.3-0.5 for a hand in front) |
| `vel.x/y/z` | m/s | palm velocity, head yaw frame (smoothed over ~50 ms) |
| `speed` | m/s | length of the velocity |
| `palmDir.x/y/z` | unit | out of the palm, world |
| `thumbDir.x/y/z` | unit | thumb knuckle to thumb tip, world |
| `fingerDir.x/y/z` | unit | wrist to middle knuckle, world |
| `palmFacing` | string | nearest of up/down/left/right/forward/back, where forward/back become `away`/`head` when the palm faces roughly away from / towards the eyes (so a palm pushed forward in front of you reads `away`, a palm shown to your face reads `head`) |
| `thumbPointing` | string | same, for the thumb |
| `size` | m | wrist to middle knuckle (about 0.08-0.10) |

## How curl is measured

- Index..little: `ratio = |wrist→tip| / |wrist→knuckle|` (about 1.9 straight, about 1.0 folded into the
  palm); `curl = clamp01((1.75 - ratio) / 0.65)`.
- Thumb: `ratio = |thumb tip → middle knuckle| / hand size`; `curl = clamp01((0.75 - ratio) / 0.45)`:
  folded across the palm → about 1; out to the side or up (thumbs up, call-me, finger gun) → 0.
  A thumb resting on the side of a fist reads 0.3-0.9, so for "fist" poses leave the thumb out.

## Built-in gestures (always there)

| Name | Hands | Phases | Recognised when |
|---|---|---|---|
| `pinch` | left/right | start, end | OVR pinch ≥ 0.85 (ends < 0.6) |
| `grab` | left/right | start, end | all four fingers curl > 0.62 (no pinch) |
| `point` | left/right | start, end | index < 0.3, middle and ring > 0.55, little > 0.5 |
| `open` | left/right | start, end | all four fingers < 0.3 |
| `palm-up` | left/right | start, end | `open` and palm normal y > 0.75 |
| `clap` | both | tap | palms closer than 7.5 cm while approaching faster than 0.35 m/s |
| `select` | left/right | tap | the select tool drew a rectangle; `detail` = "panel x y w h" |

Several can be active at once (`grab` is also true during `thumbs-up`: the fingers are curled).

## Examples (NOT installed; copy their fields into your own plugin)

The app ships two example plugins that are off by default (GESTURES > ALL > EXAMPLES, + ADD; or, after the
user's OK, `mcx-ui plugin example core-poses|swipes`). Never assume they are there.

- `core-poses` (poses only): `thumbs-up`, `thumbs-down`, `victory`, `horns`, `call-me`.
- `swipes` (Lua detectors): `swipe-left`, `swipe-right`, `swipe-up` (open hand, fast).

Their poses, ready to copy into `poses/<name>.json` of your plugin (`hold` 0.15, `cooldown` 0.5,
`hand` "any", `tolerance` 0.35 in all of them):

| Name | `fingers` | Extra |
|---|---|---|
| `thumbs-up` | `{ "thumb": 0, "index": 1, "middle": 1, "ring": 1, "little": 1 }` | `"thumbPointing": "up"` |
| `thumbs-down` | `{ "thumb": 0, "index": 1, "middle": 1, "ring": 1, "little": 1 }` | `"thumbPointing": "down"` |
| `victory` | `{ "index": 0, "middle": 0, "ring": 1, "little": 1 }` | |
| `horns` | `{ "index": 0, "middle": 1, "ring": 1, "little": 0 }` | |
| `call-me` | `{ "thumb": 0, "index": 1, "middle": 1, "ring": 1, "little": 0 }` | |

Swipes: `h.open and h.vel.x < -1.1 and math.abs(h.vel.y) < 0.7` (left; `> 1.1` right), up:
`h.open and h.palmFacing == "up" and h.vel.y > 1.1 and math.abs(h.vel.x) < 0.7`; `hold = 0`, `cooldown = 0.7`.

## Timing

Poses: checked every frame. Lua detectors: about 36 times a second (Settings > gestures detector rate).
`hold` counts from the first frame the condition is true; `end` fires on the first false frame.
Hand tracking loses hands at the edge of the view and during the system gesture (palm-up pinch): the
app ends all of that hand's gestures then.
