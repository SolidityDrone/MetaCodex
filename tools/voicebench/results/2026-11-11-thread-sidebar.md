# Voice bench after THREAD's sidebar commands (2026-11-11, branch fix/thread-sidebar-and-orb)

What changed in the registry: `thread_sidebar` (action open / close, tab changes / files / git / status / views) and
`init_git` were added; the setting `workspace_module` was removed (the WORKSPACE module is gone). Both new commands are
enabled only while a thread is open (`init_git` only while its folder is no repository), so the bench, which runs with
no thread open, does not offer them: the run checks that nothing else moved.

Same 355 sentences, the app's pipeline (fast path -> decision model, multilingual-e5-small q8_0 in the PC dev AI service),
`MCX_UITEST_ONLY=decider,voiceexport,voicedecide`, scored with `tools/voicebench/score_app.py`.

| pipeline | all | en/single | en/multi | en/noisy | en/none | it/single | it/multi | it/noisy | it/none | en | it | ms / decision (PC) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| branch start (4f159db, before the sidebar) | 91 | 96 | 88 | 88 | 93 | 88 | 96 | 85 | 80 | 93 | 88 | 20 (median), p90 35 |
| with the sidebar commands | **91** | 96 | 88 | 88 | 93 | 88 | 96 | 85 | 80 | **93** | **88** | 20 (median), p90 34 |

Routes: fast path 121, decision model 234, in both runs; the list of missed sentences is identical (no drop, English or
Italian). The 2026-11-09 file's 93 / 94 / 91 predates the commands added since (session questions, launches, ...): the
branch start is the baseline to compare with.

The new commands themselves: `VoiceCommands.Lint` clean for both (self-test `sidebar`, and `decider`: every shell command
complete, no sample copied from the bench); the fast path runs "close the thread sidebar", "open the sidebar on the git
tab", "show the status tab of this thread", "chiudi la barra laterale", "apri la scheda file del thread" and "make this
folder a git repository" (self-test `sidebar`).
