# Voice bench, English only (2026-11-12, branch feat/polish-english)

The owner dropped Italian (2026-11-12). The bench is now the 180 English sentences of `eval.jsonl` (the 175 Italian
ones were removed; the English ones keep their ids). `bench.py` and `score_app.py` score English rows only and
know settings of kind `choice` (`voice_effect`, `speech_quality`).

## Baseline: the 2026-11-11 run, rescored on English only

The last app run (`unity/Bench/voice/app.jsonl` of the main checkout, 2026-11-11 19:47, the run behind
`2026-11-11-thread-sidebar.md`) scored again with the new `score_app.py` (no Editor, no model: the same answers,
the English rows only):

| pipeline | all | single | multi | noisy | none | ms / decision (PC) |
|---|---|---|---|---|---|---|
| app: fast path -> decision model (e5-small), 2026-11-11 | **93** | 96 | 88 | 88 | 93 | 20 (median), p90 31 |

Routes on the English rows: fast path 120, decision model 60. This equals the English column of
`2026-11-11-thread-sidebar.md` (93: 96 / 88 / 88 / 93), so the new scripts score English as before.

## Open: the run with this branch's registry

The registry lost its Italian samples, synonyms, phrases and none examples, and the decision model lost its Italian
words (fillers, verbs, joiners, numbers, on / off, more / less). The English rows must not drop below the baseline
above. Not run in this change (no Editor in this lane). The integrator runs, with the dev AI service up
(`tools/local-ai-dev.sh pc start`):

```
MCX_UITEST_ONLY=decider,voicewords,spokenreplies,voicefx,control,sidebar,context,qol,voiceexport,voicedecide  (tools/unity-batch.sh BatchUiTest)
~/mcxwork/voicebench/venv/bin/python tools/voicebench/score_app.py
```

and adds the table here: no drop in exact accuracy against 93 / 96 / 88 / 88 / 93.
