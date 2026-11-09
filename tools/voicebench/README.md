# Voice bench: decision model (router) vs LLM for voice commands

355 sentences (English and Italian; single commands, several in one sentence, misheard words, non-commands) with
the commands they must run, in order (`eval.jsonl`, made by `make_eval.py`). Pipelines are scored on the exact
result (commands, order and values; values parsed the same way for all).

1. `python3 tools/voicebench/make_eval.py` (only when the sentences change)
2. Editor export (registry, the exact LLM request, the fast path on every sentence):
   `echo voiceexport > unity/Bench/uitest-only && tools/unity-batch.sh BatchUiTest 20` -> `unity/Bench/voice/`
3. Today's LLM: `tools/qvac-dev.sh pc start`, `python3 tools/voicebench/llm_run.py`, `tools/qvac-dev.sh pc stop`
4. Routers: `~/mcxwork/voicebench/venv/bin/python tools/voicebench/bench.py [model filters]`
   (venv: fastembed, model2vec, numpy) -> table + `~/mcxwork/voicebench/{report.md,results.jsonl}`

Results: `results/`.
