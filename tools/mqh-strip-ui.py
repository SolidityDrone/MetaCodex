"""Part of tools/mqh-gut.sh: remove the web UI from every MQH plugin (MQH has no web client; Unity renders
plugin data from the API and the plugin's spatial.json).

Usage: python3 -I tools/mqh-strip-ui.py <mqh dir> [--apply]        (default: dry run, prints what would go)

Per plugin: keeps the files its server/host/cli entries, its package exports and its non-UI tests reach
through relative imports, plus its skills; deletes the rest (app.tsx, React components, styles, UI tests).
A file is UI when it is .tsx/.css or imports React, the shared web components, the SDK app runtime or an
editor/UI library, directly or through another file. Then it drops the "app" manifest entry, exports that
pointed at deleted files, and UI packages no kept file imports. Fails if a server/host entry reaches UI code.
"""
import json, os, re, sys

root = sys.argv[1]
apply = "--apply" in sys.argv
SRC_EXT = (".ts", ".tsx", ".mts", ".mjs", ".js", ".css", ".cts")
IMP = re.compile(r'''(?:from\s+|import\s*\(\s*|import\s+|require\s*\(\s*)["'](\.{1,2}/[^"']+)["']''')
PKG = re.compile(r'''(?:from\s+|import\s*\(\s*|import\s+|require\s*\(\s*)["']((?:@[\w.-]+/)?[\w.-]+)[^"']*["']''')
UI_IMPORT = re.compile(r'''["'](react|react/[^"']*|react-dom[^"']*|@testing-library/[^"']*|@bb/shared-ui[^"']*|'''
                       r'''@bb/plugin-registry[^"']*|@get-bb/plugin-sdk/(?:app|testing/app)|@tiptap/[^"']*|'''
                       r'''@tanstack/react-[^"']*|lucide-react|@hugeicons/[^"']*|sonner|usehooks-ts|tippy\.js[^"']*)["']''')
UI_PACKAGES = re.compile(r"^(react|react-dom|@types/react(-dom)?|@testing-library/.*|@bb/shared-ui|@bb/plugin-registry|"
                         r"@tiptap/.*|@tanstack/react-.*|lucide-react|@hugeicons/.*|sonner|usehooks-ts|tippy\.js|jsdom|"
                         r"class-variance-authority|clsx|tailwind-merge|tailwindcss|@tailwindcss/.*|motion|framer-motion|"
                         r"cmdk|vaul|@radix-ui/.*|@base-ui-components/.*|@dnd-kit/.*|react-.*|@storybook/.*|prosemirror-.*)$")


def resolve(base, spec):
    p = os.path.normpath(os.path.join(os.path.dirname(base), spec))
    stem = re.sub(r"\.(m?js|cjs)$", "", p)
    for c in [p] + [x for e in (".ts", ".tsx", ".mts", ".cts", ".js", ".mjs") for x in (stem + e, os.path.join(p, "index" + e))]:
        if os.path.isfile(c):
            return c
    return None


def export_targets(ex):
    if isinstance(ex, str):
        yield ex
    elif isinstance(ex, dict):
        for v in ex.values():
            yield from export_targets(v)


total = 0
for d in sorted(os.listdir(os.path.join(root, "plugins"))):
    pd = os.path.join(root, "plugins", d)
    pj = os.path.join(pd, "package.json")
    if not os.path.isfile(pj):
        continue
    pkg = json.load(open(pj, encoding="utf-8"))
    man = pkg.get("bb", {})
    entries = [os.path.normpath(os.path.join(pd, man[k])) for k in ("server", "host", "cli") if isinstance(man.get(k), str)]
    exported = [os.path.normpath(os.path.join(pd, t)) for t in export_targets(pkg.get("exports", {}))]
    skill_dirs = [os.path.normpath(os.path.join(pd, s)) for s in (man.get("skills") or [])] + [os.path.join(pd, "skills")]
    files = []
    for dp, dn, fn in os.walk(pd):
        dn[:] = [x for x in dn if x not in ("node_modules", "dist", ".turbo")]
        files += [os.path.normpath(os.path.join(dp, f)) for f in fn if f.endswith(SRC_EXT)]
    deps, text = {}, {}
    for f in files:
        text[f] = open(f, encoding="utf-8", errors="replace").read()
        deps[f] = [r for r in (resolve(f, m.group(1)) for m in IMP.finditer(text[f])) if r]
    tainted = {f for f in files if f.endswith((".tsx", ".css")) or UI_IMPORT.search(text[f])}
    changed = True
    while changed:
        changed = False
        for f, ds in deps.items():
            if f not in tainted and any(x in tainted for x in ds):
                tainted.add(f)
                changed = True
    bad = [os.path.relpath(f, pd) for f in entries if f in tainted]
    if bad and apply:
        sys.exit(f"{d}: server/host entry reaches UI code: {bad} (strip it by hand or drop the plugin)")
    tests = [f for f in files if re.search(r"(\.test\.|__tests__|[/\\]test[/\\]|\.spec\.)", f) and f not in tainted]
    roots = entries + [f for f in exported if f not in tainted] + tests
    seen, stack = set(), list(roots)
    while stack:
        f = stack.pop()
        if f in seen or not os.path.isfile(f):
            continue
        seen.add(f)
        stack += deps.get(f, [])
    drop = [f for f in files if f not in seen and not os.path.relpath(f, pd).startswith(("vitest.config", "tsconfig"))
            and not any(f.startswith(s + os.sep) for s in skill_dirs)]
    kept = [f for f in files if f not in drop]
    used = {m.group(1) for f in kept for m in PKG.finditer(text[f])}
    # vitest setup files and configs that pull in the DOM test stack go with the UI
    removed_pkgs = []
    for sec in ("dependencies", "devDependencies", "peerDependencies"):
        for k in list(pkg.get(sec, {})):
            if UI_PACKAGES.match(k) and k not in used:
                removed_pkgs.append(k)
                if apply:
                    del pkg[sec][k]
    dangling = [k for k, v in (pkg.get("exports") or {}).items()
                if any(os.path.normpath(os.path.join(pd, t)) in drop for t in export_targets(v))]
    total += len(drop)
    print(f"{d}: keep {len(kept)}, drop {len(drop)} files; drop exports {dangling or '-'}; "
          f"drop packages {', '.join(sorted(set(removed_pkgs))) or '-'}" + (f"; !! entry reaches UI: {bad}" if bad else ""))
    if apply:
        for f in drop:
            os.remove(f)
        for k in dangling:
            del pkg["exports"][k]
        if pkg.get("exports") == {}:
            del pkg["exports"]
        man.pop("app", None)
        json.dump(pkg, open(pj, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
        open(pj, "a", encoding="utf-8").write("\n")
        # remove now-empty folders
        for dp, dn, fn in sorted(os.walk(pd), key=lambda x: -len(x[0])):
            if dp != pd and not os.listdir(dp):
                os.rmdir(dp)
print(f"total: {total} files {'deleted' if apply else 'would be deleted'}")
