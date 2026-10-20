Diskette Toolkit 0.9
====================

Read, write, format and test floppy disks and disk images for the Atari ST and many
other 3.5" systems (PC, MSX, Mac, Amiga, Amstrad PCW, MINIX, Linux, ...) on
Windows 10 / 11 (7 SP1 and 8.1 with .NET Framework 4.8 installed). Inspired by the
Atari program FastCopy. No installation needed — only .NET Framework 4.8, which is
part of Windows 10 and 11.

Project page, downloads and bug reports: https://github.com/Warysek/DisketteToolkit
Support development (voluntary donation): https://ko-fi.com/wary

Files in the ZIP:
  DisketteToolkit.exe        window (GUI), English or Czech — switch in the top right corner
  dtk.exe                    command line (Czech only for now)
  README.txt                 this file (English)
  NAVOD.txt                  full guide in Czech
  LISEZ-MOI.txt              guide in French (thanks to Frederic, github.com/fsagez)
  Drives-EN.png              which drive can do what (Mechaniky-CZ.png = the same in Czech)
  Testy-Tests.html           test checklist for testers — open in a browser, fill in, save
  licence/                   GNU GPL v3 and the FreeDOS boot sector licence

Start: unzip everything (including DisketteToolkit.exe.config), double-click
DisketteToolkit.exe and allow administrator rights (needed for direct drive access).
Try it without hardware:  DisketteToolkit.exe --demo   (simulated drives)

Supported drives
  USB floppy drive       standard PC formats only (640 KB, 720 KB, 1.44 MB)
  Internal drive + fdrawcmd.sys (simonowen.com/fdrawcmd)
                         also Atari 800-900 KB (10/11 sectors, up to 84 tracks),
                         DMF, 128-1024 byte sectors, step rate
  Greaseweazle (gw.exe from github.com/keirf/greaseweazle/releases, put it into
  a "greaseweazle" folder next to the program)
                         everything incl. Amiga, Mac GCR, flux backups (.scp/.hfe)
                         and writing IPF/SCP/HFE with copy protection

Add-ons are downloaded by the program itself (internet needed):
  Greaseweazle: select it as the drive → "Download Greaseweazle Host Tools"
    (latest gw.exe from GitHub incl. CAPSImg.dll, into the program folder),
    later "Greaseweazle updates" → update gw.exe or the device firmware.
  Internal drive: "Install the fdrawcmd.sys driver" downloads the author's signed
    FdInstall.exe from simonowen.com, checks the signature and runs it.
    The driver dates from 2010; Windows 11 Memory integrity may block it.
  Command line: dtk install gw | dtk firmware GW | dtk install fdrawcmd

Boot disks ("Boot disk..." button, or dtk boot ...):
  FreeDOS (downloaded once from the official FreeDOS 1.3 repository, GPL),
  MS-DOS taken from your own Windows (diskcopy.dll, like the old "Create an MS-DOS
  startup disk"), Amiga bootblock ("install", KS 1.3 and 2.0+), Atari ST disk that
  autostarts a program from the AUTO folder, and RawWrite of ready-made images.
  The FreeDOS boot sector is assembled from FreeDOS boot.asm (GPL, see third_party).

Batch backup ("Batch backup..." button): back up a box of disks one after another into
numbered images (disk-001.st, disk-002.st, ...); a disk that wasn't swapped is detected.
Drag an image file from Explorer onto the window to open it.

Drive diagnostics ("Drive diagnostics..." button; internal drive with fdrawcmd.sys or
Greaseweazle, not USB drives): speed (rpm) with wobble and a live gauge for adjusting
the speed trimmer, sensor check (track 0, write protect, disk, index), head to track 0 /
last track / sweep, head cleaning with a cleaning disk, repeated read of one track
(dirty head vs. damaged disk, stops when a disk is falling apart), track layout listing
(fdrawcmd.sys only), warm-up test, step accuracy test, flux histogram and USB bandwidth
test (Greaseweazle only), and a note, last speed and last cleaning date per drive.
Rescue a bad disk ("Rescue a bad disk..."): retries every unreadable sector, saves the list
of missing sectors next to the image and, when run again with the same file in another drive,
reads only the missing ones; experimental bit voting from bad reads (fdrawcmd.sys only).
Rescue sectors from flux (Greaseweazle): open a saved .scp with "Open image..." and click
"Rescue sectors...". The program decodes the flux itself (no disk needed), takes every sector
with a valid checksum from any revolution, retries damaged tracks with other decoder settings
and combines flux files of the same disk from several drives (PC, Atari ST, MSX: MFM, 512 bytes).
At the end it says whether another drive can still help: weak sectors (data present, bad checksum),
unreadable sectors with signal, and places where the magnetic recording is gone (unrecoverable).
Hover a red or yellow cell in the sector map to see why the sector failed. A read history
tells a faulty drive from a faulty disk, and a write test gives the disk a grade (A/B/C).

The program detects the drive and the inserted disk (DD/HD, formatted or not) and
shows for every operation and every format whether it is possible and why not.

The disk map works like in FastCopy: columns are tracks, rows are sectors, side 0
on top, side 1 below; during an operation every sector is coloured
(green read, purple formatted, blue written, teal verified, yellow weak,
red unreadable, pink write failed) and a white frame shows the head position.

File system repair (FAT12: Atari, PC, MSX) only changes FAT and directory entries,
never file data, saves a backup first, writes only changed sectors and verifies
each. On disks with a damaged system area it refuses and recommends repairing an
image instead (repairs of images always go into a copy).

Add a language: Settings -> "Create a template for a new language", translate the
text after " = " and save it as xx.lang in the "lang" folder next to the program.

Status: tested against real tools on Linux (mtools, dosfstools, hatari hmsa,
hfsutils, e2fsprogs, cpmtools, amitools, the real Greaseweazle gw program with
simulated disks) and a simulated floppy controller. Feedback from real hardware
is very welcome.

Licence: GNU GPL v3 (licence/LICENSE-GPL-3.0.txt). Free software; the source code is freely available.
