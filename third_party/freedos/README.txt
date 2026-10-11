FreeDOS boot sector (FAT12) — boot.asm from https://github.com/FDOS/kernel (boot/boot.asm),
GNU GPL v2 or later (see COPYING). Assembled with:  nasm -dISFAT12 -fbin boot.asm -o freedos-fat12.bin
The binary is embedded in Diskette Toolkit as src/Core/Boot/freedos-fat12.bin and written to boot sectors of
FreeDOS boot disks (the BPB area 0x03-0x3D is replaced by the disk's own values).
The FreeDOS kernel (KERNEL.SYS) and FreeCOM (COMMAND.COM) are NOT bundled; Diskette Toolkit downloads the
official packages kernel.zip and freecom.zip from the FreeDOS 1.3 repository at ibiblio.org when needed.
