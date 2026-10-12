#!/usr/bin/env python3
"""Generates the drive capability table as PNG in English and Czech.

Usage:  python3 tools/capabilities.py          -> docs/drives-en.png, docs/drives-cs.png
Needs Python Playwright with Chromium (the Claude sandbox has it).
Edit ROWS / FEATURES below when the program learns something new.
"""
import html
import pathlib
import sys

Y, M, N = "y", "m", "n"   # works / depends on controller or drive / not possible

# (group or None, (en, cs), usb, internal+fdrawcmd, greaseweazle, (note en, note cs))
ROWS = [
    ("Atari", None),
    (("Atari ST 720 KB (80×2×9)", "Atari ST 720 KB (80×2×9)"), Y, Y, Y,
     ("some USB drives cannot do DD disks — the program detects it", "některé USB mechaniky DD diskety neumějí — program to pozná")),
    (("Atari ST 360 KB single-sided", "Atari ST 360 KB jednostranná"), Y, Y, Y,
     ("USB writes side 0 of a double-sided disk", "USB zapíše na stranu 0 oboustranné diskety")),
    (("Atari 800–820 KB (10 sectors, 81–83 tracks)", "Atari 800–820 KB (10 sektorů, 81–83 stop)"), N, Y, Y, ("", "")),
    (("Atari 900 KB (11 sectors, Twister)", "Atari 900 KB (11 sektorů, Twister)"), N, M, Y,
     ("only some controllers handle 11 sectors", "11 sektorů zvládne jen některý řadič")),
    (("Atari protected games (.STX / .IPF)", "Atari chráněné hry (.STX / .IPF)"), N, M, Y,
     ("GW: flux .SCP/.HFE, writes IPF/SCP/HFE; .STX cannot be written", "GW: tok .SCP/.HFE, zápis IPF/SCP/HFE; .STX zapsat nejde")),
    (("Atari STE/TT/Falcon 1.44 MB HD", "Atari STE/TT/Falcon 1,44 MB HD"), Y, Y, Y, ("", "")),
    ("PC", None),
    (("PC 720 KB / 1.44 MB", "PC 720 KB / 1,44 MB"), Y, Y, Y, ("", "")),
    (("PC 640 KB (8 sectors)", "PC 640 KB (8 sektorů)"), Y, Y, Y, ("", "")),
    (("PC DMF 1.68 MB / XDF 1.72 MB", "PC DMF 1,68 MB / XDF 1,72 MB"), N, Y, Y, ("", "")),
    (("PC 2.88 MB ED", "PC 2,88 MB ED"), N, N, M, ("needs an ED drive and ED disk", "potřebuje ED mechaniku a ED disketu")),
    ("Commodore", None),
    (("Amiga 880 KB DD / 1.76 MB HD", "Amiga 880 KB DD / 1,76 MB HD"), N, N, Y,
     ("without Greaseweazle only .ADF images (Gotek, emulator)", "bez Greaseweazle jen obraz .ADF (Gotek, emulátor)")),
    (("Amiga CrossDOS 720 KB (PC disk for the Amiga)", "Amiga CrossDOS 720 KB (PC disketa pro Amigu)"), Y, Y, Y, ("", "")),
    (("Commodore 1581 (C64/C128) 800 KB", "Commodore 1581 (C64/C128) 800 KB"), N, Y, Y, ("", "")),
    ("Apple", None),
    (("Mac 1.44 MB HD (HFS)", "Mac 1,44 MB HD (HFS)"), Y, Y, Y, ("", "")),
    (("Mac 400 / 800 KB (GCR)", "Mac 400 / 800 KB (GCR)"), N, N, Y, ("", "")),
    (("Apple IIgs / IIc+ 800 KB (GCR)", "Apple IIgs / IIc+ 800 KB (GCR)"), N, N, Y, ("", "")),
    (("Other machines", "Další stroje"), None),
    (("MSX 720 / 360 KB", "MSX 720 / 360 KB"), Y, Y, Y, ("", "")),
    (("Amstrad PCW 720 KB (CP/M)", "Amstrad PCW 720 KB (CP/M)"), Y, Y, Y, ("", "")),
    (("Sinclair QL 720 KB / 1.44 MB", "Sinclair QL 720 KB / 1,44 MB"), Y, Y, Y, ("", "")),
    (("SAM Coupé 800 KB", "SAM Coupé 800 KB"), N, Y, Y, ("", "")),
    (("Acorn Archimedes 800 KB / 1.6 MB", "Acorn Archimedes 800 KB / 1,6 MB"), N, Y, Y, ("1024-byte sectors", "sektory 1024 B")),
    (("NEC PC-98 / Sharp X68000 1.23 MB", "NEC PC-98 / Sharp X68000 1,23 MB"), M, M, Y,
     ("only with a “3-mode” drive (360 rpm)", "jen s mechanikou „3-mode“ (360 ot./min)")),
    (("Music instruments (Roland, Korg, Yamaha, Akai…)", "Hudební nástroje (Roland, Korg, Yamaha, Akai…)"), M, Y, Y,
     ("FAT12 works over USB, Akai/Ensoniq use their own format", "FAT12 přes USB jde, Akai/Ensoniq mají vlastní formát")),
    (("Linux / MINIX 720 KB / 1.44 MB", "Linux / MINIX 720 KB / 1,44 MB"), Y, Y, Y, ("", "")),
]

FEATURES = [
    (("Read, write, format standard disks", "Čtení, zápis, formát standardních disket"), Y, Y, Y, ("", "")),
    (("Batch backup of many disks", "Hromadná záloha více disket"), Y, Y, Y,
     ("numbered images, a disk that wasn't swapped is detected", "číslované obrazy, nevyměněnou disketu pozná")),
    (("Surface test with sector map", "Test povrchu s mapou sektorů"), Y, Y, Y, ("", "")),
    (("FAT check and safe repair", "Kontrola a bezpečná oprava FAT"), Y, Y, Y,
     ("backup first, only changed sectors are written", "nejdřív záloha, zapisují se jen změněné sektory")),
    (("Repair format (sector shift)", "Opravný formát (posun sektorů)"), N, Y, N,
     ("experimental — a defect is covered by an extra sector", "experimentální — vadu překryje sektor navíc")),
    (("Step rate setting", "Nastavení step rate"), N, Y, Y, ("", "")),
    (("Extra tracks 81–84", "Stopy 81–84 navíc"), N, Y, Y, ("", "")),
    (("Copy protections", "Kopírovací ochrany"), N, M, Y,
     ("fdrawcmd: simple ones only, not in the program yet", "fdrawcmd: jen jednodušší, program zatím ne")),
    (("Flux backup (.SCP / .HFE)", "Záloha magnetického toku (.SCP / .HFE)"), N, N, Y, ("", "")),
    (("Boot disks FreeDOS, MS-DOS, Atari AUTO", "Spouštěcí diskety FreeDOS, MS-DOS, Atari AUTO"), Y, Y, Y, ("", "")),
    (("Amiga boot disk", "Spouštěcí disketa Amiga"), N, N, Y, ("otherwise .ADF image only", "jinak jen obraz .ADF")),
]

T = {
    "en": dict(sub='Which drive can do what — 3.5" disks for Atari, PC, Amiga, Mac and other computers',
               fmt="System / format", usb="USB floppy drive", usb2="e.g. Sony USB-FDU",
               int="Internal drive", int2="+ fdrawcmd.sys driver", gw="Greaseweazle", gw2="+ any PC drive",
               note="Note", feats="Features", feat="Feature", int_short="Internal + fdrawcmd.sys",
               leg_y="works", leg_m="depends on controller / drive", leg_n="not possible",
               foot="USB floppy drive: common Atari, PC, MSX and Mac HD disks. · Internal drive with fdrawcmd.sys: also extended "
                    "Atari 800–900 KB formats, Commodore 1581, SAM Coupé, Acorn.<br>Greaseweazle: the only option for Amiga, "
                    "Mac 400/800 KB, Apple II, protected games and flux backups."),
    "cs": dict(sub='Co umí která mechanika — 3,5" diskety Atari, PC, Amiga, Mac a dalších počítačů',
               fmt="Systém / formát", usb="USB disketovka", usb2="např. Sony USB-FDU",
               int="Interní mechanika", int2="+ ovladač fdrawcmd.sys", gw="Greaseweazle", gw2="+ běžná mechanika",
               note="Poznámka", feats="Funkce", feat="Funkce", int_short="Interní + fdrawcmd.sys",
               leg_y="jde", leg_m="záleží na řadiči / mechanice", leg_n="nejde",
               foot="USB disketovka: běžné diskety Atari, PC, MSX a Mac HD. · Interní mechanika s fdrawcmd.sys: navíc rozšířené "
                    "formáty Atari 800–900 KB, Commodore 1581, SAM Coupé, Acorn.<br>Greaseweazle: jako jediný Amiga, Mac 400/800 KB, "
                    "Apple II, chráněné hry a záloha magnetického toku."),
}

CSS = """
body{margin:0;background:#f5f6f8;font-family:"DejaVu Sans",sans-serif;color:#1f2328}
.wrap{width:1400px;padding:36px 44px 30px;box-sizing:border-box}
.head{background:#1c212b;color:#e6edf3;border-radius:14px;padding:22px 28px;display:flex;align-items:baseline;gap:18px}
.head h1{font-family:Poppins,sans-serif;font-size:30px;margin:0;font-weight:700;white-space:nowrap}
.head span{color:#9aa4b0;font-size:16px}
table{width:100%;border-collapse:separate;border-spacing:0;background:#fff;border-radius:12px;overflow:hidden;margin-top:18px;box-shadow:0 1px 3px rgba(0,0,0,.08)}
th{background:#eef1f5;font-size:14px;text-align:center;padding:12px 8px;color:#444c56;border-bottom:1px solid #dde1e6}
th.f{text-align:left;padding-left:18px}
th .sub{display:block;font-weight:400;font-size:12px;color:#6b7580;margin-top:2px}
td{padding:7px 8px;border-bottom:1px solid #eef0f3;text-align:center;font-size:15px}
td.f{text-align:left;padding-left:18px;font-weight:500}
td.note{text-align:left;color:#6b7580;font-size:13px;width:390px}
tr.grp td{background:#f8f9fb;font-family:Poppins,sans-serif;font-weight:700;font-size:15px;text-align:left;padding:9px 18px;color:#0a64d6;border-bottom:1px solid #e3e7ec}
.b{display:inline-block;width:28px;height:28px;line-height:28px;border-radius:50%;font-weight:700;font-size:16px;text-align:center}
.y{background:#dafbe1;color:#1a7f37} .m{background:#fff4d4;color:#9a6700} .n{background:#f0f1f3;color:#9aa1a9}
h2{font-family:Poppins,sans-serif;font-size:20px;margin:28px 0 0}
.legend{margin-top:16px;display:flex;gap:26px;font-size:14px;color:#444c56;align-items:center}
.foot{margin-top:14px;font-size:13px;color:#6b7580;line-height:1.5}
"""

MARK = {Y: '<span class="b y">✓</span>', M: '<span class="b m">?</span>', N: '<span class="b n">✗</span>'}


def rows_html(rows, li):
    out = []
    for r in rows:
        if r[1] is None:
            name = r[0] if isinstance(r[0], str) else r[0][li]
            out.append(f'<tr class="grp"><td colspan="5">{html.escape(name)}</td></tr>')
            continue
        names, usb, intl, gw, notes = r
        out.append(f'<tr><td class="f">{html.escape(names[li])}</td><td>{MARK[usb]}</td><td>{MARK[intl]}</td>'
                   f'<td>{MARK[gw]}</td><td class="note">{html.escape(notes[li])}</td></tr>')
    return "".join(out)


def page(lang):
    t, li = T[lang], (0 if lang == "en" else 1)
    return f"""<!doctype html><html><head><meta charset="utf-8"><style>{CSS}</style></head><body><div class="wrap">
<div class="head"><h1>Diskette Toolkit</h1><span>{t['sub']}</span></div>
<table><tr><th class="f" style="width:420px">{t['fmt']}</th><th style="width:150px">{t['usb']}<span class="sub">{t['usb2']}</span></th>
<th style="width:170px">{t['int']}<span class="sub">{t['int2']}</span></th><th style="width:150px">{t['gw']}<span class="sub">{t['gw2']}</span></th>
<th class="f">{t['note']}</th></tr>{rows_html(ROWS, li)}</table>
<h2>{t['feats']}</h2>
<table><tr><th class="f" style="width:420px">{t['feat']}</th><th style="width:150px">{t['usb']}</th><th style="width:170px">{t['int_short']}</th>
<th style="width:150px">{t['gw']}</th><th class="f">{t['note']}</th></tr>{rows_html(FEATURES, li)}</table>
<div class="legend"><span>{MARK[Y]} {t['leg_y']}</span><span>{MARK[M]} {t['leg_m']}</span><span>{MARK[N]} {t['leg_n']}</span></div>
<div class="foot">{t['foot']}</div>
</div></body></html>"""


def main():
    from playwright.sync_api import sync_playwright
    root = pathlib.Path(__file__).resolve().parent.parent
    out_dir = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else root / "docs"
    out_dir.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch()
        pg = browser.new_page(viewport={"width": 1400, "height": 1000}, device_scale_factor=2)
        for lang in ("en", "cs"):
            pg.set_content(page(lang))
            pg.wait_for_timeout(300)
            target = out_dir / f"drives-{lang}.png"
            pg.locator(".wrap").screenshot(path=str(target))
            print(target)
        browser.close()


if __name__ == "__main__":
    main()
