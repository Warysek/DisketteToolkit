#!/usr/bin/env python3
"""
Převede české texty v C# souborech na volání překladu:
    "Český text"            -> Tr.T("Český text")
    $"Stopa {t}, strana {s}" -> Tr.F("Stopa {0}, strana {1}", t, s)
Přeskakuje komentáře, znaky, úseky mezi // i18n:raw-begin a // i18n:raw-end, a texty už obalené Tr.T/Tr.F.
Použití: i18n_rewrite.py [--list] soubor.cs ...   (--list jen vypíše nalezené klíče)
"""
import re, sys

CZ = re.compile(r"[áčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ]")
EXTRA = set()   # doplňkové texty bez diakritiky (plné znění), viz --extra soubor

def is_user_text(s):
    return bool(CZ.search(s)) or s in EXTRA

class Src:
    def __init__(self, text): self.t = text; self.i = 0

def read_regular(t, i):
    """i ukazuje na úvodní " ; vrátí (konec_za_uvozovkou, obsah_raw)"""
    j = i + 1
    while j < len(t):
        c = t[j]
        if c == '\\': j += 2; continue
        if c == '"': return j + 1, t[i+1:j]
        j += 1
    raise ValueError("neukončený řetězec")

def read_verbatim(t, i):
    """i ukazuje na " po @ ; obsah bez úprav"""
    j = i + 1
    while j < len(t):
        if t[j] == '"':
            if j + 1 < len(t) and t[j+1] == '"': j += 2; continue
            return j + 1, t[i+1:j]
        j += 1
    raise ValueError("neukončený verbatim řetězec")

def read_interp(t, i, verbatim):
    """i ukazuje na " ; vrátí (konec, seznam částí) kde část = ('lit', text) | ('hole', expr, align, fmt)"""
    parts = []
    j = i + 1
    lit = []
    while j < len(t):
        c = t[j]
        if not verbatim and c == '\\':
            lit.append(t[j:j+2]); j += 2; continue
        if c == '"':
            if verbatim and j + 1 < len(t) and t[j+1] == '"':
                lit.append('""'); j += 2; continue
            parts.append(('lit', ''.join(lit)))
            return j + 1, parts
        if c == '{':
            if t[j+1] == '{': lit.append('{{'); j += 2; continue
            parts.append(('lit', ''.join(lit))); lit = []
            k, expr, align, fmt = read_hole(t, j + 1)
            parts.append(('hole', expr, align, fmt))
            j = k; continue
        if c == '}':
            if t[j+1] == '}': lit.append('}}'); j += 2; continue
        lit.append(c); j += 1
    raise ValueError("neukončený interpolovaný řetězec")

def read_hole(t, j):
    """čte výraz v {} až po odpovídající }, vrací (za }, expr, align, fmt)"""
    depth = 0
    start = j
    align_pos = fmt_pos = None
    while j < len(t):
        c = t[j]
        if c in '([{': depth += 1
        elif c in ')]':
            depth -= 1
        elif c == '}':
            if depth == 0:
                end = j
                break
            depth -= 1
        elif c == '"' or (c in '$@' and j + 1 < len(t) and t[j+1] in '"$@'):
            j = skip_string(t, j); continue
        elif c == "'":
            j = skip_char(t, j); continue
        elif c == ':' and depth == 0 and fmt_pos is None:
            # ternární ?: v díře musí být v závorkách, takže : na nulté úrovni = formát
            fmt_pos = j
        elif c == ',' and depth == 0 and fmt_pos is None and align_pos is None:
            align_pos = j
        j += 1
    else:
        raise ValueError("neukončená díra")
    expr_end = min(p for p in [align_pos, fmt_pos, end] if p is not None)
    expr = t[start:expr_end].strip()
    align = t[align_pos+1:(fmt_pos or end)].strip() if align_pos is not None else None
    fmt = t[fmt_pos+1:end] if fmt_pos is not None else None
    return end + 1, expr, align, fmt

def skip_char(t, j):
    k = j + 1
    while k < len(t):
        if t[k] == '\\': k += 2; continue
        if t[k] == "'": return k + 1
        k += 1
    return k

def skip_string(t, j):
    # vrátí index za řetězcem začínajícím na j (", @", $", $@", @$")
    pre = ''
    k = j
    while t[k] in '$@': pre += t[k]; k += 1
    if '$' in pre:
        end, _ = read_interp(t, k, '@' in pre)
        return end
    if '@' in pre:
        end, _ = read_verbatim(t, k)
        return end
    end, _ = read_regular(t, k)
    return end

def csharp_escape(s):
    return s.replace('\\', '\\\\').replace('"', '\\"')

def interp_literal_to_regular(lit, verbatim):
    """literál z interpolace (už s {{ }}) převede na obsah běžného řetězce"""
    if verbatim:
        return csharp_escape(lit.replace('""', '"'))
    return lit

def transform(text, keys, in_hole=False):
    out = []
    i = 0
    t = text
    n = len(t)
    raw_skip = False
    while i < n:
        c = t[i]
        # komentáře
        if t.startswith('//', i):
            e = t.find('\n', i)
            e = n if e < 0 else e
            comment = t[i:e]
            if 'i18n:raw-begin' in comment: raw_skip = True
            if 'i18n:raw-end' in comment: raw_skip = False
            out.append(comment); i = e; continue
        if t.startswith('/*', i):
            e = t.find('*/', i) + 2
            out.append(t[i:e]); i = e; continue
        if c == "'":
            e = skip_char(t, i); out.append(t[i:e]); i = e; continue
        # řetězce
        if c == '"' or (c in '$@' and i + 1 < n and (t[i+1] == '"' or (t[i+1] in '$@' and i + 2 < n and t[i+2] == '"'))):
            pre = ''
            k = i
            while t[k] in '$@': pre += t[k]; k += 1
            already = ''.join(out)[-6:].endswith('Tr.T(') or ''.join(out)[-6:].endswith('Tr.F(')
            if '$' in pre:
                end, parts = read_interp(t, k, '@' in pre)
                original = t[i:end]
                lits = ''.join(p[1] for p in parts if p[0] == 'lit')
                if raw_skip or already or not is_user_text(lits.replace('{{', '').replace('}}', '')):
                    # i tak projdeme výrazy uvnitř děr (mohou obsahovat české texty)
                    rebuilt = pre + '"'
                    for p in parts:
                        if p[0] == 'lit': rebuilt += p[1]
                        else:
                            inner = transform(p[1], keys, True) if not raw_skip else p[1]
                            rebuilt += '{' + inner + (',' + p[2] if p[2] else '') + (':' + p[3] if p[3] is not None else '') + '}'
                    rebuilt += '"'
                    out.append(rebuilt); i = end; continue
                template = ''
                args = []
                for p in parts:
                    if p[0] == 'lit':
                        template += interp_literal_to_regular(p[1], '@' in pre)
                    else:
                        idx = len(args)
                        args.append(transform(p[1], keys, True))
                        template += '{' + str(idx) + (',' + p[2] if p[2] else '') + (':' + p[3] if p[3] is not None else '') + '}'
                keys.append(template)
                if args:
                    out.append('Tr.F("' + template + '", ' + ', '.join(args) + ')')
                else:
                    out.append('Tr.T("' + template.replace('{{', '{').replace('}}', '}') + '")')
                i = end; continue
            if '@' in pre:
                end, content = read_verbatim(t, k)
                if raw_skip or already or not is_user_text(content):
                    out.append(t[i:end]); i = end; continue
                reg = csharp_escape(content.replace('""', '"')).replace('\r\n', '\\n').replace('\n', '\\n')
                keys.append(reg)
                out.append('Tr.T("' + reg + '")'); i = end; continue
            end, content = read_regular(t, i)
            if raw_skip or already or not is_user_text(content):
                out.append(t[i:end]); i = end; continue
            keys.append(content)
            out.append('Tr.T("' + content + '")'); i = end; continue
        out.append(c); i += 1
    return ''.join(out)

def fix_consts(text):
    # const string X = Tr.T(...)  -> lokální proměnná / vlastnost
    text = re.sub(r'(\b(?:public|internal|private)\s+)const string (\w+) =\s*(Tr\.[TF]\()', r'\1static string \2 => \3', text)
    text = re.sub(r'\bconst string (\w+) =\s*(Tr\.[TF]\()', r'string \1 = \2', text)
    return text

def main():
    args = sys.argv[1:]
    list_only = False
    if args and args[0] == '--list': list_only = True; args = args[1:]
    if args and args[0] == '--extra':
        for line in open(args[1], encoding='utf-8'):
            line = line.rstrip('\n')
            if line and not line.startswith('#'): EXTRA.add(line)
        args = args[2:]
    all_keys = []
    for f in args:
        src = open(f, encoding='utf-8').read()
        keys = []
        new = fix_consts(transform(src, keys))
        all_keys += keys
        if not list_only and new != src:
            if 'using AtariFloppy.Core.I18n;' not in new and 'namespace AtariFloppy.Core.I18n' not in new:
                # vložit using za poslední using nahoře
                m = list(re.finditer(r'^using [^\n]+;\n', new, re.M))
                pos = m[-1].end() if m else 0
                new = new[:pos] + 'using AtariFloppy.Core.I18n;\n' + new[pos:]
            open(f, 'w', encoding='utf-8').write(new)
    if list_only:
        for k in all_keys: print(k)

if __name__ == '__main__':
    main()
