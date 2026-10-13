# OpenReflect — Risks & Deferrals

## 1. Top risks

| ID | Risk | Mitigation |
|---|---|---|
| R1 | AlphaVSS TFM/compat issues on net10.0 or requires elevation | `ISnapshotProvider` seam (ARCHITECTURE.md D11): fake provider in tests; raw-read fallback for non-system disks; elevation-gated integration tests skip with documented reason |
| R2 | Raw disk writes can destroy data | **v0.1: physical-device targets are refused outright** (RestoreSafetyGateBlocked) after an actual incident on 2026-10-13 (see §4); file/VHD-image restore only; device restore ships with the 0.2 offline/WinPE workflow |
| R3 | Crash corrupts image mid-write | Dirty-shutdown flag + finalize-ordered writes (D5); Verify reports incomplete; chains refuse to extend past incomplete images |
| R4 | Password leakage | D10: passwords only via env var or prompt; never in job JSON/logs; wrong-password fails at first chunk, no partial restore writes |
| R5 | Sequential pipeline too slow on multi-TB disks | ADR-13: v0.1 sequential (correctness first); parallel Channel pipeline is 0.2; PERF.md records measured MB/s honestly |

## 4. Incident log

- **2026-10-13 — primary GPT overwritten by a gate test, repaired.** A safety-gate test invoked a
  restore with the system-disk override against the live boot disk; the exclusive raw-device open
  succeeded (elevated) and fixture bytes replaced LBA0-LBA2+. Repaired the same hour by rebuilding
  the primary GPT from the intact backup GPT (entry CRC verified, header fields repointed, CRC
  recomputed); Windows re-verified all partitions at their original offsets, C: healthy. Lesson:
  never exercise device-write paths against real hardware; make them structurally unreachable
  until complete (now enforced in code).

## 2. Open technical risks

- `\\.\PhysicalDriveN` sector-aligned writes require exclusive lock; in-use system disk restore requires offline/WinPE — out of v1 scope (deferral), CLI/Gate explains.
- VHD test fixtures rely on `Mount-DiskImage` (elevated); CI-style runs use sparse-file fakes.

## 3. Explicit v1 deferrals (honest parity gaps)

Not in 0.1.0, tracked in ROADMAP.md:
boot rescue media (WinPE builder) · viBoot · ReDeploy · SQL/Exchange granular agents ·
image browse/mount virtual drive · ransomware protection (Image-Guardian-like) ·
split archives · cross-chain dedup pool · parallel pipeline stages · zstd codec ·
email/web notifications · scheduling windows beyond daily/weekly/monthly · Linux restore env.
