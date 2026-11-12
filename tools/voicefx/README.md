# Voice effect: prototype and retuning

The app's effect is `unity/Assets/MetaCodex/Runtime/AI/VoiceFx.cs` (pure C#, run on the worker thread that fetched
each sentence). This folder is the Python prototype it was made from, kept to retune it by ear.

| File | What it is |
|---|---|
| `fx.py` | The chain in numpy/scipy. `PRESETS` = `VoiceFxParams.For` in C#, one number for one number. `render` writes every preset for the listening samples, `metrics` measures them, `bench` times each stage. |
| `asr.py` | Intelligibility proxy: the dev AI service's speech-to-text on each preset (or a preset with one stage left out), word error rate against `texts.txt`. |
| `texts.txt` | 12 short replies for `asr.py`. |

Samples folder: `$MCX_VOICEFX_SAMPLES`, by default `mcx-voicefx-samples` in the Windows user folder this checkout is
in. It holds the service's own replies `dry-F1-<n>.wav`, `dry-M1-<n>.wav` for these 4 texts (n = 1-4):
1 "Opened the agents panel." 2 "Three threads are working. Claude finished the login fix."
3 "The build finished with no errors. Two threads are waiting for you, and the terminal in the second workspace has a new message."
4 "Done. Nothing needs your attention."
`fx.py render` adds `clean-` (effect off), `adjutant-`, `starship-`, `comm-` files next to them. Nothing in that
folder is committed (WAVs).

Retuning:
1. Change a number in `PRESETS`, `python3 fx.py render`, listen (clean, adjutant, starship, comm on 2 and 3).
2. `python3 asr.py clean <preset>` (dev service running): a preset more than about 3 points worse than clean costs words.
3. Copy the numbers into `VoiceFxParams.For`, then `tests/voicefx/run-voicefx-tests.sh`: it checks levels and
   compares the C# output with the renders (correlation above 0.97 per preset).
