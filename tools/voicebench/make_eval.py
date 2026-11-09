"""Voice-command bench: the sentences and what they must do (tools/voicebench/eval.jsonl).

English and Italian; single commands, several commands in one sentence (order matters), misheard words (the real
ones from the headset's session log and typical speech-to-text slips) and sentences that are no command at all.
Gold = the ordered list of [command, args] the app must run. Values use the app's units: fractions for percent
settings (100 % -> 1.0), metres, "on" / "off", "+" / "-" for more / less. Free text (thread queries, workspace
names) is judged on its key words (`keys`).

Usage: python3 tools/voicebench/make_eval.py  -> tools/voicebench/eval.jsonl
"""
import json, os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "eval.jsonl")
rows = []


def add(lang, kind, text, *gold):
    rows.append({"id": f"{lang}-{len(rows):03d}", "lang": lang, "kind": kind, "text": text, "gold": [list(g) for g in gold]})


def P(panel): return ("open_panel", {"panel": panel})
def C(panel): return ("close_panel", {"panel": panel})
def S(setting, value): return ("set_setting", {"setting": setting, "value": value})
def F(state): return ("set_focus", {"state": state})
def T(*keys): return ("find_thread", {"keys": list(keys)})
def W(name=None): return ("switch_workspace", {"name": name} if name else {})
def L(mode): return ("set_layout", {"mode": mode})
CLOSE_ALL = ("close_all_panels", {})
NEW_TERM = ("new_terminal", {})
RECENTER = ("recenter", {})
KEYBOARD = ("show_keyboard", {})
def TYPE(text): return ("type_text", {"text": text})
def RUN(cmd): return ("run_in_terminal", {"command": cmd})

# ---------------------------------------------------------------- English, single commands
en_panels = {
    "terminals": ["show me my terminals", "list the open terminals", "open the terminals page"],
    "files": ["open the file browser", "show my files", "I want to see my folders"],
    "git": ["open git", "show me the source control", "open the repository panel"],
    "agents": ["open the providers page", "which AI providers are signed in", "show me the agents"],
    "threads": ["show my conversations", "open the threads list", "where are my chats"],
    "browser": ["open the web browser", "I need a browser", "bring up the browser"],
    "scripts": ["open the scripts", "show my lua scripts", "go to the scripts panel"],
    "gestures": ["open the gestures studio", "show the hand gestures", "take me to gestures"],
    "vault": ["open the vault", "where are my api keys", "show my secrets"],
    "settings": ["open settings", "I want to change the settings", "open the preferences"],
    "voice": ["open the voice panel", "show voice control", "open the speech settings"],
    "system": ["open the system panel", "show system info", "go to system"],
    "focus": ["open the ambient focus settings", "show the focus options", "open ambient focus"],
    "storage": ["how much storage do I have left", "show the disk usage", "open storage"],
    "tools": ["show the installed tools", "which tools are installed", "open the tools list"],
}
for panel, says in en_panels.items():
    for s in says: add("en", "single", s, P(panel))
for s, p in [("close the vault", "vault"), ("hide the files", "files"), ("close git", "git"), ("close the settings", "settings"),
             ("dismiss the browser", "browser"), ("put away the scripts panel", "scripts")]:
    add("en", "single", s, C(p))
for s in ["close all windows", "close everything", "clear my view", "get rid of all the panels"]: add("en", "single", s, CLOSE_ALL)
for s in ["open a new terminal", "I need another shell", "give me a new console", "start a terminal"]: add("en", "single", s, NEW_TERM)
add("en", "single", "find the thread where we fixed the keyboard", T("keyboard"))
add("en", "single", "which conversation was about the login page", T("login"))
add("en", "single", "search my chats for the database migration", T("database", "migration"))
add("en", "single", "look for the thread about voice commands", T("voice", "commands"))
add("en", "single", "switch to the workspace called metacodex", W("metacodex"))
add("en", "single", "go to the sandybridge project", W("sandybridge"))
add("en", "single", "change workspace", W())
add("en", "single", "open the alpha project workspace", W("alpha"))
add("en", "single", "use the floating layout", L("float"))
add("en", "single", "let the windows float around me", L("float"))
add("en", "single", "fixed layout please", L("fixed"))
add("en", "single", "pin the panels in place", L("fixed"))
for s in ["recenter", "bring everything back in front of me", "put the windows in front of me", "reset the view position"]: add("en", "single", s, RECENTER)
for s in ["show the keyboard", "bring up the keyboard", "I need the keyboard"]: add("en", "single", s, KEYBOARD)
add("en", "single", "type hello world", TYPE("hello world"))
add("en", "single", "run git status", RUN("git status"))
for s, st in [("turn on ambient focus", "on"), ("focus mode on", "on"), ("darken the room", "on"), ("turn off the focus", "off"),
              ("show the room again", "off"), ("disable ambient focus", "off")]:
    add("en", "single", s, F(st))
en_settings = [
    ("set the intensity to 50 percent", "focus_intensity", 0.5), ("make the room completely dark", "focus_intensity", 1.0),
    ("dim the room a bit more", "focus_intensity", "+"), ("set the dome radius to two meters", "focus_radius", 2.0),
    ("radius one and a half meters", "focus_radius", 1.5), ("set the softness to half a meter", "focus_softness", 0.5),
    ("set the blur to fifty percent", "focus_blur", 0.5), ("set the opacity to max", "opacity", 1.0),
    ("make the windows more transparent", "opacity", "-"), ("set the panel distance to one meter", "panel_distance", 1.0),
    ("make the windows bigger", "ui_scale", "+"), ("make the text smaller", "ui_scale", "-"), ("increase the sharpness", "sharpness", "+"),
    ("turn on panel bending", "bend", "on"), ("turn off the curved panels", "bend", "off"), ("turn off the sounds", "sounds", "off"),
    ("mute the clicks", "sounds", "off"), ("set the volume to a quarter", "volume", 0.25), ("volume up", "volume", "+"),
    ("turn the volume down a bit", "volume", "-"), ("turn on spoken answers", "speak", "on"), ("hide the hand rays", "hand_rays", "off"),
    ("show the pointer rays", "hand_rays", "on"), ("turn off occlusion", "occlusion", "off"), ("enable foveated rendering", "foveation", "on"),
    ("disable the clap gesture", "clap", "off"), ("set the carousel radius to one meter", "carousel_radius", 1.0),
    ("set the volume to eighty percent", "volume", 0.8), ("intensity 30", "focus_intensity", 0.3),
]
for s, k, v in en_settings: add("en", "single", s, S(k, v))

# ---------------------------------------------------------------- English, several commands (order matters)
en_multi = [
    ("turn on ambient focus and set intensity to 100%", [F("on"), S("focus_intensity", 1.0)]),
    ("turn on ambient focus and set the intensity to zero", [F("on"), S("focus_intensity", 0.0)]),
    ("set the intensity to 50 percent and turn on ambient focus", [S("focus_intensity", 0.5), F("on")]),
    ("close all windows and open the vault", [CLOSE_ALL, P("vault")]),
    ("open git and then open the files", [P("git"), P("files")]),
    ("open a new terminal and show the keyboard", [NEW_TERM, KEYBOARD]),
    ("turn off the sounds and set the volume to 30 percent", [S("sounds", "off"), S("volume", 0.3)]),
    ("recenter and use the floating layout", [RECENTER, L("float")]),
    ("set the radius to two meters and the softness to half a meter", [S("focus_radius", 2.0), S("focus_softness", 0.5)]),
    ("darken the room, then open settings", [F("on"), P("settings")]),
    ("open the browser, the files and git", [P("browser"), P("files"), P("git")]),
    ("turn off ambient focus and close everything", [F("off"), CLOSE_ALL]),
    ("make the windows bigger and increase the opacity", [S("ui_scale", "+"), S("opacity", "+")]),
    ("switch to the metacodex workspace and open the threads", [W("metacodex"), P("threads")]),
    ("show the installed tools and the storage", [P("tools"), P("storage")]),
    ("turn on spoken answers and set the volume to max", [S("speak", "on"), S("volume", 1.0)]),
    ("close all windows, turn on ambient focus and set the intensity to 80 percent", [CLOSE_ALL, F("on"), S("focus_intensity", 0.8)]),
    ("find the thread about the keyboard and open the files", [T("keyboard"), P("files")]),
    ("hide the vault and show the agents", [C("vault"), P("agents")]),
    ("open settings, then recenter", [P("settings"), RECENTER]),
    ("turn off the hand rays and turn on the sounds", [S("hand_rays", "off"), S("sounds", "on")]),
    ("use the fixed layout and close all windows", [L("fixed"), CLOSE_ALL]),
    ("open the voice panel and the system panel", [P("voice"), P("system")]),
    ("set the blur to 70 percent and the intensity to 40 percent", [S("focus_blur", 0.7), S("focus_intensity", 0.4)]),
    ("open a new terminal, then close the vault, then recenter", [NEW_TERM, C("vault"), RECENTER]),
]
for s, g in en_multi: add("en", "multi", s, *g)

# ---------------------------------------------------------------- English, misheard (real ones from the headset log first)
en_noisy = [
    ("Ternon ambent forgoose.", [F("on")]), ("Turn off ambient fogus.", [F("off")]), ("Turn off from the infoguse.", [F("off")]),
    ("Reduce ambient fogus intensity.", [S("focus_intensity", "-")]), ("Closer panels.", [CLOSE_ALL]), ("Closel windows", [CLOSE_ALL]),
    ("Open panels for threads and agents.", [P("threads"), P("agents")]), ("Obrans scripts.", [P("scripts")]),
    ("Open the provider's page.", [P("agents")]), ("Open ambient fogus.", [P("focus")]),
    ("turn on ambient fogus and set intensity to zero", [F("on"), S("focus_intensity", 0.0)]),
    ("opn the vault", [P("vault")]), ("clothes all windows", [CLOSE_ALL]), ("turn of the sounds", [S("sounds", "off")]),
    ("set the volum to fifty percent", [S("volume", 0.5)]), ("open the setings", [P("settings")]), ("new terminel", [NEW_TERM]),
    ("re center", [RECENTER]), ("show the key board", [KEYBOARD]), ("open the fails", [P("files")]),
    ("open the voice pannel", [P("voice")]), ("make the windows biger", [S("ui_scale", "+")]),
    ("find the thred about the keyboard", [T("keyboard")]), ("use the floting layout", [L("float")]),
    ("turn off ambient focus and open the vault", [F("off"), P("vault")]),
]
for s, g in en_noisy: add("en", "noisy", s, *g)

# ---------------------------------------------------------------- English, not a command
for s in ["Thank you.", "Ugh.", "What can you do for me?", "What is the name of this program?", "hello there",
          "what's the weather like", "good morning", "tell me a joke", "I'm tired", "who made you", "how are you",
          "what time is it", "that's great", "never mind", "hmm let me think"]:
    add("en", "none", s)

# ---------------------------------------------------------------- Italiano, comandi singoli
it_panels = {
    "terminals": ["mostrami i terminali", "apri i terminali", "elenca i terminali aperti"],
    "files": ["apri i file", "mostrami le cartelle", "apri il gestore dei file"],
    "git": ["apri git", "mostra il controllo versione", "apri il pannello del repository"],
    "agents": ["apri la pagina dei provider", "quali provider AI sono collegati", "mostrami gli agenti"],
    "threads": ["mostra le mie conversazioni", "apri la lista dei thread", "dove sono le mie chat"],
    "browser": ["apri il browser", "mi serve il browser web", "fammi vedere il browser"],
    "scripts": ["apri gli script", "mostra i miei script lua", "vai al pannello degli script"],
    "gestures": ["apri lo studio dei gesti", "mostra i gesti delle mani", "portami ai gesti"],
    "vault": ["apri la cassaforte", "dove sono le mie chiavi api", "mostra i miei segreti"],
    "settings": ["apri le impostazioni", "voglio cambiare le impostazioni", "apri le preferenze"],
    "voice": ["apri il pannello voce", "mostra il controllo vocale", "apri le impostazioni della voce"],
    "system": ["apri il pannello di sistema", "mostra le informazioni di sistema", "vai al sistema"],
    "focus": ["apri le impostazioni del focus ambientale", "mostra le opzioni del focus", "apri il focus ambientale"],
    "storage": ["quanto spazio mi rimane", "mostra lo spazio su disco", "apri l'archiviazione"],
    "tools": ["mostra gli strumenti installati", "quali strumenti sono installati", "apri la lista degli strumenti"],
}
for panel, says in it_panels.items():
    for s in says: add("it", "single", s, P(panel))
for s, p in [("chiudi la cassaforte", "vault"), ("nascondi i file", "files"), ("chiudi git", "git"), ("chiudi le impostazioni", "settings"),
             ("chiudi il browser", "browser"), ("togli il pannello degli script", "scripts")]:
    add("it", "single", s, C(p))
for s in ["chiudi tutte le finestre", "chiudi tutto", "pulisci la vista", "togli tutti i pannelli"]: add("it", "single", s, CLOSE_ALL)
for s in ["apri un nuovo terminale", "mi serve un'altra shell", "dammi una nuova console", "avvia un terminale"]: add("it", "single", s, NEW_TERM)
add("it", "single", "trova il thread dove abbiamo sistemato la tastiera", T("tastiera"))
add("it", "single", "quale conversazione parlava della pagina di login", T("login"))
add("it", "single", "cerca nelle chat la migrazione del database", T("migrazione", "database"))
add("it", "single", "cerca la conversazione sui comandi vocali", T("comandi", "vocali"))
add("it", "single", "passa al workspace metacodex", W("metacodex"))
add("it", "single", "vai al progetto sandybridge", W("sandybridge"))
add("it", "single", "cambia workspace", W())
add("it", "single", "apri il workspace del progetto alpha", W("alpha"))
add("it", "single", "usa il layout fluttuante", L("float"))
add("it", "single", "fai fluttuare le finestre intorno a me", L("float"))
add("it", "single", "layout fisso per favore", L("fixed"))
add("it", "single", "blocca i pannelli al loro posto", L("fixed"))
for s in ["ricentra", "riporta tutto davanti a me", "metti le finestre davanti a me", "riposiziona la vista"]: add("it", "single", s, RECENTER)
for s in ["mostra la tastiera", "apri la tastiera", "mi serve la tastiera"]: add("it", "single", s, KEYBOARD)
add("it", "single", "scrivi ciao mondo", TYPE("ciao mondo"))
add("it", "single", "esegui git status", RUN("git status"))
for s, st in [("accendi il focus ambientale", "on"), ("attiva la modalità focus", "on"), ("oscura la stanza", "on"), ("spegni il focus", "off"),
              ("mostrami di nuovo la stanza", "off"), ("disattiva il focus ambientale", "off")]:
    add("it", "single", s, F(st))
it_settings = [
    ("imposta l'intensità al 50 per cento", "focus_intensity", 0.5), ("rendi la stanza completamente buia", "focus_intensity", 1.0),
    ("scurisci un po' di più la stanza", "focus_intensity", "+"), ("imposta il raggio della cupola a due metri", "focus_radius", 2.0),
    ("raggio un metro e mezzo", "focus_radius", 1.5), ("imposta la morbidezza a mezzo metro", "focus_softness", 0.5),
    ("metti la sfocatura al cinquanta per cento", "focus_blur", 0.5), ("imposta l'opacità al massimo", "opacity", 1.0),
    ("rendi le finestre più trasparenti", "opacity", "-"), ("imposta la distanza dei pannelli a un metro", "panel_distance", 1.0),
    ("rendi le finestre più grandi", "ui_scale", "+"), ("rimpicciolisci il testo", "ui_scale", "-"), ("aumenta la nitidezza", "sharpness", "+"),
    ("attiva la curvatura dei pannelli", "bend", "on"), ("spegni la curvatura dei pannelli", "bend", "off"), ("disattiva i suoni", "sounds", "off"),
    ("togli i click", "sounds", "off"), ("metti il volume a un quarto", "volume", 0.25), ("alza il volume", "volume", "+"),
    ("abbassa un po' il volume", "volume", "-"), ("attiva le risposte vocali", "speak", "on"), ("nascondi i raggi delle mani", "hand_rays", "off"),
    ("mostra i raggi", "hand_rays", "on"), ("disattiva l'occlusione", "occlusion", "off"), ("attiva il rendering foveato", "foveation", "on"),
    ("disattiva il gesto dell'applauso", "clap", "off"), ("imposta il raggio del carosello a un metro", "carousel_radius", 1.0),
    ("metti il volume all'ottanta per cento", "volume", 0.8), ("intensità 30", "focus_intensity", 0.3),
]
for s, k, v in it_settings: add("it", "single", s, S(k, v))

# ---------------------------------------------------------------- Italiano, più comandi (l'ordine conta)
it_multi = [
    ("accendi il focus ambientale e metti l'intensità al 100%", [F("on"), S("focus_intensity", 1.0)]),
    ("accendi il focus ambientale e imposta l'intensità a zero", [F("on"), S("focus_intensity", 0.0)]),
    ("imposta l'intensità al 50 per cento e accendi il focus ambientale", [S("focus_intensity", 0.5), F("on")]),
    ("chiudi tutte le finestre e apri la cassaforte", [CLOSE_ALL, P("vault")]),
    ("apri git e poi apri i file", [P("git"), P("files")]),
    ("apri un nuovo terminale e mostra la tastiera", [NEW_TERM, KEYBOARD]),
    ("disattiva i suoni e metti il volume al 30 per cento", [S("sounds", "off"), S("volume", 0.3)]),
    ("ricentra e usa il layout fluttuante", [RECENTER, L("float")]),
    ("imposta il raggio a due metri e la morbidezza a mezzo metro", [S("focus_radius", 2.0), S("focus_softness", 0.5)]),
    ("oscura la stanza, poi apri le impostazioni", [F("on"), P("settings")]),
    ("apri il browser, i file e git", [P("browser"), P("files"), P("git")]),
    ("spegni il focus ambientale e chiudi tutto", [F("off"), CLOSE_ALL]),
    ("rendi le finestre più grandi e aumenta l'opacità", [S("ui_scale", "+"), S("opacity", "+")]),
    ("passa al workspace metacodex e apri i thread", [W("metacodex"), P("threads")]),
    ("mostra gli strumenti installati e l'archiviazione", [P("tools"), P("storage")]),
    ("attiva le risposte vocali e metti il volume al massimo", [S("speak", "on"), S("volume", 1.0)]),
    ("chiudi tutte le finestre, accendi il focus ambientale e imposta l'intensità all'80 per cento", [CLOSE_ALL, F("on"), S("focus_intensity", 0.8)]),
    ("trova il thread sulla tastiera e apri i file", [T("tastiera"), P("files")]),
    ("nascondi la cassaforte e mostra gli agenti", [C("vault"), P("agents")]),
    ("apri le impostazioni, poi ricentra", [P("settings"), RECENTER]),
    ("spegni i raggi delle mani e accendi i suoni", [S("hand_rays", "off"), S("sounds", "on")]),
    ("usa il layout fisso e chiudi tutte le finestre", [L("fixed"), CLOSE_ALL]),
    ("apri il pannello voce e il pannello di sistema", [P("voice"), P("system")]),
    ("metti la sfocatura al 70 per cento e l'intensità al 40 per cento", [S("focus_blur", 0.7), S("focus_intensity", 0.4)]),
    ("apri un nuovo terminale, poi chiudi la cassaforte, poi ricentra", [NEW_TERM, C("vault"), RECENTER]),
]
for s, g in it_multi: add("it", "multi", s, *g)

# ---------------------------------------------------------------- Italiano, parole sentite male
it_noisy = [
    ("accendi il fokus ambientale", [F("on")]), ("spegni il focus ambientali", [F("off")]), ("apri le impostazione", [P("settings")]),
    ("apri la casa forte", [P("vault")]), ("metti il volume a cinquanta percento", [S("volume", 0.5)]), ("chiudi tutte le finestra", [CLOSE_ALL]),
    ("apri un nuovo terminali", [NEW_TERM]), ("ri centra", [RECENTER]), ("mostra la tastira", [KEYBOARD]), ("apri i fail", [P("files")]),
    ("apri il pannello vocce", [P("voice")]), ("rendi le finestre piu grandi", [S("ui_scale", "+")]),
    ("trova il tred sulla tastiera", [T("tastiera")]), ("usa il layout flutuante", [L("float")]),
    ("accendi il focus e metti l'intensita al massimo", [F("on"), S("focus_intensity", 1.0)]),
    ("spegni il focus e apri la cassaforte", [F("off"), P("vault")]), ("apri gli scrip", [P("scripts")]),
    ("disattiva i suonni", [S("sounds", "off")]), ("chiudi il brauser", [C("browser")]), ("apri il sistemma", [P("system")]),
]
for s, g in it_noisy: add("it", "noisy", s, *g)

# ---------------------------------------------------------------- Italiano, nessun comando
for s in ["grazie", "ciao", "che tempo fa oggi", "buongiorno", "raccontami una barzelletta", "sono stanco", "chi ti ha creato",
          "come stai", "che ore sono", "perfetto", "lascia stare", "mmm fammi pensare", "cosa sai fare", "uffa", "va bene così"]:
    add("it", "none", s)

# settings the app no longer has (0.7.3: the render scale is fixed, no "sharpness"): asking for them is no command.
# Kept as rows, so every other sentence keeps its id (the LLM answers in llm.jsonl are cached by id).
REMOVED_SETTINGS = {"sharpness"}
for r in rows:
    if any(g[0] == "set_setting" and g[1].get("setting") in REMOVED_SETTINGS for g in r["gold"]): r["gold"] = []

with open(OUT, "w", encoding="utf-8") as f:
    for r in rows: f.write(json.dumps(r, ensure_ascii=False) + "\n")
by = {}
for r in rows: by[(r["lang"], r["kind"])] = by.get((r["lang"], r["kind"]), 0) + 1
print(len(rows), "sentences ->", OUT)
for k in sorted(by): print(" ", k, by[k])
