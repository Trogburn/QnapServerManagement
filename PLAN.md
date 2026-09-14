# Windows Photo Duplicate Review Plan

Build a low-maintenance Windows workflow around the unchanged Czkawka CLI. The CLI performs detection against a UNC network share; a small local PowerShell/.NET layer normalizes its version-dependent JSON, ranks groups by confidence, presents a human-friendly review surface, and quarantines only explicitly approved files with verification and undo support.

## Phases

### 1. CLI foundation

- Add a pinned Czkawka CLI download/install script for Windows and a local configuration file containing the executable path, UNC scan root, local report root, and scan defaults.
- Add a scan script that validates the UNC path, runs `dup` and `image` separately in read-only mode with compact JSON, captures stderr/warnings, command metadata, CLI version, timestamps, exit status, and raw outputs.
- Treat exit codes `0` and `11` as successful scans; fail on argument/process/path errors. Keep `-W` and `-N`; do not suppress diagnostics with `-M` during the initial implementation. Support a `-Fresh` switch mapping to `-H`; otherwise retain Czkawka cache.
- Start with exact-content duplicates via `dup -s hash` and visually similar images via `image` using calibrated defaults: hash size 16, threshold 16, Blockhash algorithm, and geometric invariance off. Keep separate commands/config for later size/name duplicate modes if needed.
- Verify against a small local fixture first, then a mapped/UNC network folder, and confirm raw JSON and logs are preserved locally.

### 2. Stable local result model

- Add a parser/normalizer that accepts the known `dup` HASH JSON shapes and `image` grouped JSON shape, including reference-folder variants, and emits one versioned local schema.
- Preserve raw upstream JSON and record the Czkawka version so parser changes are isolated and historical scans remain explainable.
- Normalize entries to path, size, modified time, hash where available, width, height, perceptual difference, source scan, reference status, and group membership. Store scan-root identity and file size/mtime snapshots for later stale-report validation.
- Add parser fixtures for exact hash groups, image groups, empty results, malformed JSON, inaccessible files/warnings, and stale files.
- Verify parsing with deterministic normalized groups/counts without requiring a network share.

### 3. Metadata date repair utility

- Add a separate dry-run-first utility that inspects real media metadata and filename date patterns without changing files by default.
- Use an explicit evidence hierarchy: EXIF `DateTimeOriginal`/digitized date first; then video/container metadata where available; then conservative filename patterns such as `YYYY-MM-DD`, `YYYYMMDD`, and timestamp-style names; use folder names only as a lower-confidence fallback. Treat current filesystem CreationTime/LastWriteTime as evidence of transfer/copy activity, not capture time.
- Normalize timezone handling, preserve the original raw value and source, and reject ambiguous formats, impossible dates, future dates beyond a configured tolerance, and conflicting metadata instead of guessing.
- Produce a review report showing current CreationTime/LastWriteTime, inferred capture time, source, confidence, parsed filename token, and proposed changes. Add quick actions to accept one item, accept a whole high-confidence batch, skip, protect, or manually override.
- On approval, update Windows `CreationTime` and `LastWriteTime` only after revalidating path, size, and modified time. Keep the original filesystem timestamps and metadata evidence in an append-only audit/undo manifest. Do not rewrite EXIF or rename files in the first version.
- Optionally apply a configurable album policy such as setting CreationTime to capture time while preserving LastWriteTime, or setting both for Explorer sorting; make the policy visible before execution.
- Verify with fixtures containing valid EXIF, conflicting EXIF, filename-only dates, timezone offsets, camera-style names, copied files with misleading timestamps, sidecars, inaccessible files, and ambiguous dates. Test dry-run, approval, stale-file refusal, and undo.

### 4. Confidence and human-oriented grouping

- Build a classifier that merges overlapping exact/image findings into review groups while retaining evidence edges and original Czkawka groups.
- Define visible confidence tiers:
  - **Very high:** same full hash/content.
  - **High:** perceptual difference near zero or very small, with compatible dimensions.
  - **Medium:** strong visual match but meaningful resize/compression or filename/path evidence.
  - **Review carefully:** weak visual match, thumbnail-like dimensions, or ambiguous transitive grouping.
- Add explainable labels such as `exact duplicate`, `resized copy`, `likely thumbnail`, `downloaded copy`, `filename variant`, and `cross-folder match`. Avoid claiming semantic certainty; show the evidence behind every label.
- Calculate keep suggestions only as recommendations, using configurable signals: preferred directories, maximum dimensions, largest file size, filename quality, and whether the item is in a reference/protected path. Never auto-delete based solely on a score.
- Verify with deterministic tests for tier boundaries, dimension/file-size ratios, preferred-folder behavior, protected/reference behavior, and overlapping groups.

### 5. Native Windows review experience

- Implement a local Windows reviewer as a small PowerShell/.NET GUI, launched from the scan output. Use native image controls for side-by-side previews and UNC paths, with graceful fallback when an image cannot load.
- Show one group at a time with confidence tier, reason/evidence, path, filename, dimensions, file size, modified time, and suggested keep.
- Provide quick actions per group: keep suggested item and quarantine the rest, choose a different keep, quarantine selected items, skip/defer, open file, open containing folder, and mark protected. Require an explicit confirmation for every destructive/quarantine action.
- Keep a separate static HTML/JSON report for browsing, searching, and sharing metadata; the native reviewer owns actions because browsers cannot reliably write/delete arbitrary UNC files.
- Verify manually with exact duplicates, different-name same-content files, resized copies, thumbnail/original pairs, inaccessible files, and groups spanning local/UNC paths.

### 6. Safe remediation and undo

- Implement quarantine, never direct deletion initially. Default quarantine should be a configured folder on the network share or a local staging folder only after confirming the user’s storage preference; preserve relative source paths and collision-safe names.
- Before moving, revalidate existence, size, modified time, and hash where available. Refuse stale or changed entries and require a rescan/review.
- Write an append-only transaction manifest containing source, quarantine destination, timestamp, pre-move metadata, reason, reviewer action, and scan ID. Provide an undo script that validates the quarantine item and restores it without overwriting newer files.
- Add dry-run mode, protected paths, excluded paths, and a clear summary of moved/skipped/failed files. Do not expose Czkawka deletion flags in the first release.
- Verify dry-run, successful quarantine, stale-file refusal, name collision, permission failure, protected path, and undo scenarios.

### 7. Operational polish

- Add README documentation with installation, configuration, UNC permissions, scan/review/remediate commands, supported image formats, cache behavior, and recovery instructions.
- Add a version pin/update check without silently replacing the executable. Keep third-party binary/license attribution and checksums in the install metadata.
- Add optional Task Scheduler guidance only after the manual workflow is stable; scheduled runs should scan and report, never quarantine automatically.
- Keep the project dependency-light: PowerShell plus Windows/.NET built-ins, Czkawka CLI, and no database or web server unless the native reviewer proves inadequate.

### 8. EXIF auto-rotate

- Add a sibling review-first workflow that reads EXIF Orientation on JPEG/TIFF and proposes baking that rotation into stored pixels.
- When EXIF is Normal or missing, propose a rotation from photo content (Windows OCR and faces) at Medium confidence. Reviewers can also choose 90°/180° from the preview.
- Default remains dry-run. Apply copies the original to a local backup, rotates pixels, sets Orientation to Normal, and re-encodes JPEG. Undo restores the backup bytes.
- Preview the corrected photo in the WPF review page before Apply. Snapshot confirmation is required, matching date work.

## Planned Files

- `README.md` - Document the end-to-end workflow and safety model.
- `tools/czkawka/install.ps1` - Pinned CLI download, checksum verification, and installation.
- `tools/czkawka/config.json` - Executable, UNC root, report/quarantine roots, scan thresholds, protected/excluded paths, and preferred directories.
- `tools/czkawka/scan.ps1` - Process runner, diagnostics, raw artifact capture, and scan metadata.
- `tools/czkawka/parse-results.ps1` or a small local parser module - Isolate Czkawka JSON-shape handling and emit the stable schema.
- `tools/czkawka/classify-results.ps1` - Evidence-based confidence tiers and keep recommendations.
- `tools/czkawka/repair-orientation.ps1` - EXIF orientation inspection, dry-run proposals, bake-in apply with original backups, and undo.
- `tools/czkawka/repair-dates.ps1` - Metadata/filename date inspection, dry-run proposals, timestamp updates, and undo manifest.
- `tools/czkawka/review.ps1` - Native Windows group review UI and explicit action capture, including date-repair approvals.
- `tools/czkawka/remediate.ps1` - Verified quarantine, transaction logging, dry-run, and undo.
- `tools/czkawka/tests/` - Fixture JSON, classifier tests, and remediation safety tests.
- `reports/` - Runtime output directory, kept out of source control; add/update `.gitignore` accordingly.

## Verification Checklist

1. Install a pinned Czkawka CLI and run `--version`.
2. Run exact and similar-image scans against a local fixture; validate exit handling, raw JSON, diagnostics, and normalized output.
3. Run the same scans against a UNC path with read-only permissions and confirm paths round-trip unchanged.
4. Run date repair in dry-run against timestamp fixtures; verify EXIF precedence, filename parsing, timezone handling, conflict refusal, proposed Explorer timestamps, and undo data.
5. Open the native reviewer and manually process exact duplicates, different-name same-content files, resized copies, thumbnails, ambiguous matches, and date-repair proposals.
6. Execute remediation in dry-run, then quarantine a test group and apply an approved timestamp change; verify stale-file protection, transaction logs, and undo for both operations. Automated tests cover a same-volume temporary quarantine; validate the configured network-share quarantine location separately before production use.
7. Run PowerShell syntax checks and all local parser/classifier/date-repair tests before any Task Scheduler integration.
8. Run orientation dry-run against JPEG fixtures with Orientation 1 and 6; apply one approved rotation; confirm pixels/dimensions change, a backup exists, and undo restores the original SHA-256.

## Decisions

- Use the Czkawka CLI unchanged; do not copy its Rust scanning implementation.
- Windows-local orchestration and review; scan data lives on the network share but reports/logs remain local by default.
- Read-only detection and date inspection first; quarantine rather than delete; undo is required before routine use.
- Use a native reviewer for actions and static HTML/JSON for report portability. Do not depend on a browser being allowed to manipulate UNC files.
- Keep confidence explainable and advisory. No automatic deletion, timestamp changes, pixel rewrites, or automatic keep decisions.
- Treat capture time and filesystem time as separate concepts; default to changing Windows CreationTime for album sorting while preserving LastWriteTime unless the user explicitly selects a different policy.
- Naive EXIF and filename timestamps are unspecified local time. Explicit offsets and Zulu timestamps convert to UTC. Capture evidence within 59 seconds is equivalent; otherwise, disagreeing UTC instants are conflicts, not guesses.
- High-confidence EXIF batch apply requires `-ApproveHighConfidence` in addition to `-Apply`. Individual items still use approve paths or a decision file.
- Start with `dup` hash mode and `image`; add name/size modes only if real scan results show a useful gap.
- Auto-rotate uses EXIF Orientation first. If the tag is Normal or missing, the app may propose a content-based 90°/180° rotation. Bake pixels after review; keep original bytes in a backup for undo.

## Further Considerations

1. Decide where quarantine should live before production use: same share preserves capacity and avoids copying large files, while local quarantine simplifies recovery but requires enough local storage. Keep it configurable; automated tests use a same-volume temporary folder, while the configured production network-share location still requires environment-specific validation.
2. Decide whether thumbnails/originals should be protected by path rules or by the classifier. Recommendation: support both, with protected paths taking precedence.
3. Pin a tested Czkawka release and store a checksum; do not track `master` for production scans.
4. Decide whether the album policy should change CreationTime only or both CreationTime and LastWriteTime. Recommendation: CreationTime only by default, because LastWriteTime describes file content/transfer state and should not be rewritten silently.
5. Decide which metadata formats are in scope for the first date-repair release. Recommendation: image EXIF plus conservative filename parsing first; add video/container and sidecar metadata only after the image workflow is proven.
