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
  reading; VSS snapshots for consistent captures of in-use volumes; LZ4 and
  ZStandard compression levels; optional AES-256-GCM encryption.
- Open image format (`.orf`), fully specified in docs/FORMAT.md, with
  per-image format-version header.
- Verification: every image can be re-read and hash-checked after write, and
  re-verified on demand.
- Restore with safety gates: explicit confirmation before any target-disk
  writes, and target sanity checks before destructive operations.
- Scheduler: backup jobs run on Windows Task Scheduler schedules.
- Command-line interface (OpenReflect.CLI) covering backup, verify, restore,
  and job management.
- Windows Forms graphical interface (OpenReflect.GUI) for the same operations.
- Test suite under tests/OpenReflect.Tests (xUnit).

### Known limitations in 0.1.0

See ROADMAP.md for the full deferred list: no boot rescue media, no viBoot,
no ReDeploy-style dissimilar-hardware restore, no SQL/Exchange application
agents, no image mount/browse, local and UNC destinations only.

[0.1.0]: https://github.com/openreflect/openreflect/releases/tag/v0.1.0
