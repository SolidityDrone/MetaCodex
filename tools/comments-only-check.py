#!/usr/bin/env python3
"""Prove that a change to C# files touched only comments and whitespace.

Usage: tools/comments-only-check.py <git-ref> [pathspec ...]   (pathspecs relative to the repository root)

For every .cs file that differs between <git-ref> and the working tree (staged or not, plus untracked .cs files),
both versions are reduced to their tokens: comments (//, ///, /* */) are dropped, whitespace between tokens is
ignored, and string literals (regular, verbatim @"", interpolated $"" with their {holes}, raw \"\"\"), char literals and
everything else are kept exactly. The check fails when the two token lists differ: then the change is not a
comment-only change. A new or deleted file fails unless it holds no code. Line endings (CRLF / LF) and a UTF-8 BOM
count as whitespace.

Exit code: 0 every file is comment-only, 1 some file changed code, 2 usage or git error.
"""
import subprocess
import sys


def lex(src):
    """Tokens of C# source as (text, line). Comments are skipped; literals keep their exact text."""
    src = src.replace("\r\n", "\n").replace("\r", "\n").lstrip("﻿")
    toks = []
    i, n, line = 0, len(src), 1

    def emit(text, at_line):
        toks.append((text, at_line))

    def string_end(j, verbatim):
        """Index after the closing quote of a regular or verbatim string whose opening quote is at j - 1."""
        while j < n:
            c = src[j]
            if verbatim:
                if c == '"':
                    if j + 1 < n and src[j + 1] == '"':
                        j += 2
                        continue
                    return j + 1
            else:
                if c == "\\":
                    j += 2
                    continue
                if c == '"':
                    return j + 1
            j += 1
        return n

    def interpolated(j, verbatim):
        """Tokens of an interpolated string body starting at j (after the opening quote); returns (tokens, end)."""
        parts, lit, start_line = [], [], src.count("\n", 0, j) + 1
        while j < n:
            c = src[j]
            if c == "{":
                if j + 1 < n and src[j + 1] == "{":
                    lit.append("{{")
                    j += 2
                    continue
                parts.append(("$lit", "".join(lit)))
                lit = []
                depth, k = 1, j + 1
                # the hole ends at the matching }; nested strings, chars and comments are skipped as code
                hole_start = k
                while k < n and depth:
                    d = src[k]
                    if d == "{":
                        depth += 1
                        k += 1
                    elif d == "}":
                        depth -= 1
                        k += 1
                    elif d == '"' or (d in "@$" and k + 1 < n and src[k + 1] in '"@$'):
                        k = skip_literal(k)
                    elif d == "'":
                        k = char_end(k)
                    elif d == "/" and k + 1 < n and src[k + 1] == "*":
                        e = src.find("*/", k + 2)
                        k = n if e < 0 else e + 2
                    else:
                        k += 1
                hole = src[hole_start:k - 1] if not depth else src[hole_start:k]
                parts.append(("$hole", tuple(t for t, _ in lex(hole))))
                j = k
                continue
            if c == "}" and j + 1 < n and src[j + 1] == "}":
                lit.append("}}")
                j += 2
                continue
            if verbatim:
                if c == '"':
                    if j + 1 < n and src[j + 1] == '"':
                        lit.append('""')
                        j += 2
                        continue
                    break
            else:
                if c == "\\":
                    lit.append(src[j:j + 2])
                    j += 2
                    continue
                if c == '"':
                    break
            lit.append(c)
            j += 1
        parts.append(("$lit", "".join(lit)))
        return parts, min(j + 1, n), start_line

    def raw_end(j):
        """Raw string literal (C# 11) at j: a run of 3+ quotes, closed by the same run. Returns the end index."""
        q = j
        while q < n and src[q] == '"':
            q += 1
        run = q - j
        e = src.find('"' * run, q)
        return n if e < 0 else e + run

    def char_end(j):
        k = j + 1
        while k < n:
            if src[k] == "\\":
                k += 2
                continue
            if src[k] == "'" or src[k] == "\n":
                return k + 1
            k += 1
        return n

    def skip_literal(j):
        """End index of the string literal that starts at j (any prefix of @ and $)."""
        k = j
        while k < n and src[k] in "@$":
            k += 1
        verbatim = "@" in src[j:k]
        interp = "$" in src[j:k]
        if not verbatim and src.startswith('"""', k):
            return raw_end(k)
        if interp:
            _, end, _ = interpolated(k + 1, verbatim)
            return end
        return string_end(k + 1, verbatim)

    while i < n:
        c = src[i]
        if c == "\n":
            line += 1
            i += 1
            continue
        if c.isspace():
            i += 1
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            e = src.find("\n", i)
            i = n if e < 0 else e
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            e = src.find("*/", i + 2)
            end = n if e < 0 else e + 2
            line += src.count("\n", i, end)
            i = end
            continue
        # string literals: "..", @"..", $"..", $@"..", @$"..", raw """..""" (with any $ prefix)
        k = i
        while k < n and src[k] in "@$" and k - i < 4:
            k += 1
        if k < n and src[k] == '"' and (k == i or set(src[i:k]) <= set("@$")):
            prefix = src[i:k]
            verbatim, interp = "@" in prefix, "$" in prefix
            if not verbatim and src.startswith('"""', k):  # raw (C# 11); @"""" is a verbatim string of one quote
                end = raw_end(k)
                emit(src[i:end], line)
            elif interp:
                parts, end, start_line = interpolated(k + 1, verbatim)
                emit((prefix, tuple(parts)), start_line)
            else:
                end = string_end(k + 1, verbatim)
                emit(src[i:end], line)
            line += src.count("\n", i, end)
            i = end
            continue
        if c == "'":
            end = char_end(i)
            emit(src[i:end], line)
            i = end
            continue
        if c.isalnum() or c == "_" or c == "@":
            k = i + 1
            while k < n and (src[k].isalnum() or src[k] == "_"):
                k += 1
            emit(src[i:k], line)
            i = k
            continue
        emit(c, line)
        i += 1
    return toks


def git(top, *args, check=True):
    r = subprocess.run(["git", "-C", top, *args], capture_output=True)
    if check and r.returncode != 0:
        sys.stderr.write(r.stderr.decode("utf-8", "replace"))
        sys.exit(2)
    return r


def decode(b):
    return b.decode("utf-8", "surrogateescape")


def show(t):
    return t if isinstance(t, str) else t[0] + '"…"'


def main(argv):
    if len(argv) < 2 or argv[1] in ("-h", "--help"):
        print(__doc__.strip())
        return 2 if len(argv) < 2 else 0
    ref, specs = argv[1], argv[2:]
    top = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True)
    if top.returncode != 0:
        sys.stderr.write("not inside a git repository\n")
        return 2
    top = top.stdout.strip()
    if git(top, "rev-parse", "--verify", "--quiet", ref + "^{commit}", check=False).returncode != 0:
        sys.stderr.write(f"unknown git ref: {ref}\n")
        return 2
    pathspec = ["--", *(specs or ["*.cs"])]
    changed = []
    for row in decode(git(top, "diff", "--name-status", "--no-renames", "-z", ref, *pathspec).stdout).split("\0"):
        if row:
            changed.append(row)
    entries = [(changed[k], changed[k + 1]) for k in range(0, len(changed) - 1, 2)]
    for p in decode(git(top, "ls-files", "--others", "--exclude-standard", "-z", *pathspec).stdout).split("\0"):
        if p:
            entries.append(("A", p))
    entries = [(s, p) for s, p in entries if p.endswith(".cs")]
    bad = 0
    for status, path in entries:
        old = "" if status == "A" else decode(git(top, "show", f"{ref}:{path}").stdout)
        try:
            with open(f"{top}/{path}", "rb") as f:
                new = "" if status == "D" else decode(f.read())
        except FileNotFoundError:
            new = ""
        a, b = lex(old), lex(new)
        if [t for t, _ in a] == [t for t, _ in b]:
            continue
        bad += 1
        k = 0
        while k < min(len(a), len(b)) and a[k][0] == b[k][0]:
            k += 1
        where_a = f"line {a[k][1]} `{show(a[k][0])}`" if k < len(a) else "end of file"
        where_b = f"line {b[k][1]} `{show(b[k][0])}`" if k < len(b) else "end of file"
        what = {"A": "new file with code", "D": "deleted file had code"}.get(status, "code changed")
        print(f"FAIL {path}: {what}; first difference: {ref} {where_a} -> working tree {where_b}")
    if bad:
        print(f"{bad} of {len(entries)} changed .cs file(s) changed more than comments and whitespace")
        return 1
    print(f"ok: {len(entries)} changed .cs file(s), comments and whitespace only")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
