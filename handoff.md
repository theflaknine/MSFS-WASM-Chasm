# Handoff

State as of 2026-11-03. Read `CLAUDE.md` first for how the project works. Check this file against the code, since it will go stale.

## Where things stand

v0.1 is on `main` and builds. It moved from .NET 8 to .NET Framework 4.8 to shrink the download from 92.5 MB to 0.7 MB. All 190 tests pass locally, plus one opt-in test against the real Recycle Bin (`WASMCHASM_RECYCLE_TEST`). There is no release or tag yet.

**Built so far:**

- **Install detection:** needs both the WASM root and `UserCfg.opt`.
- **Three scopes:** compiled cache only (default), work folder, whole package.
- **Disposal:** Recycle Bin (default), zip then Recycle Bin, or permanent. Dry run is available for all of them and plays the full animation.
- **Fun and Serious modes:** asked on first run, F4 switches, and the mode shows in the header. In code and `settings.json` they are still `Retro` and `Plain`.
- **Fun mode theatre:** splash logo, power-on effect, CRT effects, the chasm with a breathing title, rows falling in.
- **Sounds, on by the Sound setting in Fun mode only:** hum, gradual power-up, blip on row change, ratchet on mark, crumble on fall, chime when done.
- **Gauge:** linear, where a full bar means all purgeable data. The ratchet's pitch follows the number of marked aircraft instead, one semitone each up to 30.
- **Keys:** arrows move, → marks, ← unmarks, Space toggles, Enter purges after a confirm.
- **Results page:** Fun mode skips it after a clean purge. See `ScopeText.NeedsResultsPage`.
- **App icon:** in the title bar and both exes.

## Before any release

1. **Test on a real Steam install.** The dev machine can't. If `UserCfg.opt` isn't at the expected path, the install won't be detected, and with browsing removed the only fix is a corrected `locations.json`.
2. **Check a CI build by eye.** The move to 4.8 hasn't been built by CI yet. A local build with the real shader looked the same in captures, and the user tried it on a fake install. Try it on a second PC too, ideally with Windows' long-path setting off.
3. **Finish M0.** ProcMon on Steam and Store installs. `locations.json` is still marked `"verified": false`, and `MSFS2024_now_valid` remains unexplained because it doesn't exist on this machine.

4. **Add third-party notices.** The exe embeds NAudio and System.Text.Json and its dependencies, all MIT. Their licence texts should ship with the release.

## Not confirmed

- **Whether purging the cache fixes reported crashes:** the user confirms from their own knowledge that the sim rebuilds purged compiled files, so the default scope is sound. Whether a rebuild cures the problems people report, compared with clearing `work`, hasn't been tested.
- **Sounds:** Claude can't hear them. The user has reacted to the static, power-up and beep, but hasn't confirmed the latest hum, ratchet by count, or crumble. The hum sits around 55–220 Hz, so it may be faint on laptop speakers.
- **Alt+F4:** the code leaves it to close the window, but it couldn't be simulated. Try it once.
- **White flash at launch:** one capture taken as the window opened was pure white. It may be a capture artifact; the user hasn't said.
- **The source of the Steam-path WASM folder:** it holds only licence files on a Store-only machine. The guess is add-on installers, but that's unconfirmed.

## Small known issues

None of these have been fixed; the user hasn't decided on them.

- **Partial purges:** a locked file stops an aircraft mid-way, so some of its compiled files may already be in the Recycle Bin when it's reported as `FAILED`. Nothing is lost, since the sim rebuilds them. Checking for locks first would make it all-or-nothing.
- **Failure wording:** the results page says `DONE. 0 AIRCRAFT PURGED.` under a `DONE` heading even when everything failed.
- **Long dry runs:** marking all 43 aircraft means a long animation, and Esc can't stop it because the work has already finished. It could speed up or skip.
- **Esc moves the cursor:** clearing marks with Esc sends the cursor back to the top row.
- **Serious mode:** text is still in capitals and the row cursor still blinks. Sentence case would be a bigger change.
- **Footer:** it says `SPACE: SELECT` and doesn't mention the arrow keys, to avoid crowding it.
- **Rows after a purge:** a purged aircraft stays in the list at `0 B`, because its `work` folder remains.

## Open questions in the spec

- Code signing: pay for a certificate, or accept SmartScreen warnings?
- Green as an alternate phosphor colour?
- Support MSFS 2020 installs later?

The font choice (VT323) is in use, though that question is still listed as open in the spec.

## State of the dev machine

- The saved settings have **Dry run off**, Sound on and Fun mode (`Retro`), so a confirmed purge in the app is real. The first-run question has been answered, so it won't show again unless `ModeChosen` is cleared.
- `crt.ps` is compiled in the working tree, so local builds currently use the real shader. It's gitignored.
- The Recycle Bin may hold small test files named `m78edaad72d8f5958_0.*`, `m0123456789abcdef_0.*` and `mabcdef00000000*_0.*`, which are safe to empty. None came from real add-ons.
- The session log at `%LOCALAPPDATA%\WasmChasm\wasmchasm.log` contains some lines from fake-install test runs.
- No real aircraft data has been purged.
