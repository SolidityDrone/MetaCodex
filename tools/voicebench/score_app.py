"""Scores the app's own pipeline on the bench: unity/Bench/voice/app.jsonl, written by the Editor section `voicedecide`
(fast path -> VoiceDecider with the real model in the dev AI service). Same canon and scoring as bench.py. English
only since 2026-11-12: rows of other languages (an older app.jsonl has the 175 Italian ones) are not scored or counted.

Baseline "0.7.1" = the fast path as it shipped (a copy of its export in ~/mcxwork/voicebench/before/fastpath.jsonl)
followed by the LLM's answers (~/mcxwork/voicebench/llm.jsonl, llm_run.py).

Run: ~/mcxwork/voicebench/venv/bin/python tools/voicebench/score_app.py
Writes ~/mcxwork/voicebench/app-report.md and app-misses.txt (every sentence the app gets wrong, with its clauses).
"""
import json, os, statistics, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench  # noqa: E402  (loads eval.jsonl, the registry export, the LLM answers)

APP = os.path.join(bench.REPO, "unity", "Bench", "voice", "app.jsonl")
BEFORE = os.path.join(bench.WORK, "before", "fastpath.jsonl")
EN_IDS = {r["id"] for r in bench.EVAL}  # bench.EVAL holds the English rows only
app = {d["id"]: d for d in (json.loads(l) for l in open(APP, encoding="utf-8") if l.strip()) if d["id"] in EN_IDS}
before = {}
if os.path.exists(BEFORE):
    before = {d["id"]: d for d in (json.loads(l) for l in open(BEFORE, encoding="utf-8") if l.strip())}


def app_pred(row):
    a = app.get(row["id"])
    return [bench.canon(m["command"], m["args"]) for m in (a or {}).get("matches") or []]


def before_pred(row):
    f = before.get(row["id"])
    if f and f.get("matches") is not None:
        return [bench.canon(m["command"], m["args"]) for m in f["matches"]]
    llm = bench.LLM.get(row["id"])
    return bench.parse_llm(llm["content"]) if llm else []


def main():
    res, lat = [], {}
    if before and bench.LLM:
        r, _ = bench.evaluate("0.7.1: fast path -> LLM (Qwen3 0.6B)", before_pred)
        res += r
        ms = [bench.LLM[x["id"]]["ms"] for x in bench.EVAL if (before.get(x["id"]) or {}).get("matches") is None]
        lat[r[0]["pipeline"]] = f"LLM {statistics.median(ms):.0f} (median, PC)"
    r, _ = bench.evaluate("app now: fast path -> decision model (e5-small)", app_pred)
    res += r
    dms = sorted(a["ms"] for a in app.values() if a.get("via") == "decide")
    if dms:
        lat[r[0]["pipeline"]] = f"decide {statistics.median(dms):.0f} (median, PC, HTTP + model), p90 {dms[int(len(dms) * 0.9)]:.0f}"
    t = bench.table(res, lat)
    print(t)
    via = {}
    for a in app.values(): via[a.get("via")] = via.get(a.get("via"), 0) + 1
    print("routes:", via)
    with open(os.path.join(bench.WORK, "app-report.md"), "w", encoding="utf-8") as f: f.write(t + "\n")
    with open(os.path.join(bench.WORK, "app-misses.txt"), "w", encoding="utf-8") as f:
        for x in res:
            if not x["pipeline"].startswith("app now") or x["exact"]: continue
            row = next(e for e in bench.EVAL if e["id"] == x["id"])
            a = app.get(x["id"], {})
            f.write(f"{x['id']} [{x['lang']}/{x['kind']}] {x['text']}\n  gold: {row['gold']}\n  app ({a.get('via')}): {x['pred']}\n")
            for c in a.get("clauses") or []:
                f.write(f"    “{c['text']}” -> {c['command']} {c['score']} (2nd {c.get('second')} {c.get('second_score')}){' ' + c['note'] if c.get('note') else ''}\n")


if __name__ == "__main__":
    main()
