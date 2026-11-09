# The app's own pipeline with the decision model (2026-11-09, branch feat/decisional-model-commands)

What ran: the 355 bench sentences (`eval.jsonl`: English 180, Italian 175; single, composite, misheard,
chit-chat) through the app's C# pipeline in a batch Editor: the fast path (`VoiceCommands.FastMatchAll`), then
`VoiceDecider` with the real multilingual-e5-small q8_0 in the PC dev AI service (`/v1/embeddings`). Self-test
section `voicedecide` writes `unity/Bench/voice/app.jsonl`; `score_app.py` scores it with `bench.py`'s canon.
Baseline "0.7.1" = the fast path as it shipped (its export kept before this branch) followed by the cached answers
of Qwen3 0.6B (`llm_run.py`, the app's exact request).

## Result (exact: the right commands, in order, with the right values)

| pipeline | all | en/single | en/multi | en/noisy | en/none | it/single | it/multi | it/noisy | it/none | en | it | ms / decision (PC) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0.7.1: fast path -> LLM (Qwen3 0.6B) | 59 | 87 | 76 | 80 | 73 | 33 | 4 | 40 | 80 | 83 | 34 | LLM 209 (median) |
| app now: fast path -> decision model (e5-small) | **93** | 97 | 88 | 84 | 93 | 92 | 96 | 85 | 87 | **94** | **91** | decide 17 (median, HTTP + model), p90 30 |

Routes: fast path 121, decision model 234 (205 decided, 29 no command). Building the model (1373 examples, 17
commands) took 16 s with an empty cache; afterwards only new or changed examples are embedded.

## Runs on the way (same 355 sentences)

| run | change | all | en | it |
|---|---|---|---|---|
| 1 | first C# integration (registry with samples, Italian names, four new commands) | 85 | 89 | 82 |
| 2 | among commands within 0.03 of the best, the one that can run and that the sentence grounds (its own verb first, a panel / setting / preset it names); "un metro e mezzo" = 1.5 m; fuzzy names only from 5 letters ("what" was read as "chat") | 94 | 94 | 94 |
| 3 | **honest**: 36 samples and "none" examples were bench sentences word for word (and 8 near copies); all replaced by other sentences, a self-test now fails on any copy | **93** | **94** | **91** |

Run 3 is the number to quote. The shell's few-shot examples for the LLM (`GeneralExamples`, older than this branch)
still contain a few bench sentences ("good morning", "what's the weather like"); the LLM baseline sees the same
examples in its prompt, so both pipelines share that advantage.

Scoring note (both pipelines): `set_setting focus on|off` counts as `set_focus on|off`; the app runs `SetFocus` for
both (`SetSettingByVoice`), and the existing self-tests already accept either.

## The 26 misses (run 3)

- Ambiguous between opening a panel and acting, or between two panels (4): "show the focus options", "mostra le
  opzioni del focus", "open the speech settings" (voice vs settings), "apri le impostazioni della voce".
- Open vs close confused (6): "I want to change the settings", "apri la cassaforte" (close_panel 0.942 vs
  open_panel 0.903: outside the 0.03 margin, so the verb cue could not help), "chiudi git", "mostra il controllo
  versione", and the second part of two composites ("... e apri la cassaforte").
- Workspace vs threads (2): "vai al progetto sandybridge", "apri il workspace del progetto alpha" (the threads panel
  has the synonym "progetti" and wins by grounding).
- Misheard beyond repair (6): "Ternon ambent forgoose", "Obrans scripts", "open the fails", "apri i fail", "trova il
  tred sulla tastiera", "Turn off from the infoguse".
- Composites with a free-text part (2): "switch to the metacodex workspace and open the threads", "find the thread
  about the keyboard and open the files".
- Values (3): "set the radius ... and the softness to half a meter", "scurisci un po' di più la stanza", "togli i click".
- Chit-chat taken as a command (3): "What can you do for me?", "cosa sai fare", "lascia stare".

Next if needed: let the clause's own verb pick between sibling commands (open / close) over a wider margin; more
samples where panel and action meet; a bigger encoder (300-400M) is an option the owner allowed, at ~2-3x the time
and memory.
