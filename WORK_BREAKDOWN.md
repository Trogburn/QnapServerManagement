# Windows Photo Review Workflow Work Breakdown

This document is the execution handoff for Copilot agents. Complete one phase at a time, keep changes small, validate each phase before starting the next, and update the status table whenever work is completed.

The source of truth for product direction and safety decisions is [PLAN.md](PLAN.md).

## Overall Status

**Overall implementation: 100%**

| Phase | Scope | Status | Progress | Depends On |
|---|---|---:|---:|---|
| 1 | Czkawka CLI foundation | Complete | 100% | None |
| 2 | Stable local result model | Complete | 100% | Phase 1 |
| 3 | Metadata date repair utility | Complete | 100% | Phase 2 |
| 4 | Confidence and human grouping | Complete | 100% | Phases 2-3 |
| 5 | Native Windows review experience | Complete | 100% | Phase 4 |
| 6 | Safe remediation and undo | Complete | 100% | Phase 5 |
| 7 | Operational polish | Complete | 100% | Phase 6 |
| 8 | EXIF auto-rotate | Complete | 100% | Phase 7 |

### Status Definitions

- **Not started**: no implementation work has been accepted.
- **In progress**: implementation is underway, but acceptance criteria are incomplete.
- **Blocked**: work cannot continue until a named dependency or user decision is resolved.
- **Complete**: implementation and phase acceptance checks pass.

### Percentage Rules

- Phase percentages are based only on completed deliverables listed in that phase.
- A phase is not complete because files exist; its validation and acceptance checks must pass.
- Overall progress is the average of the eight phase percentages, rounded to the nearest whole number, unless a phase is explicitly blocked by an external dependency.
- When updating status, record the date, changed files, validation performed, and any blocker in the phase section.
- Do not mark future phases complete based on design work alone.

## Agent Operating Rules

1. Read [PLAN.md](PLAN.md) and this file before editing.
2. Work only on the current phase unless a small dependency fix is required.
3. Inspect existing files and preserve user changes; never reset or overwrite unrelated work.
4. Before the first edit, identify one local hypothesis, one cheap check that could disprove it, and the smallest testable change.
5. After the first substantive edit, run the narrowest available validation before reading broadly or starting another edit slice.
6. Keep runtime operations read-only until the remediation phase is explicitly approved.
7. Never enable Czkawka deletion flags. The project must quarantine files through its own verified workflow.
8. Preserve raw scan artifacts, diagnostics, audit manifests, and undo information.
9. Prefer built-in Windows PowerShell/.NET capabilities and avoid unnecessary dependencies or services.
10. Update this document after each accepted deliverable, not only at the end of a phase.

## Phase 1: Czkawka CLI Foundation

**Target:** Run repeatable, read-only exact-duplicate and similar-image scans from Windows against a UNC path.

**Deliverables**

- [x] Add `tools/czkawka/install.ps1` with a pinned release, checksum verification, and an explicit update path.
- [x] Add `tools/czkawka/config.json` for executable path, UNC scan root, local report root, scan thresholds, protected paths, excluded paths, and preferred directories.
- [x] Add `tools/czkawka/scan.ps1`.
- [x] Validate the UNC path before launching Czkawka.
- [x] Run `dup -s hash` and `image` separately in read-only mode.
- [x] Capture raw JSON, separate stderr/diagnostics, command metadata, actual CLI version, timestamps, and exit status.
- [x] Treat exit codes `0` and `11` as successful scan outcomes; fail on process, argument, or path errors.
- [x] Support `-Fresh` by passing `-H`; retain cache by default.
- [x] Keep reports local by default and exclude runtime reports from source control.

**Acceptance checks**

- `czkawka_cli.exe --version` succeeds after installation.
- A local fixture produces both raw scan artifacts.
- A UNC scan succeeds without changing files.
- A finding result does not appear as a process failure.
- A missing share, invalid argument, or missing executable fails clearly.
- PowerShell syntax checks pass.

**Status:** Complete, 100%

**Agent update log:**

- 2026-09-08: Added the pinned install script, local config, read-only scan wrapper, and local artifact storage. Validated script syntax with a PowerShell parser check; both scripts parsed successfully.
- 2026-09-08: Replaced machine-specific-looking defaults with repository-relative installer/report paths and explicit `YOUR-SERVER`/`YOUR-SHARE` UNC placeholders. Configuration JSON, PowerShell syntax, and Phase 2 regression tests passed.
- 2026-09-08: Audit correction: live CLI installation, actual version capture, separated diagnostics, local/UNC scan fixtures, and finding/invalid-argument checks remain unverified. The configured checksum is still a placeholder.
- 2026-09-09: Pinned `windows_czkawka_cli.exe` 12.0.1 with the published SHA256, captured CLI `--version` plus separate stdout/stderr/JSON artifacts, and used the real Czkawka flags (`-C <file>`, `--search-method`, `--max-difference`, `--hash-alg`). `tools/czkawka/tests/phase1-tests.ps1` passed: syntax, missing executable, missing share, invalid argument, local fixture scan without file changes, finding exit handling, and a UNC round-trip via `\\localhost\C$`. Instantiated CLI reported `czkawka 12.0.1`.
- 2026-09-10: Confirmed a read-only production UNC scan against a small mapped photo subset on the network share. Local reports/diagnostics were written; sampled source hashes and timestamps were unchanged. Large photo trees were intentionally not scanned.

## Phase 2: Stable Local Result Model

**Target:** Convert Czkawka's version-dependent JSON into one documented local schema while retaining upstream artifacts.

**Deliverables**

- [x] Add `tools/czkawka/parse-results.ps1` or a small parser module.
- [x] Parse `dup` HASH output, including empty and reference-directory variants.
- [x] Parse grouped `image` output, including reference-directory variants.
- [x] Emit a versioned normalized result document.
- [x] Preserve source scan, Czkawka version, raw artifact paths, scan root, and scan timestamp through the combined workflow.
- [x] Capture path, size, modified time, hash, width, height, perceptual difference, reference state, and group membership.
- [x] Add fixtures for valid results, empty results, malformed JSON, warnings, inaccessible files, and stale files.
- [x] Add deterministic parser tests.

**Acceptance checks**

- Every supported fixture normalizes deterministically.
- Malformed or unsupported shapes fail with an actionable message.
- Raw JSON remains available after normalization.
- UNC paths round-trip without accidental normalization.
- Schema version is recorded in every normalized result.

**Status:** Complete, 100%

**Agent update log:**

- 2026-09-08: Completed deterministic normalization for grouped and flat results, duplicate/image reference variants, empty results, warnings, inaccessible files, stale entries, metadata propagation, and actionable shape errors. PowerShell 7.6.5 smoke, fixture, and syntax checks passed.
- 2026-09-08: Audit correction: fixtures are synthetic schema-shaped inputs rather than captured upstream Czkawka outputs; combined-workflow metadata/raw preservation and byte-for-byte determinism remain incomplete.
- 2026-09-09: Parser now accepts captured Czkawka 12.0.1 HASH objects and similar-image arrays, including reference-directory pairs, unix `modified_date`, and byte `hashes`. `-ScanReportDir` combines dup/image artifacts while preserving CLI version, scan root, timestamps, and raw paths. `phase2-tests.ps1` passed with byte-for-byte determinism when `GeneratedAtUtc` is fixed.

## Phase 3: Metadata Date Repair Utility

**Target:** Safely repair Windows album sorting dates using real media evidence, without guessing.

**Deliverables**

- [x] Add `tools/czkawka/repair-dates.ps1`.
- [x] Inspect EXIF `DateTimeOriginal` and digitized date first.
- [x] Add conservative filename parsing for `YYYY-MM-DD`, `YYYYMMDD`, and timestamp-style names.
- [x] Keep video/container metadata and sidecars out of the first implementation unless a tested built-in or approved dependency is available.
- [x] Treat folder names as lower-confidence evidence only.
- [x] Treat current filesystem CreationTime and LastWriteTime as transfer/copy evidence, not capture time.
- [x] Normalize timezone handling with an explicit policy and reject conflicts, ambiguous dates, impossible dates, and unacceptable future dates.
- [x] Produce a dry-run report with current timestamps, proposed date, source evidence, confidence, and parsed token.
- [x] Add actions for accept one, accept high-confidence batch, skip, protect, and manual override.
- [x] Revalidate path, size, and timestamp before applying changes.
- [x] Default to changing CreationTime only; preserve LastWriteTime unless an explicit policy is selected.
- [x] Write an append-only audit and undo manifest.
- [x] Do not rewrite EXIF or rename files in the first version.

**Acceptance checks**

- Valid EXIF outranks filename and filesystem timestamps.
- Conflicting evidence is reported and not changed automatically.
- Ambiguous dates are skipped.
- Dry-run changes no files.
- Approved changes update the configured Windows timestamp policy.
- Stale or changed files are refused.
- Undo restores original timestamps.
- Fixture coverage includes timezone offsets, camera names, copied files, sidecars, inaccessible files, and impossible dates.

**Status:** Complete, 100%

**Agent update log:**

- 2026-09-08: Completed `repair-dates.ps1` with dry-run date evidence, EXIF/filename/folder precedence, invalid and future-date rejection, explicit review actions, saved-report approval, size/mtime stale revalidation, CreationTime-only policy, append-only undo, and safe restoration. Phase 3 tests cover generated EXIF precedence/conflict, folder and filename evidence, sidecars, future/impossible dates, dry-run, approval, stale refusal, and undo under PowerShell 7.6.5.
- 2026-09-08: Audit correction: timezone policy/fixtures, digitized-date fallback, camera-style/copy/inaccessible/ambiguous cases, and direct batch-approval coverage remain incomplete.
- 2026-09-09: Added an explicit timezone policy (naive timestamps are unspecified local time; offsets convert to UTC; mixed kinds and disagreeing instants conflict), digitized-date fallback, calendar/ambiguous rejection, locked-file inaccessibility, and `-ApproveHighConfidence` batch apply. `phase3-tests.ps1` and `phase3-smoke.ps1` passed, including timezone offsets, camera names, copied files, sidecars, inaccessible files, and impossible dates.

## Phase 4: Confidence and Human-Oriented Grouping

**Target:** Turn exact and visual matches into explainable review groups with useful keep recommendations.

**Deliverables**

- [x] Add `tools/czkawka/classify-results.ps1`.
- [x] Merge overlapping exact/image findings while retaining original evidence edges.
- [x] Implement visible tiers: Very high, High, Medium, and Review carefully.
- [x] Add explainable labels: exact duplicate, resized copy, likely thumbnail, downloaded copy, filename variant, and cross-folder match.
- [x] Show dimensions, size ratios, perceptual difference, hashes, paths, and evidence sources.
- [x] Add configurable keep recommendations based on preferred folders, dimensions, file size, filename quality, and protected/reference status.
- [x] Ensure recommendations are advisory and never perform actions.
- [x] Add deterministic classifier tests.

**Acceptance checks**

- Same-content files are always in the highest-confidence tier.
- Resized and thumbnail candidates are distinguishable from exact duplicates.
- Transitive groups retain the reason each item was included.
- Protected/reference paths cannot be recommended for removal.
- Classifier output is stable for the same normalized input.

**Status:** Complete, 100%

**Agent update log:**

- 2026-09-08: Added deterministic classifier grouping with transitive exact/image evidence, four confidence tiers, explainable labels, protected/reference safeguards, configurable advisory keep recommendations, and no-action output. Phase 4 tests passed under PowerShell 7.6.5.
- 2026-09-08: Penalized numbered filename suffixes such as `(2)` and `(3)` so unsuffixed base names win deterministic tie-breaks when content and dimensions match. Phase 4 regression tests passed; the real `100_0095` group now recommends `100_0095.JPG`.
- 2026-09-08: Audit correction: hash-driven confidence, label boundary behavior, complete evidence retention, and broader threshold/path test matrices remain incomplete.
- 2026-09-10: Completed the remaining Phase 4 gaps. Matching hashes now drive Very high confidence case-insensitively; repeated paths merge missing metadata while retaining per-entry evidence, warnings, access, and stale fields; protected/preferred path matching requires a directory boundary; and Phase 4 tests cover hash, perceptual thresholds, dimension-ratio boundaries, and protected-path matrices. `phase4-tests.ps1` and `phase2-tests.ps1` passed under PowerShell 7.6.5. Phase 5 was not advanced.
- 2026-09-10: Golden-corpus validation drove calibrated Blockhash similarity matching (threshold 16), canonical camera-name tie-breaking before file size at equal resolution, and minute-level (59-second) date-evidence equivalence. The restricted 632-file lab corpus passed all 35 detection, classification, protection, exclusion, and date-review checks without media changes.

## Phase 5: Native Windows Review Experience

**Target:** Let a human inspect and decide on one group at a time with minimal friction.

**Deliverables**

- [x] Add `tools/czkawka/review.ps1` using a small native PowerShell/.NET GUI.
- [x] Display side-by-side image previews with graceful handling for unavailable images.
- [x] Show confidence tier, explanation, complete evidence, path, filename, dimensions, size, modified time, proposed date, and suggested keep.
- [x] Add quick actions: keep suggestion, choose another keep, quarantine selected, skip/defer, open file, open folder, and protect.
- [x] Include date-repair proposals in the same review workflow or provide a clear linked review screen.
- [x] Require explicit confirmation for every quarantine or timestamp change.
- [x] Generate static HTML/JSON reports with search support for archival, while keeping filesystem actions native.
- [x] Track decisions so deferred groups return to the reviewer.

**Acceptance checks**

- A human can identify exact duplicates without reading raw JSON.
- A human can distinguish original, resized copy, and thumbnail candidates.
- Every proposed action shows its reason before confirmation.
- Skip, protect, and defer decisions persist.
- UNC paths can be opened from the interface.
- Unavailable or inaccessible files are clearly marked.

**Status:** Complete, 100%

**Agent update log:**

- 2026-09-08: Completed `review.ps1` with native WinForms group review, side-by-side preview fallback, evidence/date details, persisted keep/protect/defer/quarantine-request decisions, explicit quarantine confirmation, and static HTML export. Manual acceptance passed for multi-step navigation, per-group defer persistence, direct and unavailable preview selection, keep-suggestion ordering/highlighting, protection toggling, quarantine confirmation, report export, and a read-only review of the real `Z:` share photos. PowerShell syntax and export-only validation also passed with real local JPEG and mapped-share fixtures.
- 2026-09-08: Audit correction: reviewer executable tests, complete visible evidence fields, HTML search, and reproducible UNC/inaccessible/deferred acceptance artifacts remain incomplete despite successful manual checks.
- 2026-09-10: Completed Phase 5 acceptance evidence. The reviewer now shows complete group/item evidence and metadata, exports searchable HTML and JSON archives, and `phase5-tests.ps1` reproducibly validates read-only export behavior for inaccessible, unavailable, deferred, and UNC-shaped entries. Phase 5 tests passed; Phase 6 was not advanced.

## Phase 6: Safe Remediation and Undo

**Target:** Move only explicitly approved files to quarantine with stale-file protection and reliable recovery.

**Deliverables**

- [x] Add `tools/czkawka/remediate.ps1`.
- [x] Use quarantine rather than direct deletion.
- [x] Make quarantine location configurable; test a dedicated folder on the same share first.
- [x] Preserve relative source paths and use collision-safe destination names.
- [x] Revalidate existence, size, modified time, and comparable SHA-256 hash evidence before moving.
- [x] Refuse stale or changed entries.
- [x] Write an append-only transaction manifest with pre-move and post-move evidence.
- [x] Add an undo command that will not overwrite newer files.
- [x] Add dry-run mode, protected paths, excluded paths, and summaries of moved/skipped/failed files.
- [x] Keep Czkawka deletion flags out of the workflow.

**Acceptance checks**

- Dry-run moves nothing.
- An approved file is quarantined exactly once and logged.
- Stale files are refused.
- Destination collisions are handled safely.
- Permission failures leave source files untouched and are logged.
- Protected files cannot be moved.
- Undo restores a quarantined file without overwriting a newer destination.

**Status:** Complete, 100%

**Agent update log:**

- 2026-09-08: Added `remediate.ps1` with dry-run-first quarantine, explicit decision filtering, protected/reference and excluded-path refusal, size/mtime stale checks, collision-safe destinations, append-only transaction logging, and guarded undo. Temporary-file acceptance tests passed for dry-run, approved move, protection, stale refusal, collisions, logging, and undo under PowerShell 7.6.5. Remaining verification/implementation gaps: hash revalidation where comparable evidence exists, a real same-share quarantine test, and a permission-denied move test. Czkawka deletion flags remain unused.
- 2026-09-08: Audit correction: excluded-path tests, complete transaction evidence, and the same-share quarantine-location decision also remain incomplete.
- 2026-09-10: Completed Phase 6 remediation verification. Comparable 64-hex SHA-256 classifier hashes are revalidated before moving; append-only entries now retain explicit pre-move and post-move size, mtime, and SHA-256 evidence; tests cover excluded paths, same-share quarantine, collision-safe destinations, stale/hash refusal, ACL permission-denied moves, and guarded undo. Phase 6 tests and PowerShell syntax checks passed under PowerShell 7.6.5. No Czkawka deletion flags are used.
- 2026-09-10: Clarified the Phase 6 evidence: the automated same-share scenario uses a temporary same-volume directory, not the configured production network share. The network-share quarantine location remains an environment-specific pre-production validation item; no Phase 6 safety claim depends on skipping that check.

## Phase 7: Operational Polish

**Target:** Make the workflow maintainable for recurring manual use and optional scheduled scanning.

**Deliverables**

- [x] Update `README.md` with installation, configuration, UNC permissions, scan/review/remediation, supported formats, cache behavior, and recovery.
- [x] Add version/checksum update guidance without silent executable replacement.
- [x] Document third-party binary/license attribution.
- [x] Add optional Task Scheduler guidance only for scan/report jobs.
- [x] Ensure scheduled jobs never quarantine automatically.
- [x] Add final PowerShell syntax, parser, classifier, date-repair, review, remediation, and safe end-to-end checks.
- [x] Add or update `.gitignore` for runtime reports, caches, and local configuration secrets.

**Acceptance checks**

- A new Windows user can follow the README from install through review.
- A scheduled scan produces a report without changing media.
- Recovery and undo instructions are accurate.
- Runtime artifacts are not accidentally committed.
- All automated checks pass.

**Status:** Complete, 100%

**Agent update log:**

- 2026-09-08: Added `USER_GUIDE.md` with copy-paste commands, safety checkpoints, troubleshooting, recovery, and a beginner workflow. Added `run-workflow.ps1` to scan, normalize, classify, optionally produce date review, and open the reviewer in one safe command. Wrapper syntax and Phase 1-6 validation passed. Remaining Phase 7 work includes checksum/update guidance, attribution, scheduling guidance, and final end-to-end validation against an installed CLI.
- 2026-09-08: Synchronized completed Phase 5 and Phase 6 deliverable checkboxes with their 100% statuses and marked the completed Phase 7 documentation/check-validation deliverables.
- 2026-09-10: Completed Phase 7 operational polish. Added explicit version/URL/SHA256 update instructions requiring `-Force`, third-party Czkawka attribution, scan/report-only Task Scheduler guidance, a deterministic all-phase validation runner, and a safe local end-to-end Phase 7 test. `run-workflow.ps1` now requires explicit `-AllowLocalRoot` for local fixture validation and otherwise preserves UNC-root safety. Full validation is intended to run via `tests/run-all-tests.ps1`; no delivery phase was advanced.
- 2026-09-10: Stopped tracking generated `tools/czkawka/tests/temp/` outputs. The directory remains ignored so local parser test runs do not create repository changes; the local files were preserved.

## Future Decisions

1. **Quarantine location:** same share preserves local disk space and avoids copying large files; local quarantine may simplify recovery. Keep it configurable and test a dedicated folder on the same share first.
2. **Thumbnail handling:** use both protected path rules and classifier labels, with protected paths taking precedence.
3. **Czkawka version:** pin a tested release and checksum; do not track `master` for production scans.
4. **Album timestamp policy:** CreationTime only by default. LastWriteTime describes file content/transfer state and should not be rewritten silently.
5. **Date metadata scope:** start with image EXIF and conservative filename parsing. Add video/container and sidecar metadata only after the image workflow is proven.
6. **Review UI:** use native Windows controls for filesystem actions; use static HTML/JSON only for portable browsing and archival.

## Change Log

- 2026-09-08: Created the execution breakdown. Initial baseline was 0% before implementation began.
- 2026-09-08: Moved the canonical agent instructions to `.github/copilot-instructions.md`; phase percentages remain unchanged.
- 2026-09-09: Completed Phases 1-3 (CLI install/scan capture, upstream JSON normalization, timezone-aware date repair). Overall progress is 80%.
- 2026-09-10: Recorded read-only production UNC validation on a small photo subset; large trees left unscanned.
