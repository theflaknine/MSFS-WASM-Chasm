# WASM Chasm

Free, open-source Windows utility for Microsoft Flight Simulator 2024 on PC. It clears an aircraft's WASM data without the user hunting through AppData by package ID. C#, .NET Framework 4.8, WPF. MIT licence.

- `docs/SPEC.md`: the specification. Keep it in step with behaviour changes.
- `handoff.md`: current state and open items. Read it first when picking the work up.

## Commands

Run from the repo root.

```
dotnet build WasmChasm.sln
dotnet test tests/WasmChasm.Tests
dotnet run --project src/WasmChasm.App
dotnet run --project src/WasmChasm.Cli -- scan --store store
dotnet run --project src/WasmChasm.Cli -- purge <package-id> --store store --dry-run
powershell -ExecutionPolicy Bypass -File tools\MakeIcon.ps1 [preview.png]
```

- A running `WasmChasm.exe` locks its build output, so close the app before building the solution.
- CI builds `main`, `claude/**` and `v*` tags on `windows-latest`, and uploads the app as an artifact. The CLI isn't in it. `gh` is not installed on the dev machine; read CI status from `api.github.com/repos/theflaknine/MSFS-WASM-Chasm/actions/runs`.
- The CRT shader (`crt.ps`) is gitignored and compiled in CI, or by hand with `fxc` (see README). `fxc` is on the dev machine. Without `crt.ps`, local builds fall back to overlay effects and can hide shader bugs, so compile it when checking anything visual.
- The shader's area follows the bounds of everything inside `ScreenRoot`. Nothing in there may move past its edge, or the whole screen distorts. That's why the roll band moves a gradient inside a fixed rectangle.

## Layout

| Path | Contents |
|---|---|
| `src/WasmChasm.Core` | Detection, scanning, titles, path guard, disposal, sound synthesis, gauge scale. No UI references. |
| `src/WasmChasm.App` | WPF shell. Nearly all UI logic is in `MainWindow.xaml.cs`. |
| `src/WasmChasm.Cli` | Thin console wrapper over Core, for support diagnostics. |
| `tests/WasmChasm.Tests` | xUnit, on synthetic fixture trees (`Fixture.cs`). |
| `data/locations.json` | Every store-specific path, so a Sim Update move is a data fix. |
| `tools/MakeIcon.ps1` | Draws `src/WasmChasm.App/Assets/WasmChasm.ico`. |

Internal names still say "consign" (`ConsignmentService`, `ConsignTarget`) and "ledger" (`Ledger`, `OnLedgerPreviewKeyDown`). They predate the rename and users never see them. On-screen text must not use those words.

The modes are called Fun and Serious on screen, but `AppMode.Retro` and `AppMode.Plain` in code and in `settings.json`. On-screen text must not say Retro or Plain mode.

## How the data works

A package folder at `<WASM root>\MSFS2024\<package>\` (or `MSFS2020`) holds two different things:

- `m<hash>_<n>.{cache,dll,lib,natvis,obj,pdb}`: compiled by the sim. This is what the default scope removes.
- `work\`: the add-on's persistent data, which can include settings, navdata, account IDs and licence files. The SDK documents `\work` as persistent read/write storage, but not its location on disk.

Non-obvious facts learned from a real Store install:

- A WASM folder alone doesn't prove an install. A Store-only machine had one at the Steam path holding only `license.key` and `userinfo.txt`. An install needs `UserCfg.opt` too.
- `LocalState` can be a junction to another drive, so a path's drive may not be where the data lives. `WindowsRecycleBin.PhysicalDriveRoot` handles this.
- Being absent from the library doesn't mean an aircraft is uninstalled: add-on managers keep aircraft elsewhere and link them in. That's why there is no orphan check.
- Guides describing `Packages\<package>\work\wasm` don't match what was found, including the FlyAway page cited in the spec.

## Safety rules

These protect users' real data, including licence files. Don't weaken them.

- Every target goes through `PathGuard`, in preflight and again just before the write. Files inside a target are guarded individually.
- Never follow reparse points; remove the link, not its target.
- Wrap paths in `PathEx.Extended` at every file system call. .NET Framework fails on ordinary paths past 260 characters unless the PC opted in, and this dev machine has opted in, so tests alone can miss it. A program without the `longPathAware` manifest, such as the CLI, shows the real behaviour. The path guard, logs and saved values must use the ordinary form.
- The sim is checked before the run and before every item.
- If the Recycle Bin can't be used, ask. Never silently delete permanently.
- The compiled-cache pattern matches only real files directly in the package folder, and an invalid pattern matches nothing.
- In Fun mode, a purge may skip the results page only if `ScopeText.NeedsResultsPage` allows it. Failures must always be shown.

## Working on the user's machine

The dev machine has a real MSFS 2024 Store install with the user's own add-ons.

- Treat its WASM folders as live data: read-only or dry-run unless the user explicitly says otherwise. Don't read the contents of licence or userinfo files.
- Dry run can be off in the saved settings (`%LOCALAPPDATA%\WasmChasm\settings.json`). Check it before sending Enter to the app. A second Enter on the confirm page is a real purge if dry run is off.
- For real purges, use a fake install. Set `LOCALAPPDATA` and `APPDATA` to a scratch tree before launching: store paths come from environment variables, while settings and the log stay in the real profile because they use the known-folder API. Confirm from the log that the app is on the fake install before pressing anything.

## Checking the UI without a human

- `PrintWindow` with flag 2 captures the window without moving it.
- `PostMessage` with `WM_KEYDOWN` drives the app. It works best after `SetForegroundWindow`.
- UI Automation's `FocusedElement.Current.Name` reports the row and whether it's marked.
- The main list ignores keys during a scan (except F4), and a scan can take seconds. Wait for a new `SCAN` line in the log before sending keys, or they vanish.
- Sound cannot be checked this way. Say so, and let the user judge it.

## User preferences

- **Wording:** plain English that non-native speakers can follow. The action word is "purge". Avoid terms like "consign", "records", "released", "reclaimed", "queued", "ledger" and "clear" (it reads as clearing a selection).
- **Clutter:** keep screens minimal. No package column, no library or orphan check, no empty "nothing to remove" rows. Untitled aircraft show their raw package ID, untidied.
- **Sound:** gentle and never startling. Harsh beeps, CRT static and sudden thumps were all rejected.
- **Modes:** Fun is the theatre; Serious has none of it (one font size, no clock, chasm, animations or sound). The user renamed them from Retro and Plain because both look retro. What happens to files must be identical in both.
- **Single word over two:** the same action keeps the same name in both modes.
- **Folder browsing:** the app doesn't offer it, because the WASM folder is always in a known place. The CLI's `--root` stays for support and tests. The backup folder is a pasted or typed path, by the user's choice, not a picker.

## Git

Work directly on `main`; the user prefers no long-lived branches. Commit and push only when asked.
