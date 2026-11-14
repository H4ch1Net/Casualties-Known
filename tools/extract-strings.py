"""
Collects every player-facing English line in the mod (Lang.T / Lang.F / Lang.N / Lang.Pack
calls) and writes lang/template.txt, the starting point for a translation.

    python tools/extract-strings.py                 writes lang/template.txt
    python tools/extract-strings.py lang/zh-CN.txt  adds new lines to that translation,
                                                    keeps its existing ones, and lists
                                                    lines the mod no longer uses

The file format (see src/CCoop.Plugin/UI/Lang.cs): an English line, then its translation
on the next line after "= ". Keep {0}, {1}... (names and numbers) and the *asterisks*
of emotes. Lines starting with # are comments; \\n stands for a line break.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "CCoop.Plugin")
LANG = os.path.join(ROOT, "lang")

CALL = re.compile(r'Lang\.(T|F|N|Pack)\(\s*"((?:[^"\\]|\\.)*)"')
ESC = {'n': '\\n', 't': '\t', '"': '"', '\\': '\\', "'": "'", '0': ''}


def unescape(s):
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            out.append(ESC.get(s[i + 1], s[i + 1]))
            i += 2
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def collect():
    seen, order = {}, []
    for dirpath, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj", "Dev")]
        for fn in sorted(files):
            if not fn.endswith(".cs") or fn == "Lang.cs":
                continue
            path = os.path.join(dirpath, fn)
            text = open(path, encoding="utf-8").read()
            for m in CALL.finditer(text):
                english = unescape(m.group(2))
                # Nothing to translate in "{0}: {1}" and the like.
                if not re.search(r'[A-Za-z]', re.sub(r'\{\d+\}', '', english)):
                    continue
                where = os.path.relpath(path, SRC).replace(os.sep, "/")
                if english not in seen:
                    seen[english] = where
                    order.append(english)
    return order, seen


def read_translation(path):
    table, english = {}, None
    for raw in open(path, encoding="utf-8"):
        line = raw.rstrip("\r\n")
        if not line or line.startswith("#"):
            continue
        if line.startswith("= ") or line == "=":
            if english is not None:
                table[english] = line[2:] if len(line) > 2 else ""
            english = None
            continue
        english = line
    return table


def write(path, order, seen, table=None, header=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(header or "")
        last = None
        for english in order:
            if seen[english] != last:
                f.write("\n# " + seen[english] + "\n")
                last = seen[english]
            f.write(english + "\n")
            f.write("= " + (table.get(english, "") if table else "") + "\n")


def main():
    order, seen = collect()
    if len(sys.argv) > 1:
        path = sys.argv[1]
        old = read_translation(path) if os.path.exists(path) else {}
        header = "".join(l for l in open(path, encoding="utf-8") if l.startswith("# ") and "CasualtiesCoop" in l) if os.path.exists(path) else ""
        write(path, order, seen, old, header or "# CasualtiesCoop translation\n")
        missing = [e for e in order if not old.get(e)]
        unused = [e for e in old if e not in seen]
        print("%s: %d lines, %d still to translate" % (path, len(order), len(missing)))
        for e in unused:
            print("  no longer used: " + e)
        return
    header = ("# CasualtiesCoop: every English line the mod shows.\n"
              "# To translate: copy this file to lang/<the game's language code>.txt (EN, zh-CN, ...),\n"
              "# write each translation after \"= \", and leave {0}, {1}... where they make sense.\n"
              "# Keep button words short (Join, Kick, Check, Take...): the co-op window gives them little room.\n"
              "# Untranslated lines show in English. Put the file beside the mod (BepInEx/plugins/CasualtiesCoop/lang/)\n"
              "# or in the game's data folder under CasualtiesCoop/lang/.\n")
    write(os.path.join(LANG, "template.txt"), order, seen, None, header)
    print("lang/template.txt: %d lines" % len(order))


if __name__ == "__main__":
    main()
