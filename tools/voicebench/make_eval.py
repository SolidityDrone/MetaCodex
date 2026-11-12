"""Voice-command bench: the sentences and what they must do (tools/voicebench/eval.jsonl).

English only (since 2026-11-12: the 175 Italian rows were dropped; the English ids stay). Single commands, several
commands in one sentence (order matters), misheard words (the real ones from the headset's session log and typical
speech-to-text slips) and sentences that are no command at all.
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
    "web": ["open the web browser", "I need a browser", "bring up the browser"],
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
             ("dismiss the browser", "web"), ("put away the scripts panel", "scripts")]:
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
    ("open the browser, the files and git", [P("web"), P("files"), P("git")]),
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
