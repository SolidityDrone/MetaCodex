# Git gestures

| Gesture | How | Does |
|---|---|---|
| `thumbs-up` (right hand only) | fist with the thumb pointing up | opens the git panel |
| `palm-push` (either hand) | open hand, palm facing away from you, pushed forward fast (about 1 m/s) | closes the git panel |

- `thumbs-up` comes from the `core-poses` starter plugin: keep that plugin enabled.
- Watch it live: Gestures panel > LIVE (finger bars, active gestures).
- Turn it off: Gestures > PLUGINS, or `mcx-ui plugin disable git-gestures`.
- Tuning (in `main.lua`): lower `h.vel.z > 1.0` if the push does not register; raise it if it fires by accident.
  To restrict the push to one hand, change `hand = "any"` to `"right"` or `"left"`.
