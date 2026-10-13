# OpenReflect Roadmap

Status of each capability at a glance, then the honest list of what 0.1.0 does
not do and why. Shipped items link to the docs that describe them.

## Shipped in 0.1.0

| Capability | Where documented |
|---|---|
| Full, incremental, and differential images (file / VHD / volume sources) | docs/USER-GUIDE.md |
| Used-sector imaging (all-zero regions skipped, zero-filled on restore) | docs/USER-GUIDE.md |
| Region-hash chain dedup (unchanged regions inherited, not stored) | docs/ARCHITECTURE.md |
| Crash-safe images: dirty-shutdown flag + resume | docs/FORMAT.md |
| Deflate compression (levels 1–9), per-region framing | docs/FORMAT.md |
| AES-256-GCM encryption, PBKDF2 100k–5M iterations | docs/USER-GUIDE.md |
| Three-depth verification (structure / manifest / full hash) | docs/TEST-PLAN.md |
| Byte-identical restore to files and mounted VHDs, with safety gates | docs/USER-GUIDE.md |
| Scheduled jobs via Windows Task Scheduler | docs/USER-GUIDE.md |
| Headless job JSON (schemaVersion 1) | docs/ARCHITECTURE.md §5 |
| Command-line interface and WinForms GUI (progress + tray) | README.md |
| Open, byte-level documented `.orf` image format | docs/FORMAT.md |
| Portable package with SHA-256 manifest | packaging/package.ps1 |

## Deferred, with rationale

- **VSS snapshots of live/locked volumes.** The provider seam (`ISnapshotProvider`) ships and is
  tested; the real AlphaVSS/`vssapi` integration lands in 0.2 (docs/RISKS.md R1). v0.1 reads
  files, mounted VHDs and idle volumes directly.
- **Restore to physical disks.** v0.1 refuses every `\\.\` target outright. This is a deliberate
  incident-driven decision (docs/RISKS.md §4): raw-device gates cannot be exercised safely against
  a running system disk. Device restore ships with the 0.2 offline (WinPE-style) workflow — which
  is also the right place for **boot rescue media**.
- **Boot rescue media.** Building a bootable WinPE/Linux recovery environment requires downloading
  and repacking third-party boot components; it ships together with offline restore in 0.2.
- **zstd compression.** The codec is self-describing per region (FORMAT.md §7), so zstd drops in
  without a format change; v0.1 ships deflate to keep the dependency surface at zero.
- **Parallel pipeline.** v0.1 is deliberately sequential (measured ~43 MB/s on incompressible
  data, docs/PERF.md); the Channel-based parallel stages target 5–10× in 0.2.
- **viBoot (virtual-boot of backup images).** Depends on hypervisor integration; deferred until
  the format and restore paths are hardened by field use.
- **ReDeploy-style driver injection.** Offline registry and driver-store surgery is easy to get
  dangerously wrong; deferred until there is a well-tested, reversible implementation.
- **Application-aware SQL Server / Exchange agents.** Per-product VSS-writer orchestration needs
  test infrastructure for those products; deferred.
- **Image mount/browse as a drive letter.** Requires a user-mode filesystem or filter driver;
  per-file recovery today = restore to a spare image and copy.
- **File-and-folder (non-imaging) backup, split archives, consolidation, cloud destinations,
  email/web notifications, BitLocker-aware mode, Image-Guardian-style ransomware protection.**
  Each is a scoped project of its own; none is silently promised (docs/PARITY.md).

## Versioning intent

0.x releases may change the `.orf` format or CLI surface; check CHANGELOG.md
before upgrading. The format version is recorded in every image header, so
images written by old versions remain restorable per the compatibility rules in
docs/FORMAT.md.
