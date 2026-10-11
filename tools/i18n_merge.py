#!/usr/bin/env python3
"""Spojí sousední řetězce spojené + ("a " + "b" -> "a b", $"x{y}" + "z" -> $"x{y}z") — kvůli celým větám v překladu."""
import re, sys
LIT = r'(\$?"(?:[^"\\\n]|\\.)*")'
PAT = re.compile(LIT + r'(\s*\+\s*)' + LIT)
def esc_braces(s): return s.replace('{', '{{').replace('}', '}}')
def merge(a, b):
    ai, bi = a.startswith('$'), b.startswith('$')
    ab, bb = a[ai + 1:-1], b[bi + 1:-1]
    if ai or bi:
        if not ai: ab = esc_braces(ab)
        if not bi: bb = esc_braces(bb)
        return '$"' + ab + bb + '"'
    return '"' + ab + bb + '"'
for f in sys.argv[1:]:
    s = open(f, encoding='utf-8').read()
    lines = s.split('\n')
    # nepracujeme uvnitř komentářů: jednoduchá ochrana — spojujeme jen, když mezi literály není //
    while True:
        m = None
        for mm in PAT.finditer(s):
            if '//' in mm.group(2): continue
            # vyloučit případ, kdy před prvním literálem je @ (verbatim)
            if mm.start() > 0 and s[mm.start() - 1] == '@': continue
            m = mm; break
        if not m: break
        s = s[:m.start()] + merge(m.group(1), m.group(3)) + s[m.end():]
    open(f, 'w', encoding='utf-8').write(s)
