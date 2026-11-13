# Testing a gesture plugin

## On the headset (inside the Linux userland: an MQH thread or a terminal)

```bash
mcx-ui plugin list                    # your plugin: enabled=True, error=-
mcx-ui plugin log finger-gun          # "loaded (1 handlers, 1 poses, 0 detectors)" or the Lua error
mcx-ui gesture list                   # your gesture names, kind pose/lua, active=False
mcx-ui gesture finger-gun right start # simulate the event: the reaction must happen
mcx-ui ui list                        # panels and open=True/False after the reaction
mcx-ui script eval 'local h = hands.get("right"); return h.tracked, h.curl.thumb, h.palmFacing'
mcx-ui gesture bind finger-gun open:web   # no-code binding (GESTURES > BIND shows it)
mcx-ui plugin disable finger-gun      # off (enable / delete / reload too)
```

`mcx-ui` talks to the app's control port (127.0.0.1:47800, token in /mnt/quest/control.token). If it
says the port is closed, the app is not running. The simulation (`gesture <name> <hand> start`) tests
the reaction only; recognition needs real hands: ask the user to make the gesture while the
MQH > GESTURES > LIVE tab is open (finger bars, palm/thumb direction, active gestures, detector cost).

## Off the headset (developer PC, Unity Editor batch tests)

The Editor's self-test (`tools/unity-batch.sh BatchCheck`) drives simulated hands:
`hand pose right 0 1 1 1 1 up` (thumb index middle ring little curls, thumb direction) then
`gesture tick 0.4` advances the engine; the same control commands work there. `tools/agent-plugin-test.sh`
asks a fresh agent to write a plugin from this skill and replays it in the Editor.

## When it misfires

- Never fires: first `mcx-ui gesture list`: is the name defined at all (by your plugin or a built-in)?
  Then record the pose (GESTURES > RECORD) and compare the curls with yours; widen
  `tolerance` (max 0.45) or drop a finger you do not care about; relax `directionMin` to 0.5.
- Fires too often: add `hold` (0.2-0.3), a `cooldown`, a palm/thumb direction, or require
  `h.confident`.
- Fires with another gesture: make the two poses differ in at least two fingers, or check
  `gesture.active("other")` in a detector and return false.
