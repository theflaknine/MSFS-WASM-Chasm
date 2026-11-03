# WASM Chasm

> **Not affiliated with Microsoft, Asobo Studio or any aircraft developer.**
>
> **Warning: clearing an aircraft's WASM `work` folder erases that aircraft's own settings and saved
> state, and anything add-on tools stored there — for example installed navdata, a SimBrief alias or a
> Hoppie ID.** You may need to re-enter or reinstall them. By default WASM Chasm moves folders to the
> Recycle Bin so you can restore them.

WASM Chasm is a free, open-source Windows utility for **Microsoft Flight Simulator 2024** on PC (Steam or
Microsoft Store / PC Game Pass). Pick one or more aircraft and remove their WASM data in a few keystrokes,
instead of hunting through AppData by package ID.

It looks like an anonymous late-1970s office terminal: amber phosphor, scanlines, and a chasm on the right
that the aircraft you consign fall into — one row at a time, as each folder actually completes.

No installer. No telemetry. No network access. No admin rights.

## Where WASM data lives

| Install | WASM root | Package data |
| --- | --- | --- |
| Steam | `%APPDATA%\Microsoft Flight Simulator 2024\WASM` | `MSFS2024\<package>\work` |
| Microsoft Store / PC Game Pass | `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalState\WASM` | `MSFS2024\<package>\work` |

2020-format aircraft use an `MSFS2020` sibling folder. Other siblings (for example `MSFS2024_now_valid`)
are shown under their own name but are **never consigned automatically** — you have to include them
explicitly on the confirm screen.

All of these paths live in [`data/locations.json`](data/locations.json). If a Sim Update moves them, edit
that file (a copy placed beside `WasmChasm.exe` overrides the built-in one).

## Using it

| Key | Action |
| --- | --- |
| ↑ ↓ | Move the cursor |
| Space / click | Mark or unmark an aircraft |
| Ctrl+A / Ctrl+D | Mark all shown / clear marks |
| Ctrl+O | Mark every orphaned aircraft (no longer installed) |
| `/` or Ctrl+F | Filter by title or package ID |
| F6 / Shift+F6 | Sort by name, size or date / reverse |
| Enter | Consign marked aircraft (a confirm screen always follows) |
| Esc | Cancel / clear marks / stop after the current folder |
| F1 | Help |
| F2 | Switch install (Steam / Store) or browse for a WASM folder |
| F3 | Settings |
| F5 | Rescan |

**Settings (F3):** CRT effects on/off, sound on/off (off by default), disposal method (Recycle Bin —
default; zip backup then Recycle Bin; permanent delete), backup folder, scope (work folder only — default;
or whole package folder) and dry run.

More than five aircraft at once requires typing `Y` to confirm.

### Safety

- Every target path is canonicalised and must sit inside the detected WASM root (at least two levels
  down), and no ancestor between the root and the target may be a link. Anything else is refused.
- Symlinks and junctions are never followed: if a `work` folder is a link (Addons Linker and similar),
  only the link is removed and the result says so.
- The sim (`FlightSimulator2024.exe`) is checked before the run and before **every** folder. If it starts
  mid-run, the rest are skipped.
- A locked or access-denied folder is skipped and reported; there are no retries and no elevation.
- If the Recycle Bin is disabled or too small, you're asked: zip a backup first and then delete, or cancel.
  Nothing is ever silently deleted permanently.
- Each session is appended to `%LOCALAPPDATA%\WasmChasm\wasmchasm.log`. Settings are kept beside it in
  `settings.json`.

### Command line

`wasmchasm-cli.exe` is a thin wrapper over the same core, for support diagnostics:

```
wasmchasm stores
wasmchasm scan [--store steam|store] [--root <WASM folder>] [--json]
wasmchasm consign <package-id>... [--dry-run] [--scope work|package] [--include-unknown]
                  [--mode recycle|zip|permanent] [--backup <folder>] [--yes]
```

`consign` is always a dry run unless `--yes` is given.

## Building

Requires the .NET 8 SDK.

```
dotnet test tests/WasmChasm.Tests
dotnet publish src/WasmChasm.App -c Release -r win-x64 -o artifacts/app
```

The WPF app only runs on Windows; Core, CLI and tests build and run anywhere.

The curvature/scanline pixel shader (`src/WasmChasm.App/Shaders/crt.fx`) is compiled with `fxc.exe` from
the Windows SDK:

```
fxc /nologo /T ps_2_0 /E main /Fo src\WasmChasm.App\Shaders\crt.ps src\WasmChasm.App\Shaders\crt.fx
```

CI does this automatically. Without `crt.ps` the app falls back to overlay scanlines and vignette (no
curvature).

## Repository layout

```
src/WasmChasm.Core/      install detection, scanning, name resolution, path guard, disposal (no UI)
src/WasmChasm.App/       WPF shell, CRT renderer, ledger and chasm views, settings
src/WasmChasm.Cli/       console wrapper: scan, consign --dry-run
tests/WasmChasm.Tests/   xUnit against synthetic fixture trees
data/locations.json      every store-specific path
docs/SPEC.md             the specification
```

## Status

Milestone **M0 (path verification with ProcMon on real Steam and Store installs) has not been done yet.**
The paths in `locations.json` come from public guides and are marked `"verified": false`. In particular:
whether a `Packages\<package>\work\wasm` layout exists, and what `MSFS2024_now_valid` is for, are still
open questions. Until M0 is done, use dry run first (F3 → Dry run, or `wasmchasm consign … --dry-run`)
and check the listed folders.

## Licence

MIT — see [LICENSE](LICENSE). The bundled VT323 font is © The VT323 Project Authors, SIL Open Font
Licence 1.1 ([OFL.txt](src/WasmChasm.App/Fonts/OFL.txt)).
