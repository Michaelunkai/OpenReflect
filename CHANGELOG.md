# Changelog

All notable changes to OpenReflect are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning follows [Semantic Versioning](https://semver.org/) with the caveat
that 0.x releases may break the image format or CLI surface (see ROADMAP.md).

## [0.2.0] - 2026-10-14

The "win11 workflow" release: whole-system disk imaging and guarded offline
restore, matching a professional bare-metal workflow end to end.

### Added

- Whole-disk intelligent imaging (`backup-disk`): images the full disk that hosts
  Windows (GPT/MBR layout, all partitions, head/tail margins) instead of single
  volumes. VSS shadow-copy consistency for NTFS volumes; allocation is read from
  the snapshot's own $Bitmap so unused space is never read or stored.
- DiskAccess layer: Win32 raw device I/O, GPT/MBR parsers, NTFS $Bitmap parsing
  with update-sequence fixup, multi-extent reads, volume enumeration, and VSS
  snapshot sets (AlphaVSS).
- Guarded device restore (`restore-disk`): plan/preflight gates (size match,
  running-OS refusal, explicit confirmation), exclusive open with mandatory
  target-volume offlining (mount points removed + IOCTL-based dismount so the
  filesystem cannot write behind the restore), ordered parallel write, optional
  post-write verify re-reading every written region.
- Real-time JSON progress (`##START`/`##PROGRESS`/`##DONE`) on stdout for
  scripting; parallel create pipeline (reader -> N workers -> ordered writer
  with watermark backpressure) and parallel full verify.
- zstd compression (levels 1-9) alongside deflate and none (format v2 codec
  byte); readers accept both format generations.
- E2E disk test suite on VHDs only (never real disks): GPT survival asserted by
  raw LBA reads after restore and after detach/attach, file-hash equality,
  partition-table equality, size-mismatch and running-OS refusals, and
  skip-unwritten writing strictly fewer bytes than zero-fill.

### Fixed

- Chunk-granular backed-range skipping dropped the GPT header chunk and
  non-aligned partition tails; backed ranges are now merged and expanded to the
  chunk grid before the containment check.
- $Bitmap "FILE" magic check compared a 3-character string against the
  4-character signature, silently disabling intelligent-sector imaging.
- Shadow devices clip large reads at the NTFS volume end (which can be smaller
  than its GPT partition): the shadow extent is now sized from the boot
  sector's own volume size, with the slack read directly from the disk.
- Shadow device reads are sector- and size-constrained: the $Bitmap is read in
  sector-aligned pieces and oversized single reads are rejected up front.
- Target restore write corruption when the target volume remounted mid-restore:
  mount points are deleted and the dismount is mandatory before any write.
- Pipeline hangs now fail fast: a reader fault aborts the ordered writer with
  the root-cause exception instead of spinning forever.

### Measured (real system disk, 2 TB NVMe, 476,933 chunks)

- backup-disk (VSS + intelligent sectors + zstd-3): 89.5 GB image, 309 s
  imaging, 168 s full verify; restored bytes byte-identical in the VHD e2e.

## [0.1.0] - 2026-10-13

First public release of OpenReflect, an open-source Windows disk-imaging suite
covering full, incremental, and differential images with an open, documented
file format.

### Added

- Core imaging engine: full, incremental, and differential images; used-sector
  imaging (all-zero regions skipped, zero-filled on restore); region-hash chain
  dedup; crash-safe images (dirty-shutdown flag + resume). Compression: per-region
  deflate, levels 1–9. Optional AES-256-GCM encryption (PBKDF2-HMAC-SHA256,
  100k–5M iterations; passwords via environment variable or prompt, never stored).
- Open image format (`.orf`), fully specified in docs/FORMAT.md, with per-image
  format-version header, CRC-protected header, SHA-256 index manifest and exact
  logical size in the footer.
- Verification at three depths (structure / manifest / full re-hash) with failing
  chunks named; `verify` CLI command.
- Restore with safety gates: plan-then-execute, dry-run, explicit confirmation,
  target size and lock checks, failed-restore rollback, and v0.1 refusal of
  physical-device targets (offline restore workflow arrives in 0.2).
- Scheduler: backup jobs run on Windows Task Scheduler schedules (`schedule
  add/list/remove/run`); headless jobs from schemaVersion-1 JSON (`jobs`).
- Command-line interface (OpenReflect.CLI) covering create, verify, restore,
  schedule, and jobs.
- Windows Forms graphical interface (OpenReflect.GUI) for the same operations,
  with progress bar and tray presence.
- Test suite under tests/OpenReflect.Tests (xUnit, 27 tests) covering format
  conformance, engine behavior, verify/restore gates, scheduler and CLI E2E.
- Documentation set: SPEC, FEATURE-MATRIX, PARITY (honest gaps), TRACEABILITY,
  TEST-PLAN, PERF, USER-GUIDE, FAQ, FORMAT, ARCHITECTURE, RISKS.

### Deferred to 0.2 (see ROADMAP.md)

VSS snapshots of live volumes (provider seam shipped) · restore to physical disks
(offline/WinPE workflow) · boot rescue media · zstd codec · parallel pipeline.
