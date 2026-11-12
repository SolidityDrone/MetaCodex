# Examples

`agent-git-gestures/`: the gesture plugin a fresh Claude Code wrote in one shot on 2026-11-07, given only the
skills the headset's Linux has (quest-gesture-plugins, quest-scripting, metacodex-context, Unity and Meta
skills) and the request "when I do a thumbs up with my right hand, open the git panel; when I push my open palm
away from me fast, close it" (`tools/agent-plugin-test.sh`). The Unity UI self-test replays it with simulated
hands: right thumbs-up opens git, a left thumbs-up does nothing, a fast palm push closes git. Since 2026-11-12
the gesture examples (`core-poses`, `swipes`) are not installed on a fresh headset, so the plugin brings its own
`poses/thumbs-up.json` (the skill says so too). Copy the folder to `/mnt/quest/plugins/` on the headset to use it.
