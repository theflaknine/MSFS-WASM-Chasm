# WASM Chasm — Specification

Nov 3, 2026

## Overview

WASM Chasm is a free, open-source Windows utility that lets an MSFS 2024 PC user pick one or more aircraft and remove their WASM data in a few clicks, instead of hunting through AppData by package ID.

**Problem.** Support teams routinely ask users to clear an aircraft's WASM work folder to fix crashes or corrupt state. The folders live in different places for Steam and Microsoft Store installs, are named by package ID rather than aircraft name, and sit next to data for every other aircraft. Users delete the wrong thing, or give up.

**Audience.** PC simmers on MSFS 2024 (Steam or Microsoft Store / PC Game Pass) who have been told to reset an aircraft, or want to reclaim space from aircraft they no longer fly.

**Distribution.** Public GitHub repository under `theflaknine`, MIT licence, releases as a signed or unsigned single-file `.exe` via GitHub Releases. No installer, no telemetry, no network access.

**Repository.** [github.com/theflaknine/MSFS-WASM-Chasm](https://github.com/theflaknine/MSFS-WASM-Chasm) (public).

**Non-goals for v1**

- MSFS 2020 installs (2020-format aircraft running inside 2024 are in scope)
- Clearing other caches (rolling cache, SceneryIndexes, StreamedPackages)
- Uninstalling aircraft packages themselves
- Selective deletion of individual files inside a work folder
- Xbox or PS5 (no file access)

## Background: where WASM data lives

Each WASM aircraft gets a per-package folder under the sim's `WASM` directory, holding a `work` subfolder of persistent state (settings, saved states, navdata, sometimes licence files) and, beside it, the files the sim generates when it compiles the aircraft's modules (`m<hash>_<n>.{cache,dll,lib,natvis,obj,pdb}`, observed on one Store install). The SDK documents `\work` as an add-on's persistent read/write storage (SDK docs: Programming APIs > WASM > WebAssembly > File Access) but not where it sits on disk. The root differs by store; the package folder sits under a sim-version subfolder ([MSFS Addons](https://msfsaddons.com/msfs-2024-faq/wasm-crash/)).

| Install | WASM root | Package data |
| --- | --- | --- |
| Steam | `%APPDATA%\Microsoft Flight Simulator 2024\WASM` | `MSFS2024\<package>\work` |
| Microsoft Store / PC Game Pass | `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalState\WASM` | `MSFS2024\<package>\work` |

Aircraft built for MSFS 2020 use an `MSFS2020` sibling folder instead of `MSFS2024`.

**Known irregularities the scanner must handle**

- A `MSFS2024_now_valid` sibling folder has been observed alongside `MSFS2024` on Store installs, holding the same package names ([Navigraph forum](https://forum.navigraph.com/t/msfs-2024-pmdg-777-300er-hub-alternates-extraction-path-causes-wasm-crash-and-77w-ident-ends-up-blank/23027)). Its role is undocumented.
- Some guides describe a `Packages\<package>\work\wasm` cache instead ([FlyAway Simulation](https://flyawaysimulation.com/ask/answers/msfs-2024-wasm-folder/)). This conflicts with the above. On the one Store install inspected, no package under the WASM root had `work\wasm`, and `LocalState\packages` held only 2 packages, each with `work` but no `wasm`. Confirm in M0.
- Both Steam and Store roots can exist on one machine, including when only one is installed: a Store-only machine had a WASM folder at the Steam path holding only `license.key` and `userinfo.txt` files, with no `UserCfg.opt` beside it. What creates it is unknown.
- `LocalState` can be a junction to another drive (a Store install moved off C:), so the WASM root's real drive may differ from the one in its path.

**What users lose.** Clearing a work folder wipes the aircraft's settings, and third-party data placed there, which can include licence files. One reported case lost installed navdata plus the user's SimBrief alias and Hoppie ID ([Navigraph forum](https://forum.navigraph.com/t/msfs-2024-pmdg-777-300er-hub-alternates-extraction-path-causes-wasm-crash-and-77w-ident-ends-up-blank/23027)). The UI must say this plainly before deletion.

**Friendly names.** Folder names are package IDs (`pmdg-aircraft-77w`). Titles come from each package's `manifest.json`, found under the `InstalledPackagesPath` recorded in `UserCfg.opt`: `%APPDATA%\Microsoft Flight Simulator 2024\UserCfg.opt` (Steam) or `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt` (Store) ([FlyAway Simulation](https://flyawaysimulation.com/ask/answers/msfs-2024-configuration-log-file-locations/)).

## Functional requirements

**Discovery**

- FR-1: On launch, detect Steam and Store installs. An install needs both its WASM root and its `UserCfg.opt`; a WASM root alone is not an install and is reachable only by browsing to it. Show which were found; if both, the user picks one (remembered).
- FR-2: Enumerate every package folder under each sim-version subfolder (`MSFS2024`, `MSFS2020`, any other sibling). Each entry records package ID, sim-version folder, size on disk, last-modified date.
- FR-3: Resolve a display title from the package's `manifest.json` (Community, Official and StreamedPackages). Entries with no readable title show the package ID. The app does not judge whether a package is still installed: add-on managers link and unlink packages, so that can't be known reliably.
- FR-4: Entries with the same package ID across sibling folders are grouped as one aircraft, with sub-rows.

**Selection**

- FR-5: Single or multi-select by click or keyboard. Filter by text; sort by name, size, date.
- FR-6: A running total shows how much data the selection holds.

**Deletion ("into the chasm")**

- FR-7: Before acting, block if `FlightSimulator2024.exe` is running.
- FR-8: A confirm step lists every folder to be removed or, for the compiled cache, the folders whose compiled files will go, with the warning for that scope. No silent deletes.
- FR-9: Default action moves folders and files to the Windows Recycle Bin. Optional setting: zip a backup to a user-chosen folder first. Permanent delete is off by default.
- FR-10: Three scopes. **Compiled cache only** (default) removes only the sim-generated compiled files and keeps `work`. **Work folder only** removes the `work` subfolder, for when a developer's support team asks for it. **Whole package folder** removes both. The compiled-cache default assumes the sim rebuilds those files; this has not been tested (see M0).
- FR-11: A results screen lists each folder as removed, skipped or failed, with the reason.

**Safety**

- FR-12: Every target path is canonicalised and must sit inside a detected WASM root. Anything else is refused.
- FR-13: Never follow reparse points (symlinks, junctions); remove the link, not its target.
- FR-14: No admin rights required. No network access. No changes outside the WASM roots and the backup folder.
- FR-15: Dry-run mode shows what would be removed and touches nothing.
- FR-16: Append each session to a plain-text log in `%LOCALAPPDATA%\WasmChasm\`.

## Visual and interaction design

The app looks like an anonymous late-1970s office terminal: one phosphor colour on black, chunky type, a blinking cursor, and procedural language that treats a routine chore as solemn clerical work. The look is original; it borrows the era, not any specific show or product.

**Screen and palette**

- Fixed 4:3 "monitor" panel centred in the window, with a thick rounded bezel and slight barrel curvature. Window resizes letterbox around it.
- Single amber phosphor (`#FFB000`) on near-black (`#0E0B07`), with a dim amber (`#7A5400`) for secondary text. No other hues, including for warnings: emphasis comes from inverse video and blinking.
- Subtle scanlines, faint glow on lit pixels, a slow vertical roll band. All CRT effects toggle off in settings (accessibility, and low-end GPUs).

**Type and copy**

- One monospace bitmap-style face (candidate: an OFL-licensed terminal font, bundled). Uppercase headings, sentence-case body.
- Copy is dry and procedural, in plain words that non-native English speakers can follow: "SELECT AIRCRAFT TO CLEAR", "DONE. 3 AIRCRAFT CLEARED. 412 MB FREED." Never jokey in the data-loss warning.

**Layout**

- Header bar: app name, detected install (STEAM or STORE), session clock.
- Left two-thirds: the aircraft list, one row per aircraft (title, size, date), keyboard navigable, selected rows in inverse video.
- Right third: the chasm, a narrow vertical pit drawn in line art receding into darkness, with a depth gauge showing the MB marked.
- Footer: function-key legend (F1 help, F5 rescan, SPACE select, ENTER clear, ESC cancel).

**The clearing moment**

1. User selects rows; each gets a marker and the depth gauge climbs.
2. ENTER opens a full-screen confirm sheet: the folder list, the warning, and a typed confirmation ("Y") for more than five aircraft.
3. On confirm, each row detaches from the list, slides right, and drops into the pit, breaking into falling characters. Rows fall one at a time as each folder actually completes, so the animation reports real progress.
4. Results screen in the same style, with any failures in inverse video.

**Sound (optional, off by default)**: relay click on select, low hum while clearing, single beep on completion.

**Accessibility**: full keyboard operation, CRT effects off switch, Windows high-contrast respected, screen-reader names on every control.

## Architecture and tech stack

C# on .NET 8, WPF front end, published as a self-contained single-file x64 `.exe`. WPF handles the custom CRT rendering (pixel shader effect for scanlines and curvature) without a browser runtime.

**Projects**

| Project | Responsibility |
| --- | --- |
| `WasmChasm.Core` | Install detection, scanning, name resolution, path guard, deletion. No UI references. |
| `WasmChasm.App` | WPF shell, CRT renderer, ledger and chasm views, settings. |
| `WasmChasm.Cli` | Thin console wrapper over Core: `scan`, `clear --dry-run`. Used for M1 and support diagnostics. |
| `WasmChasm.Tests` | xUnit, against fixture directory trees. |

**Key interfaces in Core**

- `IInstallLocator` — one implementation per store; returns WASM root and `UserCfg.opt` path, or nothing.
- `IWasmScanner` — walks a root, returns `WasmEntry` records.
- `IManifestResolver` — package ID to display title.
- `IPathGuard` — canonicalises and validates every target before any write.
- `IDisposalStrategy` — `RecycleBin`, `ZipThenRecycle`, `Permanent`.

All store-specific paths live in a single `locations.json` shipped beside the code, so a path change after a Sim Update is a data fix, not a code change.

**Flow**: locate install → scan → resolve names → user selects → pre-flight (sim not running, path guard) → dispose per folder, emitting progress events the UI animates → write log → results.

## Edge cases, errors and testing

| Case | Behaviour |
| --- | --- |
| No install found | Explain both expected roots; offer "Browse for WASM folder" with the path guard anchored to the chosen folder. |
| Both stores installed | Picker on first run; switchable from header. |
| Sim launches while clearing | Re-check before each folder; stop, report the rest as skipped. |
| File locked or access denied | Skip that folder, report it, continue. No retries with elevation. |
| Recycle Bin disabled or over quota | Fall back to asking: zip backup first, or cancel. Never silently permanent-delete. |
| Work folder is a junction (Addons Linker and similar) | Remove the link only, flag it in results. |
| Path longer than 260 chars | Use long-path APIs; manifest enables `longPathAware`. |
| Unknown sibling folder (e.g. `MSFS2024_now_valid`) | Scanned and shown under its folder name; never auto-deleted. |

**Testing**

- Unit tests in Core against synthetic directory fixtures for each layout above, including junctions and long paths.
- Path guard fuzzed with traversal strings (`..`, mixed separators, UNC, 8.3 short names).
- Manual test matrix: Steam only, Store only, both; Windows 10 22H2 and Windows 11 24H2; at least one 2020-format and one 2024-native WASM aircraft.

## Milestones, repo and open questions

**Milestones and exit gates**

1. **M0 — Path verification spike.** ProcMon on Steam and Store installs while loading a 2024-native and a 2020-format WASM aircraft. Exit: confirmed `locations.json`; the `Packages\<package>\work\wasm` and `MSFS2024_now_valid` questions answered; confirmed that the sim rebuilds removed compiled files cleanly.
2. **M1 — Core + CLI.** `scan` and `clear --dry-run` work on both stores. Exit: tests green; output matches manual inspection on two machines.
3. **M2 — WPF app.** Ledger, chasm, confirm, Recycle Bin disposal, results, CRT renderer. Exit: full manual matrix passes.
4. **M3 — Closed test.** 5–10 testers across both stores. Exit: no data outside WASM roots touched; no open severity-1 issues.
5. **M4 — Public v1.0.** README with screenshots, release binary, checksum.

**Repository layout**

```
MSFS-WASM-Chasm/
  src/WasmChasm.Core/
  src/WasmChasm.App/
  src/WasmChasm.Cli/
  tests/WasmChasm.Tests/
  data/locations.json
  docs/SPEC.md
  .github/workflows/build.yml
  README.md
  LICENSE
```

README carries a not-affiliated-with-Microsoft-or-Asobo notice and the data-loss warning near the top.

**Open questions**

- [x] Scope: resolved by FR-10. Compiled cache is the default; whole-folder does clear compiled output.
- [x] One-click "consign all orphans": dropped, along with the library check. Being absent from the library doesn't prove an aircraft is gone: one Store install showed 22 of 43 packages that way, including aircraft the owner still has.
- [ ] Code signing: pay for a certificate, or accept SmartScreen warnings for a free tool?
- [ ] Font choice and its licence.
- [ ] Phosphor colour: amber, or offer green as an alternate theme?
- [ ] Support MSFS 2020 installs in a later version?
