#!/usr/bin/env python3
"""Zkontroluje, že každý text Tr.T/Tr.F ve zdrojích má překlad v jazykovém souboru.
Použití: i18n_check.py src/Core/I18n/en.lang src/Core src/Gui ..."""
import re, sys, glob, subprocess, os
lang, roots = sys.argv[1], sys.argv[2:]
keys = set()
for l in open(lang, encoding='utf-8'):
    l = l.rstrip('\n')
    if l and not l.startswith('#') and ' = ' in l: keys.add(l.split(' = ', 1)[0].strip().replace(' \\= ', ' = '))
here = os.path.dirname(__file__)
# UTF-8 i na Windows (jinak cp1250 a české uvozovky / šipky skript shodí)
used = subprocess.run([sys.executable, os.path.join(here, 'i18n_extract.py')] + roots, capture_output=True, encoding='utf-8',
                      env={**os.environ, 'PYTHONIOENCODING': 'utf-8'}, check=True).stdout.splitlines()
miss = [k for k in used if k.strip() and k.strip() not in keys]
for k in miss: print('CHYBÍ:', k)
print(f'{len(used)} textů, {len(miss)} bez překladu')
sys.exit(1 if miss else 0)
