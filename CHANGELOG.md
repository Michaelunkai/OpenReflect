# Changelog

All notable changes to OpenReflect are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning follows [Semantic Versioning](https://semver.org/) with the caveat
that 0.x releases may break the image format or CLI surface (see ROADMAP.md).

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
