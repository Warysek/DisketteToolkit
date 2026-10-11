# Diskette Toolkit — notes for Claude

Windows tool for reading, writing, formatting and testing floppies and disk images (Atari ST first, plus PC, MSX,
Mac, Amiga, PCW, MINIX, ext2). Formerly "AtariFloppy" — namespaces, Core/Tests assembly names and resource
LogicalNames still use `AtariFloppy.*` on purpose; don't rename them.

## Owner
Wary (HoroPC) does not program. He decides WHAT, Claude writes all code. Talk to him in Czech, about behaviour he
can see in the program, with exact click paths. He tests on real hardware: Sony USB-FDU (USB drive), an old PC with
an internal drive + fdrawcmd.sys, and a Greaseweazle.

## Layout
- `src/Core` — all logic, compiled into both exes (drives, formats, file systems, boot disks, i18n, tool setup)
  - `Drives/` Windows (USB/internal), `Fdraw/` (fdrawcmd.sys IOCTLs), `Gw/` (runs gw.exe and parses its output)
  - `App/DeviceFactory.cs` picks Windows / fdrawcmd / Greaseweazle; `App/Repair.cs` repair planning and backups
  - `Analysis/TrackActivity.cs` + `SectorMapModel.cs` feed the live sector map
  - `I18n/en.lang` — English translations; key = the Czech text (`cz = en`, ` \= ` escapes " = ")
- `src/Gui` — WinForms window `DisketteToolkit.exe` (`MainForm`, `Jobs.cs` = all operations, `Dialogs/`)
- `src/Cli` — command line `dtk.exe` (Czech strings, not translated yet)
- `tests/Tests.cs` — own test runner (methods `T_*` run by reflection), simulated devices, `tests/data`
- `third_party/freedos` — GPL boot sector source; `tools/i18n_*.py` — translation helpers
- `dist/` — release ZIPs (gitignored)

## Build & check (Linux sandbox)
- `dotnet build src/Gui/Gui.csproj -c Release` (also `src/Cli/Cli.csproj`, `tests/Tests.csproj`); Mono reference
  assemblies via `FrameworkPathOverride` in `Directory.Build.props`
- Tests: `mono tests/bin/Release/net48/AtariFloppy.Tests.exe` — all must pass
- Translations: `python3 tools/i18n_check.py src/Core/I18n/en.lang src/Core src/Gui` — must report 0 missing.
  Every new `Tr.T`/`Tr.F` text needs a line in `en.lang`.
- GUI screenshots: Xvfb + `mono DisketteToolkit.exe --demo --screenshot <scenario>` (scenarios only run in demo mode)
- Version: only `<Version>` in `Directory.Build.props` (read via `AppPaths.Version`)
- Real floppy hardware cannot be tested here — always say what was verified in simulation and what Wary must test.

## Licence
GPL-3.0 (`LICENSE`). The embedded FreeDOS boot sector is GPL-2.0-or-later. FreeDOS kernel, gw.exe and fdrawcmd.sys
are downloaded at runtime, never bundled. MS-DOS comes only from the user's own `diskcopy.dll`.

## Coding guidelines
(after Andrej Karpathy's observations on LLM coding pitfalls — github.com/multica-ai/andrej-karpathy-skills, MIT)
Caution over speed; for trivial changes use judgment.

1. **Think before coding.** State assumptions. If a request can be read two ways, name both and pick one with a
   reason, or ask when a wrong guess is costly. Say what is unclear instead of guessing. Propose simpler approaches.
2. **Simplicity first.** Minimum code that solves the request. No speculative features, unrequested settings,
   single-use abstractions, or error handling for impossible cases.
3. **Surgical changes.** Touch only what the task needs; match existing style; don't refactor working neighbours.
   Remove only what your change made unused; mention other dead code instead of deleting it.
4. **Goal-driven execution.** Turn tasks into verifiable goals (reproduce a bug with a test, then fix it). For
   multi-step work, plan with a check per step and loop until checks pass. Report honestly what was verified.
