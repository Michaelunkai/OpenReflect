# Performance — recorded 2026-10-14 (v0.2.0, real system disk)

Machine: Ryzen 7 9800X3D, 96 GB RAM, Samsung SSD 9100 PRO 2TB (system disk 0,
GPT: EFI + MSR + C: 1.86 TiB NTFS + Recovery), destination F: (Fanxiang 1 TB NVMe).

Real run via workflow\orf-backup.ps1 (repo) (backup-disk: VSS + intelligent
sectors + zstd level 3, 4 MiB chunks):

| Operation | Result |
|---|---|
| Whole-disk backup (2,000,398,934,016 B logical, 476,933 chunks) | **309 s** (~5.1 min) |
| Final image size | **89.5 GB (83.39 GiB)** for ~91 GB of used filesystem data |
| Full verify (every chunk decompressed + re-hashed) | **168 s** |
| End-to-end script (backup + verify) | **477 s** |
| Effective imaging throughput | ~3.0 GB/s logical read rate (mostly skipped unallocated space + parallel pipeline) |

Reference: the Macrium Reflect Free image of the same disk taken 2026-10-13 is
44.67 GB; OpenReflect's image is larger (it stores a superset — the full disk
including GPT, margins, and Recovery partition — and does not exclude pagefile /
hiberfil like Macrium's defaults) while restoring the complete 1:1 disk layout.
Imaging is far faster (minutes, not tens of minutes).

# Performance smoke (S54) — recorded 2026-10-13



Machine: Ryzen 7 9800X3D, 96 GB RAM, Samsung SSD 9100 PRO 2TB (C:), source = 1 GiB random file
on the same drive (worst case: incompressible data, same-disk I/O).

| Operation | Time | Throughput | Notes |
|---|---|---|---|
| `create` full image, 1 GiB, deflate L6, 4 MiB chunks | **24.7 s** | **~43 MB/s** | deflate-level-6 CPU bound; incompressible random data |
| `verify` full depth (all 256 chunks re-hashed) | **0.77 s** | ~1.4 GB/s | SHA-NI accelerated; file cache warm |

## Interpretation (honest)

- Verify throughput is excellent (SHA extensions).
- Create throughput is limited by the **sequential single-thread pipeline + deflate level 6**
  (ADR-13 deferral: parallel Channel pipeline + zstd are 0.2 items). Multi-TB images at this rate
  would take hours — the parallel + zstd work in 0.2 targets a 5–10× improvement; recorded here
  as the baseline per SH-39.
- Compressible data images faster (deflate throughput scales with content).
