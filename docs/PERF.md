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
