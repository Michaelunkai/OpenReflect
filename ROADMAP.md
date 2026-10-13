# OpenReflect Roadmap

Status of each capability at a glance, then the honest list of what 0.1.0 does
not do and why. Shipped items link to the docs that describe them.

## Shipped in 0.1.0

| Capability | Where documented |
|---|---|
| Full, incremental, and differential disk/volume images | docs/USER-GUIDE.md |
| Used-sector imaging (unused blocks are skipped, not stored) | docs/USER-GUIDE.md |
| VSS-consistent snapshots of locked volumes | docs/USER-GUIDE.md |
| LZ4 or ZStandard compression | docs/USER-GUIDE.md |
| AES-256-GCM encryption with per-image keys | docs/USER-GUIDE.md |
| Post-write verification (re-read and hash check) | docs/USER-GUIDE.md |
| Restore with safety gates (target-write confirmation, same/other disk checks) | docs/USER-GUIDE.md |
| Scheduled jobs via Windows Task Scheduler | docs/USER-GUIDE.md |
| Command-line interface and WinForms GUI | README.md |
| Open, documented `.orf` image format | docs/FORMAT.md |

## Deferred, with rationale

These are capabilities of commercial imaging suites that 0.1.0 deliberately
does not ship. Each entry states why, and what using OpenReflect means for you
in the meantime.

- **Boot rescue media.** Building a bootable WinPE/Linux recovery environment
  requires downloading and repacking third-party boot components; shipping that
  pipeline reliably is a project of its own. Until then, restore must run from
  within Windows (or from another Windows installation on the machine) against
  offline target disks.
- **viBoot (virtual-boot of backup images).** Depends on Hypervisor platform
  integration and on serving the image format as a virtual disk. Deferred until
  the core format and restore paths are hardened by field use.
- **ReDeploy-style driver injection for dissimilar hardware.** Restoring to
  different hardware can leave Windows unbootable; fixing that requires offline
  registry and driver-store surgery that is easy to get dangerously wrong.
  Deferred until there is a well-tested, reversible implementation.
- **Application-aware SQL Server / Exchange agents.** Require per-product VSS
  writer orchestration and test infrastructure for those products. Deferred to
  keep the 0.1.0 dependency surface and test matrix small. Crash-consistent
  snapshots of the hosting volumes still work via VSS.
- **File-level mount/browse of images.** Requires a user-mode virtual
  filesystem or filter driver to expose image contents as a drive letter.
  Deferred for driver-signing and stability reasons; per-file recovery is
  possible today by restoring to a spare disk and copying from it.
- **Cloud storage destinations.** 0.1.0 writes to local and UNC paths only.
  Object-storage backends imply credential handling and resumable-upload
  design that deserves its own release.
- **Delta indexes / Rapid Delta Clone.** Incremental chains already avoid
  re-reading unchanged source data at backup time; merge-free forever
  incrementals with in-place chain editing are a later optimization.

## Versioning intent

0.x releases may change the `.orf` format or CLI surface; check CHANGELOG.md
before upgrading. The format version is recorded in every image header, so
images written by old versions remain restorable per the compatibility rules in
docs/FORMAT.md.
