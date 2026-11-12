"""Today's LLM on every bench sentence: the app's exact request (unity/Bench/voice/llm-request.json, exported by the
Editor) sent to the dev AI service (tools/local-ai-dev.sh pc start; url + token in unity/Bench/qvac-dev.json).
Writes ~/mcxwork/voicebench/llm.jsonl: {id, content, ms}. One warm-up request first (not counted).
"""
import json, os, sys, time, urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.expanduser("~/mcxwork/voicebench/llm.jsonl")
dev = json.load(open(os.path.join(REPO, "unity", "Bench", "qvac-dev.json")))
req = json.load(open(os.path.join(REPO, "unity", "Bench", "voice", "llm-request.json")))
rows = [json.loads(l) for l in open(os.path.join(REPO, "tools", "voicebench", "eval.jsonl"), encoding="utf-8") if l.strip()]


def ask(text):
    body = json.loads(json.dumps(req))
    body["messages"][-1]["content"] = text
    r = urllib.request.Request(dev["url"].rstrip("/") + "/v1/chat/completions", data=json.dumps(body).encode(),
                               headers={"Content-Type": "application/json", "Authorization": "Bearer " + dev["token"]})
    t0 = time.perf_counter()
    with urllib.request.urlopen(r, timeout=120) as f: out = json.load(f)
    ms = (time.perf_counter() - t0) * 1000
    return out["choices"][0]["message"].get("content") or "", ms


ask("open the files")  # warm-up: loads the model and fills the prompt cache
done = {}
if os.path.exists(OUT):
    for l in open(OUT, encoding="utf-8"):
        if l.strip(): d = json.loads(l); done[d["id"]] = d
with open(OUT, "a", encoding="utf-8") as f:
    for i, row in enumerate(rows):
        if row["id"] in done: continue
        content, ms = ask(row["text"])
        f.write(json.dumps({"id": row["id"], "content": content, "ms": round(ms, 1)}, ensure_ascii=False) + "\n")
        f.flush()
        if i % 25 == 0: print(f"{i}/{len(rows)} {row['text'][:40]!r} -> {content!r} {ms:.0f} ms", flush=True)
print("done ->", OUT)
