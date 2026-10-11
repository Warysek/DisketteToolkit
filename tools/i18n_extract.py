#!/usr/bin/env python3
"""Vypíše všechny texty k překladu (Tr.T("…") a Tr.F("…", …)) ze zdrojových souborů — unikátně, v pořadí výskytu."""
import re, sys, glob
PAT = re.compile(r'Tr\.[TF]\(\s*"((?:[^"\\\n]|\\.)*)"')
def unescape(s):
    return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t', '"': '"', '\\': '\\'}.get(m.group(1), m.group(1)), s)
seen, out = set(), []
for root in sys.argv[1:]:
    for f in sorted(glob.glob(root + '/**/*.cs', recursive=True)):
        if '/obj/' in f or '/bin/' in f: continue
        for m in PAT.finditer(open(f, encoding='utf-8').read()):
            k = unescape(m.group(1))
            if k not in seen: seen.add(k); out.append((f, k))
for f, k in out:
    print(k.replace('\n', '\\n'))
