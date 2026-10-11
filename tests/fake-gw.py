#!/usr/bin/env python3
# Simulovaný Greaseweazle pro testy: "disketa" je soubor s magnetickým tokem (AFLOPPY_FAKE_DISK, .hfe/.scp).
# read  = gw convert <disketa> <výstup>,  write = gw convert <vstup> <disketa>,  info = pevný text.
import os, sys, subprocess
args = sys.argv[1:]
disk = os.environ.get("AFLOPPY_FAKE_DISK", "")
if not args:
    sys.exit(1)
action, rest = args[0], args[1:]
drop_val = {"--device", "--drive", "--retries", "--revs", "--seek-retries", "--precomp", "--densel", "--dd"}
drop_flag = {"--no-verify", "--erase-empty", "--pre-erase", "--raw"}
keep, files, i = [], [], 0
while i < len(rest):
    a = rest[i]
    if a in drop_val: i += 2; continue
    if a in drop_flag: i += 1; continue
    if a in ("--format", "--diskdefs", "--tracks"): keep += [a, rest[i+1]]; i += 2; continue
    files.append(a); i += 1
if action == "info":
    print("Host Tools: fake\nDevice:\n  Model:     Greaseweazle V4.1\n  Firmware:  1.6"); sys.exit(0)
if action == "delays":
    print("Step Delay: 3000us"); sys.exit(0)
if not os.path.exists(disk) and action == "read":
    print("** FATAL ERROR:\nNo index pulse detected"); sys.exit(1)
if action == "read":
    cmd = ["gw", "convert"] + keep + [disk, files[-1]]
elif action == "write":
    cmd = ["gw", "convert"] + keep + [files[-1], disk]
elif action == "erase":
    if os.path.exists(disk): os.remove(disk)
    print("T0.0: Erasing Track"); sys.exit(0)
else:
    sys.exit(2)
p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
out = p.stdout
if action == "write":
    # convert vypisuje stopy jako čtení — převedeme na hlášky zápisu jako u skutečného gw write
    lines = []
    for l in out.splitlines():
        if l.startswith("T") and ":" in l and "sectors" in l:
            lines.append(l.split(":")[0] + ": Writing Track (Bitcells)")
        elif not l.startswith("***"):
            lines.append(l)
    lines.append("All tracks verified")
    out = "\n".join(lines)
print(out)
sys.exit(p.returncode)
