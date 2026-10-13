# OpenReflect — Requirements Specification (S03)

Requirements are derived from the official Macrium feature set (docs/FEATURE-MATRIX.md) and
REA behavioral facts (analysis/ARCH-FACTS.md). Each SHALL has an id used by docs/TRACEABILITY.md.
Status column: ✅ implemented+tested in 0.1.0 · 🕐 deferred (ROADMAP.md).

## 1. Imaging

| ID | Requirement | Status |
|---|---|---|
| SH-01 | SHALL create full disk/volume/file-source images | ✅ |
| SH-02 | SHALL create incremental images storing only changed regions | ✅ |
| SH-03 | SHALL create differential images measured against the chain root | ✅ |
| SH-04 | SHALL implement used-sector imaging: all-zero regions are omitted, restore zero-fills them | ✅ |
| SH-05 | SHALL snapshot live sources for consistency (VSS seam; raw-read for files/VHDs in 0.1) | ✅/🕐 VSS 0.2 |
| SH-06 | SHALL support fixed-size regions (512 KiB…16 MiB) pinned per chain | ✅ |
| SH-07 | SHALL deduplicate unchanged regions against the parent chain | ✅ |
| SH-08 | SHALL resume an interrupted image (crash/cancel) without restarting | ✅ |
| SH-09 | SHALL detect crash-interrupted images via a dirty-shutdown flag and refuse to extend chains past them | ✅ |

## 2. Image format

| ID | Requirement | Status |
|---|---|---|
| SH-10 | SHALL use an open, byte-level documented format (`.orf`, docs/FORMAT.md) | ✅ |
| SH-11 | SHALL integrity-protect the header (CRC-32) and index (manifest SHA-256) | ✅ |
| SH-12 | SHALL store a plaintext SHA-256 per region for dedup + verification | ✅ |
| SH-13 | SHALL compress per region (deflate in 0.1; zstd 0.2), keeping only smaller results | ✅ |
| SH-14 | SHALL encrypt per region with AES-256-GCM, PBKDF2-HMAC-SHA256 (100k…5M iterations) | ✅ |
| SH-15 | SHALL detect corruption: bad magic/version, header CRC, manifest mismatch, truncated records | ✅ |
| SH-16 | SHALL skip unknown fields/records for forward compatibility | ✅ |

## 3. Verification & restore

| ID | Requirement | Status |
|---|---|---|
| SH-17 | SHALL verify at three depths: structure, manifest, full re-hash | ✅ |
| SH-18 | SHALL name failing chunks on verification failure | ✅ |
| SH-19 | SHALL restore by chain merge (newest region wins) to a file or mounted-VHD target | ✅ |
| SH-20 | SHALL plan before writing and execute a dry-run that writes nothing | ✅ |
| SH-21 | SHALL refuse unconfirmed restores and refuse physical-device targets in 0.1 | ✅ |
| SH-22 | SHALL refuse undersized targets and targets locked by other handles | ✅ |
| SH-23 | SHALL fail fast with no partial writes on wrong password | ✅ |
| SH-24 | SHALL restore byte-identical content (full, and incremental-merged chains) | ✅ |

## 4. Scheduling & jobs

| ID | Requirement | Status |
|---|---|---|
| SH-25 | SHALL register/list/remove/run Windows Task Scheduler jobs | ✅ |
| SH-26 | SHALL run jobs headless from job JSON (schemaVersion 1, forward-compatible) | ✅ |
| SH-27 | SHALL validate job JSON (chunk sizes, codecs, iteration bounds) with typed errors | ✅ |
| SH-28 | SHALL never store passwords in job files — environment variable or prompt only | ✅ |

## 5. Interfaces

| ID | Requirement | Status |
|---|---|---|
| SH-29 | SHALL provide a CLI: create / verify / restore / schedule / jobs with --help | ✅ |
| SH-30 | SHALL provide a GUI (WinForms) with backup, restore (plan+confirm), verify, schedule tabs | ✅ |
| SH-31 | SHALL surface progress (bytes, speed, ETA) and tray presence in the GUI | ✅ |
| SH-32 | SHALL confirm destructive actions in the GUI before writing | ✅ |
| SH-33 | SHALL log every run to a per-run file log | ✅ |

## 6. Process

| ID | Requirement | Status |
|---|---|---|
| SH-34 | SHALL produce typed, testable error results (no silent failures) | ✅ |
| SH-35 | SHALL ship a one-command build+test gate (build.ps1) | ✅ |
| SH-36 | SHALL publish portable packages with SHA-256 manifests | ✅ |
| SH-37 | SHALL stay clean-room: no Macrium code, formats, or assets (analysis/CLEANROOM.md) | ✅ |
| SH-38 | SHALL document honest parity gaps (docs/PARITY.md) | ✅ |
| SH-39 | SHALL image ≥1 GB sources with recorded throughput | ✅ (docs/PERF.md) |
| SH-40 | SHALL keep the entire suite green as the release gate | ✅ (docs/TEST-PLAN.md) |

Deferrals (not requirements in 0.1.0): WinPE rescue media, viBoot, ReDeploy, Exchange/SQL agents,
image browsing/mounting, Image-Guardian-style ransomware protection, split archives, cross-chain
dedup pool, parallel pipeline stages, zstd codec, email notifications — see ROADMAP.md.
