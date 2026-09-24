# Releasing to food4rhino / public source

Checklist for the first (and every) public release. Prepared 2026-07-17;
items marked **BLOCKER** must be resolved before anything is uploaded.

## Blockers before first publish

1. ~~Sanitize the embedded `.3dm` resources.~~ **RESOLVED 2026-07-17,
   RE-OPENED AND RE-RESOLVED 2026-09-01 (AUD-REL-005).** The 2026-07-17
   rhino3dm rebuild itself stamped the maintainer's Windows username into both
   files' revision history (`created by` / `last edited by`, UTF-16LE —
   invisible to ASCII-only `strings` scans, which is why the 2026-08-20
   re-verification passed). `tools/sanitize_3dm_revision.py` neutralized the
   fields in place with a CRC-correct equal-length patch, validated by
   rhino3dm (identical geometry, byte length unchanged so the
   `CalibratedAssemblyBytes` pin holds). **Every future PII scan must decode
   UTF-16 in addition to ASCII** — `tools/release.ps1` does. Original text:
   Both
   resources were rebuilt with rhino3dm (geometry copied exactly — per-part
   bounding boxes and mesh counts verified identical; the SCARA block
   instances were flattened to world-coordinate breps). The old files leaked
   user-profile paths in RDK XML and the saved document path; the
   rebuilt files and the Release `.gha` scan clean. `CalibratedAssemblyBytes`
   was re-pinned. Release builds also set `<PathMap>` so the PE debug
   directory no longer embeds the local PDB path (Debug builds still do —
   ship Release only). **Re-verified 2026-08-20** against the current Release
   build: `strings` over `Lugworm.gha`, `Lugworm.Core.dll`,
   `robot.xml` and `Robot_Assembly.3dm` returned no
   user-profile path, no username, and no project path. Residual (optional): open
   the `.3dm` file once in Rhino and confirm the SCARA arm renders —
   the automated check verified geometry, not Rhino's meshing of it.

2. ~~Confirm `Robot_Assembly.3dm` provenance.~~ **RESOLVED 2026-07-17.**
   Own IDAC model (author-confirmed). Note it is an approximate model of the
   Potterbot Scara v4, not vendor-verified geometry.

3. **Fresh public repository.** Git history still contains everything that
   was untracked on 2026-07-17 (draft-paper extracts,
   internal audit records, send bundles). Publish source via `git archive`
   / clean export into a new repo — never push this repo's history and never
   copy the working directory (ignored files would come along).
   **Code side closed 2026-09-01:** `tools/release.ps1` produces
   `dist/source-Lugworm-<version>.zip` from committed content only,
   restricted to a public path allowlist (projects, tests, third_party, tools,
   public docs). Creating and pushing the new public repository from that
   export remains a maintainer action.
   **Repository model since 2026-09-24:** the public repository carries the code
   itself, imported from that export by `tools/publish-source.ps1` as one commit
   per release; the ZIP is never uploaded. See "Publishing to GitHub" below.

   The independent `docs/github/` checkout is deliberately not part of this
   archive and must never be copied as a nested repository. The exporter now
   fails with a clear message if any required public path is not committed.
   Commit or remove every required path deliberately before producing a public
   source archive.

   **Do this check first, before any export:** confirm `.claude/` is ignored.
   It holds Codex/Claude skill state — thread IDs, review text, and event
   logs containing verbatim source excerpts and lab notes — and it sat
   untracked but *un-ignored* in the repo root until 2026-08-20, where a
   single `git add -A` would have committed ~2.9 MB of it. The ignore rule
   is now in `.gitignore`; verify with `git check-ignore -v .claude` before
   trusting it.

4. ~~Pseudonymize or keep the machine serial (decide).~~ **RESOLVED
   2026-08-20 — decision: KEEP the real serial `SCV4-2373`.** It appears in
   `robot.xml`, `duet-control.md`, and test fixtures (it was also in
   `ScaraFit.cs` and `CalibrationReport.cs` until the SCARA calibration pair
   was removed). It is a machine asset tag, not a credential, and keeping it
   preserves the traceability of the calibrated `L1`/`L2`/`M669` values to the
   specific machine they were measured on — which is what makes the shipped
   `robot.xml` defensible as provenance rather than arbitrary numbers. This
   was a deliberate, informed choice; do not re-open it as an oversight.

5. ~~Resolve the `HomePose calibrated="true"` contradiction.~~ **RESOLVED
   2026-07-17.** The post-homeall pose was measured on the machine
   (X200 Y-500 Z400) and entered in `robot.xml`; docs updated to match.
   Re-measure after any `homeall.g` change.

6. ~~Confirm copyright authority.~~ **RESOLVED 2026-09-01.** The maintainer
   confirmed TUHH/IDAC authority to release the employee-created and
   legacy-derived (clay_core_v2 and earlier) work under MIT.

## Yak package hygiene

**Since 2.6.0 the packaging below is automated: run `tools/release.ps1`**
(candidate mode by default). Public mode additionally requires the real HTTPS
Food4Rhino/project URL and explicit owner-gate confirmation:
`-Mode Public -ReleaseUrl https://... -OwnerGatesConfirmed`. No public URL is configured by
candidate mode. Both modes build the `.yak` from the scanned stage
(`yak build --platform win`) into `dist/` and put it inside
`dist/send-Lugworm-<version>.zip` next to the raw `net48/` + `net7.0/`
payload and `INSTALL.md` + `UPGRADE.md` (added to the zip, not to the stage, so
they stay out of the `.yak`); candidate mode only warns when Yak.exe is missing,
public mode fails. The csproj no longer runs Yak on ordinary builds — the raw-output
package was a development artifact and needed `-p:BuildYakPackage=true` to be
produced at all. The manual checklist stays below as the specification the
script implements: package from a **clean staging folder**
(allowlist, don't blocklist):

The executable allowlist in `tools/release.ps1` is the package specification:
each `net48/` and `net7.0/` payload includes the plugin, Core, Clipper2,
MathNet, SkiaSharp and OpenTK managed assemblies. The x64 Skia native is
`net48/x64/libSkiaSharp.dll` or `net7.0/libSkiaSharp.dll` respectively.
`net7.0-windows/` is a build/test target and is not copied into the package.

- Only the intended framework payloads (`net48/` + `net7.0/`) are shipped;
  `net7.0-windows/` is a build/test target. The script's allowlist above is
  authoritative for managed and native runtime files. **No PDBs** (path
  leakage), no `doe_plan.*` strays, no build droppings.
- Include `LICENSE` and `THIRD-PARTY-NOTICES.md` inside the package.
- `package-icon.png` is required; candidate packaging fails if it is missing.
- The 24 px assembly icons are a separate, verified set. Run
  `Lugworm/Resources/icons-src/Lugworm/make_icons.ps1 -VerifyOnly` before packaging:
  the manifest must hold **exactly 25** names, each with a 24×24 `Format32bppArgb` PNG.
  4.0.0 added `IamLegend` and `IamSettings` and removed both UR icons; the Version icon
  went with the component on 2026-09-21 and Graph Mapper draws its icon at runtime — a
  count that is not 25 means the manifest and the shipped set have drifted.
- Curate `manifest.yml`: real HTTPS `url` (project page, not an email),
  `keywords`, an `icon` PNG (~64×64; separate from the 24 px assembly icon),
  and the deliberately chosen package name (first upload fixes it forever).
- Build with `yak build --platform win` (the plugin is Windows-only — don't
  tag it `any`).
- Version: never re-release changed code under an existing version number.
  `<Version>` in `Lugworm/Lugworm.csproj` is the single source of
  truth — `tools/release.ps1` reads it and names every artifact from it. (This
  checklist deliberately no longer states a number; the last one it stated
  went stale, which is AUD-DOC-001.)

## Compatibility contracts (since 4.0.0)

The Rhino contract gate runs against the **unchanged v3 baseline**
(`tests/grasshopper/component-contracts-v3.json`); 4.0.0 declares its deliberate breaks on the
command line instead of recapturing, so the comparison stays a comparison. `-AllowedAdditions`
and `-AllowedRemovals` take comma-separated GUIDs; `-AllowedChanges` entries are `guid:keys`
joined by `;`, with a **colon** before the keys and either `all` or a comma-separated list of
record keys. Run it on all three payloads (`net48`, `net7.0`, `net7.0-windows`):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 `
  -Framework net48 `
  -AllowedAdditions a1a2c3d4-0030-4beb-9c3d-aa0000000030,a1a2c3d4-0031-4bfb-9c3d-aa0000000031,a1a2c3d4-0032-4c0b-9c3d-aa0000000032,a1a2c3d4-0033-4c1b-9c3d-aa0000000033 `
  -AllowedRemovals a1a2c3d4-0011-4abb-9c3d-aa0000000011,a1a2c3d4-0012-4acb-9c3d-aa0000000012,a1a2c3d4-0020-4b4b-9c3d-aa0000000020,a1a2c3d4-0021-4b5b-9c3d-aa0000000021,a1a2c3d4-0028-4bcb-9c3d-aa0000000028 `
  -AllowedChanges 'a1a2c3d4-0026-4bab-9c3d-aa0000000026:all','c1a2c3d4-0003-4a3b-9c3d-aa0000000003:all','a1a2c3d4-0029-4bdb-9c3d-aa0000000029:name,outputs','a1a2c3d4-0025-4b9b-9c3d-aa0000000025:outputs','f1a2c3d4-0006-4a6b-9c3d-aa0000000006:outputs','a1a2c3d4-0022-4b6b-9c3d-aa0000000022:name','a1a2c3d4-0023-4b7b-9c3d-aa0000000023:name'
```

The allowances, one line each: the four post-v3 components (`…0030` Pareto Select, `…0031`
Interlayer Time, `…0032` Legend, `…0033` Load Settings) are additions; the two UR GUIDs
(`…0011`, `…0012`), the two SCARA calibration GUIDs (`…0020` Calib Points, `…0021` Scara
Fit) and Version (`…0028`, removed 2026-09-21) are removals; Robot Simulation (`…0026`) and the G-code Generator
(`c1a2…0003`) changed their input specs and need `all`; the Duet Console (`…0029`) is
`name,outputs`; Robot Definition (`…0025`) and Overhang (`f1a2…0006`) are `outputs`; Machine
Files (`…0022`) and Live View (`…0023`) are `name` only. Anything the gate reports that is not
on that list is a real compatibility break — do not widen the switches to make it pass.

**0.1.0-beta (the 2026-09-22 rename) changed `name`, `class` and `category` of every
surviving v3 record** (the tab and namespace moved from `IAMLab` to `Lugworm`, the names
dropped the prefix; no GUID, nickname or socket moved). Until a v4 baseline is captured the
gate is run with those three keys allowed on all 24 survivors, on top of the allowances above:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 `
  -Framework net48 `
  -AllowedAdditions a1a2c3d4-0030-4beb-9c3d-aa0000000030,a1a2c3d4-0031-4bfb-9c3d-aa0000000031,a1a2c3d4-0032-4c0b-9c3d-aa0000000032,a1a2c3d4-0033-4c1b-9c3d-aa0000000033 `
  -AllowedRemovals a1a2c3d4-0011-4abb-9c3d-aa0000000011,a1a2c3d4-0012-4acb-9c3d-aa0000000012,a1a2c3d4-0020-4b4b-9c3d-aa0000000020,a1a2c3d4-0021-4b5b-9c3d-aa0000000021,a1a2c3d4-0028-4bcb-9c3d-aa0000000028 `
  -AllowedChanges 'a1a2c3d4-0001-4a1b-9c3d-aa0000000001:name,class,category','a1a2c3d4-0007-4a7b-9c3d-aa0000000007:name,class,category','a1a2c3d4-0008-4a8b-9c3d-aa0000000008:name,class,category','a1a2c3d4-0009-4a9b-9c3d-aa0000000009:name,class,category','a1a2c3d4-0010-4aab-9c3d-aa0000000010:name,class,category','a1a2c3d4-0013-4adb-9c3d-aa0000000013:name,class,category','a1a2c3d4-0014-4aeb-9c3d-aa0000000014:name,class,category','a1a2c3d4-0015-4afb-9c3d-aa0000000015:name,class,category','a1a2c3d4-0016-4b0b-9c3d-aa0000000016:name,class,category','a1a2c3d4-0017-4b1b-9c3d-aa0000000017:name,class,category','a1a2c3d4-0018-4b2b-9c3d-aa0000000018:name,class,category','a1a2c3d4-0019-4b3b-9c3d-aa0000000019:name,class,category','a1a2c3d4-0022-4b6b-9c3d-aa0000000022:name,class,category','a1a2c3d4-0023-4b7b-9c3d-aa0000000023:name,class,category','a1a2c3d4-0024-4b8b-9c3d-aa0000000024:name,class,category','a1a2c3d4-0025-4b9b-9c3d-aa0000000025:name,class,category,outputs','a1a2c3d4-0026-4bab-9c3d-aa0000000026:all','a1a2c3d4-0027-4bbb-9c3d-aa0000000027:name,class,category','a1a2c3d4-0029-4bdb-9c3d-aa0000000029:name,class,category,outputs','b1a2c3d4-0002-4a2b-9c3d-aa0000000002:name,class,category','c1a2c3d4-0003-4a3b-9c3d-aa0000000003:all','d1a2c3d4-0004-4a4b-9c3d-aa0000000004:name,class,category','e1a2c3d4-0005-4a5b-9c3d-aa0000000005:name,class,category','f1a2c3d4-0006-4a6b-9c3d-aa0000000006:name,class,category,outputs'
```

Anything else the gate reports is a real break. The probe itself expects at least 28
components since the Version component went (`tools/verify-component-contracts.py`).

Also run the two offline probes on the candidate: `-Probe pareto-selection` and
`-Probe interlayer-time`. Neither connects to a machine.

**Capture the v4 baseline only after the manual round on
`docs/dev/COMPONENT-TEST-SHEET.md` has passed**, and before the next release opens:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 `
  -Framework net48 -Capture -Baseline tests/grasshopper/component-contracts-v4.json
```

`-Capture` refuses to overwrite an existing baseline and refuses any `-Probe` other than
`contracts`, so a v4 file cannot be written to paper over a failed comparison. Once v4 exists,
4.1.0 compares against it with an empty switch list.

## Verification before upload

- `dotnet test Lugworm.Core.Tests` — all green.
- Unpack the built `.yak` (rename to `.zip`) and inspect every file.
- Scan the final binaries for local paths/usernames (`strings` / regex over
  the `.gha`, DLLs, and `.3dm` resources).
- Confirm both the binary and committed-only source ZIP pass the script's
  ASCII/UTF-16LE/UTF-16BE PII, Debug-marker and forbidden-file scans; `.patched`
  sanitizer intermediates must never appear in the source export.
- Install into a clean Rhino 8 profile via
  `yak install --source <folder> lugworm` (the `.yak` from `dist/`, also
  inside the send zip); confirm the Lugworm tab (seven groups, **22 ribbon
  components** plus six hidden compatibility components, `7 Print` reading Duet Console / Machine Files / Live View)
  and load the SCARA robot model. Since 4.0.0 there is only one embedded robot
  model — `UR_Robots.3dm` was removed and must not reappear in the payload.

## Publishing to GitHub

The public repository `JasperVoll/Lugworm` holds the code (same paths as this tree,
restricted to `tools/public-paths.txt`), the documentation site under `site/`, the
releases and the issue board. Its history is independent: each release lands as one
import commit, and pull requests merged there come back with `git am`. The private
history is never pushed. `site/`, `.github/` and the public `.gitignore` are edited in
the public checkout; everything on the allowlist is edited here and overwritten there.

1. Private tree clean and committed, `<Version>` set in both csproj files, tests green.
   Build with `tools/release.ps1 -ReleaseUrl https://github.com/JasperVoll/Lugworm`
   (candidate mode; once the owner gates are confirmed and HEAD is tagged `v<version>`,
   add `-Mode Public -OwnerGatesConfirmed`). `dist/` then holds the `.yak`, the send zip,
   `HASHES-<version>.txt`, the source export and `SOURCE-EXPORT-<version>.txt`.
2. Public checkout (`docs/github`): commit or stash changes to tracked files and check out
   the target branch (`import/<version>`, so the diff goes through a pull request and CI).
3. `tools/publish-source.ps1 -Tag`: refuses an export that does not match HEAD, replaces
   the allowlisted paths, commits `source: import Lugworm <version>` and tags `v<version>`.
   Read `git -C docs/github show --stat HEAD`.
4. Push branch and tag: `git -C docs/github push origin <branch> v<version>`. The build
   workflow (`.github/workflows/build.yml`) must pass; merge the pull request into `main`.
5. Draft release, notes written by hand (the source is the tag, not an asset):
   `gh release create v<version> --draft --prerelease --title "Lugworm <version>"
   --notes-file dist/RELEASE-NOTES-<version>.md dist/lugworm-<version>-rh8_0-win.yak
   dist/send-Lugworm-<version>.zip dist/HASHES-<version>.txt` (no `--prerelease` for a
   stable version).
6. Owner: check the draft, the site and the tree; publish the release; Food4Rhino listing
   linked to the same `.yak`. The first release also switches the repository to public.
7. A merged pull request comes back into this tree with
   `git -C docs/github format-patch -1 <sha> --stdout | git am` (author preserved).

The site is built by `.github/workflows/pages.yml` from `site/`; the Pages source of the
repository must be "GitHub Actions" (legacy mode would render the repository root).

## food4rhino listing

Separate from the Yak package: listing text, screenshots/images, release
notes, and a sanitized example `.gh` definition. A food4rhino app can be
linked to the Yak package so both distribution paths serve one artifact —
prefer that over maintaining two binaries. Submissions get human review;
plan for a delay.
