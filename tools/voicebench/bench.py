"""Voice bench: today's pipeline (fast path -> LLM) against Jev-style embedding routers (fast path -> router).

A router decides like Jev: the options are the app's commands (and "none"), each represented by example sentences built
from the command registry the app exports (phrase templates expanded, examples, descriptions); a sentence is split into
clauses (and / then / commas, English and Italian), each clause is embedded and scored against every command's
examples (cosine, best example), the best command wins unless it scores under a threshold (then: no command). Values
are read by rules (numbers, units, percent, on / off, more / less, panel and setting names), the same parser for every
pipeline, so the comparison is about choosing the right commands in the right order.

Inputs: tools/voicebench/eval.jsonl, unity/Bench/voice/{registry.json, fastpath.jsonl} (Editor: uitest section
voiceexport), ~/mcxwork/voicebench/llm.jsonl (llm_run.py). Run with ~/mcxwork/voicebench/venv/bin/python.
Output: the table on stdout, ~/mcxwork/voicebench/report.md and results.jsonl (every sentence, every pipeline).
"""
import json, math, os, random, re, sys, time, unicodedata
import numpy as np

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
WORK = os.path.expanduser("~/mcxwork/voicebench")
EVAL = [json.loads(l) for l in open(os.path.join(REPO, "tools", "voicebench", "eval.jsonl"), encoding="utf-8") if l.strip()]
REG = json.load(open(os.path.join(REPO, "unity", "Bench", "voice", "registry.json"), encoding="utf-8"))
FAST = {d["id"]: d for d in (json.loads(l) for l in open(os.path.join(REPO, "unity", "Bench", "voice", "fastpath.jsonl"), encoding="utf-8") if l.strip())}
LLM = {}
if os.path.exists(os.path.join(WORK, "llm.jsonl")):
    LLM = {d["id"]: d for d in (json.loads(l) for l in open(os.path.join(WORK, "llm.jsonl"), encoding="utf-8") if l.strip())}
CMDS = {c["name"]: c for c in REG["commands"]}
SETTINGS = {s["id"]: s for s in REG["settings"]}


def fold(s):
    s = unicodedata.normalize("NFKD", s.lower())
    s = "".join(ch for ch in s if not unicodedata.combining(ch))
    s = s.replace("'", " ").replace("’", " ")
    s = re.sub(r"[^a-z0-9%. ]+", " ", s)
    return re.sub(r"\s+", " ", s).strip()


def lev(a, b):
    if abs(len(a) - len(b)) > 2: return 9
    d = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        prev, d[0] = d[0], i
        for j, cb in enumerate(b, 1):
            cur = min(d[j] + 1, d[j - 1] + 1, prev + (ca != cb))
            prev, d[j] = d[j], cur
    return d[len(b)]


# ------------------------------------------------------------------------------------------ clauses
FILLERS = ["hey metacodex", "ok", "okay", "hey", "please", "could you", "can you", "would you", "i want to", "i would like to",
           "id like to", "lets", "now", "per favore", "puoi", "potresti", "ehi", "vorrei", "adesso"]
JOIN = re.compile(r"\s*(?:[,;]|\band then\b|\band also\b|\bafter that\b|\bthen\b|\balso\b|\band\b|\be poi\b|\bpoi\b|\banche\b|\bed\b|\be\b)\s*")


def clauses(text):
    t = text.lower()
    t = re.sub(r"(\d),(\d)", r"\1.\2", t)
    t = re.sub(r"\band a half\b", " andahalf ", t)
    t = re.sub(r"\be mezzo\b", " emezzo ", t)
    parts = [p.strip(" .!?") for p in JOIN.split(t)]
    out = []
    for p in parts:
        p = p.replace("andahalf", "and a half").replace("emezzo", "e mezzo").strip()
        f = fold(p)
        changed = True
        while changed:
            changed = False
            for fl in FILLERS:
                if f == fl: f = ""; break
                if f.startswith(fl + " "): f = f[len(fl) + 1:]; changed = True
                if f.endswith(" " + fl): f = f[: -len(fl) - 1]; changed = True
        if f: out.append(f)
    return out


# ------------------------------------------------------------------------------------------ values (English + Italian)
UNITS_EN = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
            "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"]
TENS_EN = {"twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80, "ninety": 90}
WORDS_IT = {"zero": 0, "uno": 1, "un": 1, "una": 1, "due": 2, "tre": 3, "quattro": 4, "cinque": 5, "sei": 6, "sette": 7, "otto": 8,
            "nove": 9, "dieci": 10, "venti": 20, "trenta": 30, "quaranta": 40, "cinquanta": 50, "sessanta": 60, "settanta": 70,
            "ottanta": 80, "novanta": 90, "cento": 100}
MORE = ["a bit more", "a little more", "more", "up", "higher", "bigger", "biger", "larger", "stronger", "darker", "wider", "louder",
        "brighter", "increase", "raise", "piu grandi", "piu grande", "di piu", "piu", "aumenta", "alza", "ingrandisci", "scurisci"]
LESS = ["a bit less", "a little less", "less", "down", "lower", "smaller", "weaker", "lighter", "narrower", "quieter", "decrease",
        "reduce", "transparent", "meno", "piu piccoli", "piu piccolo", "diminuisci", "abbassa", "riduci", "rimpicciolisci", "trasparenti"]
ON = ["turn on", "switch on", "enable", "activate", "start", "show", "on", "accendi", "attiva", "avvia", "mostra", "metti"]
OFF = ["turn off", "switch off", "turn of", "disable", "deactivate", "stop", "mute", "hide", "off", "flat", "spegni", "disattiva",
       "ferma", "togli", "nascondi", "muto"]


def has(t, w): return re.search(r"(?:^|\s)" + re.escape(w) + r"(?:$|\s)", t) is not None


def number(t):
    """(value, percent, unit) from a clause: digits, English and Italian number words, half / quarter / max / min."""
    pct = bool(re.search(r"%|\bpercent\b|\bper cent\b|\bper cento\b|\bpercento\b", t))
    unit = "cm" if re.search(r"\b(cm|centimet(er|re|ri|ro)s?)\b", t) else "m" if re.search(r"\b(meters?|metres?|metri|metro)\b", t) else None
    if re.search(r"\b(max|maximum|full|massimo|completamente|completely)\b", t): return ("max", pct, unit)
    if re.search(r"\b(min|minimum|minimo)\b", t): return ("min", pct, unit)
    m = re.search(r"(\d+(?:\.\d+)?)", t)
    if m:
        v = float(m.group(1))
        if re.search(r"\b(and a half|e mezzo)\b", t): v += 0.5
        return (v, pct, unit)
    words = t.split()
    total, any_ = 0, False
    for w in words:
        if w in UNITS_EN: total += UNITS_EN.index(w); any_ = True
        elif w in TENS_EN: total += TENS_EN[w]; any_ = True
        elif w == "hundred": total = (total or 1) * 100; any_ = True
        elif w in WORDS_IT: total += WORDS_IT[w]; any_ = True
        elif re.fullmatch(r"(venti|trenta|quaranta|cinquanta|sessanta|settanta|ottanta|novanta)(uno|due|tre|quattro|cinque|sei|sette|otto|nove)", w):
            a, b = re.fullmatch(r"(venti|trenta|quaranta|cinquanta|sessanta|settanta|ottanta|novanta)(\w+)", w).groups(); total += WORDS_IT[a] + WORDS_IT.get(b, 0); any_ = True
    if any_:
        if re.search(r"\b(and a half|e mezzo)\b", t): total += 0.5
        return (float(total), pct, unit)
    if re.search(r"\b(half|mezzo|meta|mezza)\b", t): return ("half", pct, unit)
    if re.search(r"\b(quarter|quarto)\b", t): return ("quarter", pct, unit)
    return None


def setting_value(sid, raw):
    """A spoken value -> the setting's value: on / off, + / -, or a number in its units (percent -> fraction)."""
    s = SETTINGS[sid]
    t = fold(str(raw)) if raw not in ("+", "-") else raw
    if t in ("+", "-"): return t
    if s["kind"] == "bool":
        if any(has(t, w) for w in OFF) or t in ("0", "false", "no"): return "off"
        if any(has(t, w) for w in ON) or t in ("1", "true", "yes", "toggle"): return "on"
        return None
    if any(has(t, w) for w in MORE): return "+"
    if any(has(t, w) for w in LESS): return "-"
    n = number(t)
    if n is None: return None
    v, pct, unit = n
    lo, hi = s["min"], s["max"]
    if v == "max": return hi
    if v == "min": return lo
    if v == "half": return 0.5 if (unit == "m" or s["unit"] == "m") else (lo + hi) / 2
    if v == "quarter": return 0.25 if s["unit"] == "m" else lo + (hi - lo) / 4
    if unit == "cm" and s["unit"] == "m": v /= 100.0
    if s["unit"] == "%" and (pct or v > 1): v /= 100.0
    return max(lo, min(hi, v))


# ------------------------------------------------------------------------------------------ names: panels, settings (+ Italian)
PANEL_IT = {"terminali": "terminals", "terminale": "terminals", "file": "files", "cartelle": "files", "gestore dei file": "files",
            "controllo versione": "git", "repository": "git", "provider": "agents", "agenti": "agents", "conversazioni": "threads",
            "thread": "threads", "chat": "threads", "navigatore": "browser", "script": "scripts", "gesti": "gestures",
            "cassaforte": "vault", "casa forte": "vault", "segreti": "vault", "chiavi api": "vault", "impostazioni": "settings",
            "impostazione": "settings", "preferenze": "settings", "voce": "voice", "controllo vocale": "voice", "vocale": "voice",
            "sistema": "system", "focus ambientale": "focus", "focus": "focus", "spazio": "storage", "archiviazione": "storage",
            "disco": "storage", "strumenti": "tools", "strumenti installati": "tools"}
SETTING_IT = {"intensita": "focus_intensity", "buia": "focus_intensity", "raggio della cupola": "focus_radius", "raggio": "focus_radius",
              "cupola": "focus_radius", "morbidezza": "focus_softness", "sfocatura": "focus_blur", "opacita": "opacity",
              "trasparenti": "opacity", "distanza dei pannelli": "panel_distance", "distanza": "panel_distance",
              "finestre": "ui_scale", "testo": "ui_scale", "nitidezza": "sharpness", "curvatura": "bend", "suoni": "sounds",
              "click": "sounds", "volume": "volume", "risposte vocali": "speak", "raggi delle mani": "hand_rays", "raggi": "hand_rays",
              "occlusione": "occlusion", "rendering foveato": "foveation", "applauso": "clap", "raggio del carosello": "carousel_radius",
              "carosello": "carousel_radius", "stanza": "focus_intensity"}
SETTING_EN_EXTRA = {"room": "focus_intensity", "dark": "focus_intensity", "transparent": "opacity", "bending": "bend", "curved panels": "bend",
                    "bigger": "ui_scale", "text": "ui_scale", "windows": "ui_scale", "pointer rays": "hand_rays"}


def name_map(param):
    m = {}
    if param == "panel":
        for v in CMDS["open_panel"]["parameters"]["properties"]["panel"]["enum"]: m[fold(v)] = v
        for k, v in CMDS["open_panel"]["synonyms"].get("panel", {}).items(): m[fold(k)] = v
        for k, v in PANEL_IT.items(): m[fold(k)] = v
    else:
        for s in REG["settings"]:
            for n in s["names"]: m[fold(n)] = s["id"]
        for k, v in SETTING_EN_EXTRA.items(): m.setdefault(fold(k), v)
        for k, v in SETTING_IT.items(): m[fold(k)] = v
    return m


PANELS, SETTING_NAMES = name_map("panel"), name_map("setting")


def find_name(t, names):
    """The longest known name in the clause; else a word one or two letters off a known one-word name."""
    for k in sorted(names, key=len, reverse=True):
        if has(t, k): return names[k]
    for w in t.split():
        if len(w) < 4: continue
        best = None
        for k, v in names.items():
            if " " in k or len(k) < 4: continue
            d = lev(w, k)
            if d <= (1 if len(k) <= 6 else 2) and (best is None or d < best[0]): best = (d, v)
        if best: return best[1]
    return None


# ------------------------------------------------------------------------------------------ predictions -> canonical
def canon(cmd, args, clause=""):
    """Any pipeline's (command, args) -> what the app would run, values parsed the same way."""
    a = dict(args or {})
    if cmd == "set_setting":
        sid = a.get("setting")
        if sid not in SETTINGS: return (cmd, {"setting": sid, "value": None})
        val = setting_value(sid, a.get("value", ""))
        # the setting "focus" on / off runs SetFocus, exactly what set_focus runs: one action, one canonical form
        if sid == "focus" and val in ("on", "off"): return ("set_focus", {"state": val})
        return (cmd, {"setting": sid, "value": val})
    if cmd == "set_focus":
        st = fold(str(a.get("state", "")))
        return (cmd, {"state": "off" if st in ("off", "disable", "stop", "end") else "on"})
    if cmd in ("open_panel", "close_panel"): return (cmd, {"panel": a.get("panel")})
    if cmd == "set_layout": return (cmd, {"mode": a.get("mode")})
    if cmd == "find_thread": return (cmd, {"query": fold(str(a.get("query", "")))})
    if cmd == "switch_workspace": return (cmd, {"name": fold(str(a.get("name") or "")) or None})
    if cmd in ("type_text", "run_in_terminal"):
        k = "text" if cmd == "type_text" else "command"
        return (cmd, {k: fold(str(a.get(k, "")))})
    return (cmd, {})


def correct(pred, gold):
    """(exact, commands_only): the same commands in the same order (and the same values)."""
    if len(pred) != len(gold): return False, False
    names = all(p[0] == g[0] for p, g in zip(pred, gold))
    if not names: return False, False
    for (pc, pa), (gc, ga) in zip(pred, gold):
        if gc == "set_setting":
            if pa.get("setting") != ga["setting"]: return False, False
            pv, gv = pa.get("value"), ga["value"]
            if isinstance(gv, (int, float)) and not isinstance(gv, bool):
                if not isinstance(pv, (int, float)) or abs(pv - gv) > 0.011 + 0.02 * abs(gv): return False, True
            elif pv != gv: return False, True
        elif gc == "set_focus":
            if pa.get("state") != ga["state"]: return False, True
        elif gc in ("open_panel", "close_panel"):
            if pa.get("panel") != ga["panel"]: return False, False
        elif gc == "set_layout":
            if pa.get("mode") != ga["mode"]: return False, True
        elif gc == "find_thread":
            q = pa.get("query") or ""
            if not all(fold(k) in q for k in ga["keys"]): return False, True
        elif gc == "switch_workspace":
            if fold(ga.get("name") or "") != (pa.get("name") or ""): return False, True
        elif gc in ("type_text", "run_in_terminal"):
            k = "text" if gc == "type_text" else "command"
            if fold(ga[k]) != pa.get(k): return False, True
    return True, True


# ------------------------------------------------------------------------------------------ the LLM's lines
def parse_llm(content):
    out = []
    c = (content or "").strip()
    if not c or c.lower() == "none": return out
    for line in c.split(";"):
        parts = line.strip().split()
        if not parts: continue
        name = parts[0]
        if name not in CMDS: return out + [("?", {})]
        props = list(CMDS[name]["parameters"].get("properties", {}).keys())
        args, rest = {}, parts[1:]
        for i, p in enumerate(props):
            if not rest: break
            if i == len(props) - 1: args[p] = " ".join(rest); rest = []
            else: args[p] = rest[0]; rest = rest[1:]
        out.append(canon(name, args))
    return out


def fast_pred(row):
    f = FAST.get(row["id"])
    if not f or f.get("matches") is None: return None
    return [canon(m["command"], m["args"]) for m in f["matches"]]


# ------------------------------------------------------------------------------------------ the router's examples
FREE = {"query": ["the release notes", "the crash on startup", "fixing the build"], "name": ["omega", "website"],
        "text": ["good morning"], "command": ["ls -la", "npm test"], "value": ["on", "off", "40 percent", "three meters", "max", "more"]}
IT_EXAMPLES = {  # a few Italian sentences per command (none of them in the bench)
    "open_panel": ["apri il pannello", "mostrami la pagina", "fammi vedere la lista", "vai alla schermata", "apri la finestra delle impostazioni"],
    "close_panel": ["chiudi il pannello", "chiudi la finestra", "nascondi il pannello", "togli la pagina"],
    "close_all_panels": ["chiudi tutti i pannelli", "chiudi ogni finestra", "pulisci lo spazio", "togli tutte le finestre"],
    "new_terminal": ["apri un altro terminale", "crea una nuova shell", "nuovo terminale"],
    "find_thread": ["trova la conversazione in cui", "cerca il thread che parla di", "in quale chat abbiamo parlato di"],
    "switch_workspace": ["passa al progetto", "cambia progetto", "apri il workspace chiamato"],
    "set_layout": ["usa il layout fisso", "metti le finestre fluttuanti", "cambia la disposizione delle finestre"],
    "recenter": ["rimetti tutto davanti", "centra le finestre", "riporta i pannelli qui"],
    "type_text": ["scrivi", "digita il testo"], "run_in_terminal": ["esegui il comando", "lancia nel terminale"],
    "set_focus": ["accendi la modalità concentrazione", "spegni il focus", "oscura l'ambiente", "fammi rivedere la stanza"],
    "set_setting": ["imposta il volume a", "metti la luminosità al", "alza il", "abbassa la", "attiva i suoni", "rendi più grande"],
    "show_keyboard": ["fammi vedere la tastiera", "tira fuori la tastiera"],
}
NONE_EXTRA = ["what is this", "i like it", "okay thanks", "who are you", "come ti chiami", "va bene", "che bello", "non so"]


def expand(template, rnd, n=24):
    toks, cur, depth = [], "", 0
    for ch in template:
        if ch in "([{": depth += 1
        if ch in ")]}": depth -= 1
        if ch == " " and depth == 0:
            if cur: toks.append(cur); cur = ""
            continue
        cur += ch
    if cur: toks.append(cur)
    out = set()
    for _ in range(n):
        words = []
        for t in toks:
            if t.startswith("{"):
                inner = t.strip("{}")
                if ":" in inner: continue
                p = inner
                if p == "panel": words.append(rnd.choice(list(PANELS.keys())[:60]))
                elif p == "setting": words.append(rnd.choice([n for s in REG["settings"] for n in s["names"]]))
                elif p == "state": words.append(rnd.choice(["on", "off"]))
                elif p == "mode": words.append(rnd.choice(["float", "fixed"]))
                elif p == "color": words.append("blue")
                else: words.append(rnd.choice(FREE.get(p, ["something"])))
            elif t.startswith("("): words.append(rnd.choice(t[1:-1].split("|")))
            elif t.startswith("["):
                if rnd.random() < 0.5: words.append(rnd.choice(t[1:-1].split("|")))
            else: words.append(t)
        out.add(fold(" ".join(w for w in words if w)))
    return sorted(out)


def build_examples(with_it):
    rnd = random.Random(7)
    ex = []  # (text, command)
    for c in REG["commands"]:
        for ph in c["phrases"]:
            for e in expand(ph, rnd): ex.append((e, c["name"]))
        for said, line in c["examples"]: ex.append((fold(said), line.split()[0]))
        ex.append((fold(c["description"]), c["name"]))
        if with_it:
            for s in IT_EXAMPLES.get(c["name"], []): ex.append((fold(s), c["name"]))
    for said, line in REG["general_examples"]:
        name = line.split()[0]
        if name == "none": ex.append((fold(said), "none"))
        elif ";" not in line: ex.append((fold(said), name))
    for s in NONE_EXTRA: ex.append((fold(s), "none"))
    seen, out = set(), []
    for t, c in ex:
        if t and (t, c) not in seen: seen.add((t, c)); out.append((t, c))
    return out


# ------------------------------------------------------------------------------------------ encoders
class Encoder:
    def __init__(self, kind, name, prefix=""):
        self.kind, self.name, self.prefix = kind, name, prefix
        t0 = time.perf_counter()
        if kind == "fastembed":
            from fastembed import TextEmbedding
            if name == "intfloat/multilingual-e5-small" and not any(m["model"] == name for m in TextEmbedding.list_supported_models()):
                from fastembed.common.model_description import PoolingType, ModelSource
                TextEmbedding.add_custom_model(model=name, pooling=PoolingType.MEAN, normalization=True,
                                               sources=ModelSource(hf=name), dim=384, model_file="onnx/model.onnx")
            self.m = TextEmbedding(model_name=name, threads=2)
        else:
            from model2vec import StaticModel
            self.m = StaticModel.from_pretrained(name)
        self.load_s = time.perf_counter() - t0

    def embed(self, texts):
        texts = [self.prefix + t for t in texts]
        if self.kind == "fastembed": v = np.array(list(self.m.embed(texts, batch_size=64)))
        else: v = np.asarray(self.m.encode(texts))
        v = v / (np.linalg.norm(v, axis=1, keepdims=True) + 1e-9)
        return v.astype(np.float32)


VERBS = ["open", "close", "show", "hide", "turn", "set", "make", "switch", "use", "find", "search", "look", "start", "run", "type", "put",
         "bring", "dim", "darken", "increase", "decrease", "reduce", "raise", "lower", "mute", "enable", "disable", "recenter", "go", "give",
         "change", "let", "pin", "clear", "get", "list", "take", "dismiss", "volume", "radius", "intensity",
         "apri", "chiudi", "mostra", "mostrami", "nascondi", "accendi", "spegni", "imposta", "metti", "rendi", "passa", "usa", "trova",
         "cerca", "avvia", "esegui", "scrivi", "porta", "portami", "riporta", "oscura", "aumenta", "diminuisci", "alza", "abbassa", "attiva",
         "disattiva", "togli", "ricentra", "vai", "cambia", "fai", "blocca", "rimpicciolisci", "scurisci", "elenca", "dammi", "pulisci",
         "fammi", "intensita", "raggio"]


class Router:
    def __init__(self, enc, with_it):
        self.enc = enc
        self.ex = build_examples(with_it)
        self.labels = [c for _, c in self.ex]
        self.E = enc.embed([t for t, _ in self.ex])
        self.names = sorted(set(self.labels))
        self.idx = {n: [i for i, l in enumerate(self.labels) if l == n] for n in self.names}

    def scores(self, v):
        sims = self.E @ v
        return {n: float(sims[ix].max()) for n, ix in self.idx.items()}

    def decide(self, text, theta):
        cl = clauses(text)
        if not cl: return [], []
        V = self.enc.embed(cl)
        out, prev, trace = [], None, []
        for c, v in zip(cl, V):
            sc = self.scores(v)
            best = max(sc, key=sc.get)
            top = sc[best]
            verbless = not any(has(c, w) for w in VERBS)
            # an elliptic part ("the files", "e la morbidezza a mezzo metro"): the previous command with new values
            if prev and verbless and prev[0] in ("open_panel", "close_panel", "set_setting"):
                a = self.args(prev[0], c)
                if a and (prev[0] != "set_setting" or a.get("setting")):
                    out.append(canon(prev[0], a)); trace.append((c, prev[0], top)); continue
            if best == "none" or top < theta:
                trace.append((c, "none", top)); continue
            a = self.args(best, c)
            out.append(canon(best, a))
            prev = (best, a)
            trace.append((c, best, top))
        return out, trace

    def args(self, cmd, c):
        if cmd in ("open_panel", "close_panel"):
            p = find_name(c, PANELS)
            return {"panel": p} if p else {}
        if cmd == "set_setting":
            sid = find_name(c, SETTING_NAMES)
            if sid is None: return {}
            if SETTINGS[sid]["kind"] == "bool":
                v = "off" if any(has(c, w) for w in OFF) else "on"
                return {"setting": sid, "value": v}
            return {"setting": sid, "value": c}
        if cmd == "set_focus":
            return {"state": "off" if any(has(c, w) for w in OFF + ["again", "di nuovo", "rivedere"]) else "on"}
        if cmd == "set_layout":
            return {"mode": "fixed" if re.search(r"\b(fixed|pin|fisso|blocca)\b", c) else "float"}
        if cmd == "find_thread": return {"query": c}
        if cmd == "switch_workspace":
            m = re.search(r"\b(?:called|named|workspace|project|progetto|al)\s+(?:the\s+|del\s+progetto\s+)?([a-z0-9]+)", c)
            n = m.group(1) if m else None
            if n in ("workspace", "project", "progetto", "please", "per", None): n = None
            m2 = re.search(r"\b(?:to the|al|the)\s+([a-z0-9]+)\s+(?:project|workspace)\b", c)
            if not n and m2: n = m2.group(1)
            return {"name": n} if n else {}
        if cmd == "type_text":
            return {"text": re.sub(r"^(type|scrivi|digita)\s+", "", c)}
        if cmd == "run_in_terminal":
            return {"command": re.sub(r"^(run|execute|esegui|lancia)\s+", "", c)}
        return {}


# ------------------------------------------------------------------------------------------ run
def evaluate(name, predict):
    res, t_all = [], []
    for row in EVAL:
        t0 = time.perf_counter()
        pred = predict(row)
        t_all.append((time.perf_counter() - t0) * 1000)
        gold = [tuple(g) for g in row["gold"]]
        ex, cm = correct(pred, gold)
        res.append({"id": row["id"], "lang": row["lang"], "kind": row["kind"], "text": row["text"], "pipeline": name,
                    "pred": pred, "exact": ex, "cmd": cm})
    return res, t_all


def table(all_res, lat):
    keys = [("all", None, None)] + [(f"{l}/{k}", l, k) for l in ("en", "it") for k in ("single", "multi", "noisy", "none")] + [("en", "en", None), ("it", "it", None)]
    pipes = list(dict.fromkeys(r["pipeline"] for r in all_res))
    lines = ["| pipeline | " + " | ".join(k for k, _, _ in keys) + " | ms / sentence (PC) |", "|---" * (len(keys) + 2) + "|"]
    for p in pipes:
        rs = [r for r in all_res if r["pipeline"] == p]
        cells = []
        for k, l, kind in keys:
            sub = [r for r in rs if (l is None or r["lang"] == l) and (kind is None or r["kind"] == kind)]
            if not sub: cells.append("-"); continue
            cells.append(f"{100 * sum(r['exact'] for r in sub) / len(sub):.0f}")
        lines.append(f"| {p} | " + " | ".join(cells) + f" | {lat.get(p, '-')} |")
    return "\n".join(lines)


def main():
    out_res, lat = [], {}
    # today: fast path -> LLM, and the LLM alone
    if LLM:
        r, _ = evaluate("today: fast path -> LLM (Qwen3 0.6B)", lambda row: fast_pred(row) if fast_pred(row) is not None else parse_llm(LLM[row["id"]]["content"]))
        out_res += r
        ms = [LLM[x["id"]]["ms"] for x in EVAL if fast_pred(x) is None]
        lat[r[0]["pipeline"]] = f"LLM {np.median(ms):.0f} (median, on the {len(ms)} the fast path misses)"
        r, _ = evaluate("LLM alone", lambda row: parse_llm(LLM[row["id"]]["content"]))
        out_res += r
        lat["LLM alone"] = f"{np.median([LLM[x['id']]['ms'] for x in EVAL]):.0f}"
    r, _ = evaluate("fast path alone", lambda row: fast_pred(row) or [])
    out_res += r
    encs = [("fastembed", "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2", "", "multilingual MiniLM-L12 (118M)"),
            ("fastembed", "intfloat/multilingual-e5-small", "query: ", "multilingual-e5-small (118M)"),
            ("fastembed", "google/embeddinggemma-300m", "", "EmbeddingGemma (300M)"),
            ("fastembed", "BAAI/bge-small-en-v1.5", "", "bge-small-en (33M, English)"),
            ("model2vec", "minishlab/potion-multilingual-128M", "", "potion-multilingual (static)"),
            ("model2vec", "minishlab/potion-base-8M", "", "potion-base-8M (static, English)")]
    only = set(sys.argv[1:])
    for kind, name, prefix, label in encs:
        if only and not any(o in name for o in only): continue
        try:
            enc = Encoder(kind, name, prefix)
        except Exception as e:
            print("skip", name, e); continue
        for with_it in (False, True):
            router = Router(enc, with_it)
            even = [row for i, row in enumerate(EVAL) if i % 2 == 0]
            best = None
            for theta in np.arange(0.30, 0.92, 0.02):  # the threshold chosen on half the sentences only
                ok = 0
                for row in even:
                    pred, _ = router.decide(row["text"], theta)
                    ok += correct(pred, [tuple(g) for g in row["gold"]])[0]
                if best is None or ok > best[1]: best = (float(theta), ok)
            theta = best[0]
            tag = f"{label}{' + IT examples' if with_it else ''}"
            r, t1 = evaluate(f"fast path -> {tag}", lambda row: fast_pred(row) if fast_pred(row) is not None else router.decide(row["text"], theta)[0])
            out_res += r
            r2, t2 = evaluate(f"{tag} alone", lambda row: router.decide(row["text"], theta)[0])
            out_res += r2
            lat[f"fast path -> {tag}"] = f"{np.median(t2):.1f} (router, θ {theta:.2f})"
            lat[f"{tag} alone"] = f"{np.median(t2):.1f}"
            print(f"{tag}: θ {theta:.2f}, {len(router.ex)} examples, load {enc.load_s:.1f} s", flush=True)
    t = table(out_res, lat)
    print(t)
    with open(os.path.join(WORK, "results.jsonl"), "w", encoding="utf-8") as f:
        for r in out_res: f.write(json.dumps(r, ensure_ascii=False, default=str) + "\n")
    with open(os.path.join(WORK, "report.md"), "w", encoding="utf-8") as f: f.write(t + "\n")


if __name__ == "__main__":
    main()
