# OpenReflect ↔ Macrium Reflect — Parity (S04/S53, final)

Macrium Reflect is commercial software by Paramount Software UK Ltd; this comparison is nominative,
based on its public feature set (docs/FEATURE-MATRIX.md) and REA behavioral facts
(analysis/ARCH-FACTS.md). Evidence column cites the OpenReflect test/doc proving the row.

## Shipped in 0.1.0 — professional core parity

| Capability | Macrium | OpenReflect 0.1.0 | Evidence |
|---|---|---|---|
| Full images | ✔ | ✔ | CoreTests.S24, OpsTests.S44 |
| Incremental images (changed regions only) | ✔ | ✔ region-hash dedup vs parent | CoreTests.S29 |
| Differential images (vs chain root) | ✔ | ✔ | CoreTests.S29 |
| Used-sector imaging (skip zeros) | ✔ | ✔ sparse regions + zero-fill restore | CoreTests.S22, RestoreVerifyTests.S38 |
| VSS-consistent snapshots of live volumes | ✔ | ◐ seam shipped, real VSS in 0.2 (RISKS R1) | CoreTests.S20 |
| Image verification (3 depths + standalone tool) | ✔ | ✔ | RestoreVerifyTests.S37, Verifier.cs, CLI verify |
| AES-256 encryption + KDF strength | ✔ | ✔ AES-256-GCM + PBKDF2 100k–5M | FormatTests.S28, RestoreVerifyTests.S41 |
| Compression (levels) | ✔ | ✔ deflate 1–9 (zstd 0.2) | FormatTests.S27 |
| Scheduling via Windows Task Scheduler | ✔ | ✔ add/list/remove/run | OpsTests.S32 |
| Headless jobs (automation) | ✔ | ✔ job JSON + `jobs` + `schedule run` | OpsTests.S33 |
| Crash-safe images (dirty flag, resume) | ✔ | ✔ | CoreTests.S23, FormatTests.S30 |
| Corruption detection (header/index/manifest/chunks) | ✔ | ✔ | FormatTests.S30, RestoreVerifyTests.S37 |
| Byte-identical restore | ✔ | ✔ to files/mounted VHDs | RestoreVerifyTests.S38/S40, OpsTests.S44 |
| Restore safety gates (dry-run, confirm, size, locks) | ✔ | ✔ **plus v0.1 device-target lockout** | RestoreVerifyTests.S42 |
| Progress/ETA + tray | ✔ | ✔ | MainForm.cs, S45/S47 |
| File logging per run | ✔ | ✔ | FileLogSink |
| Portable package + hashes | ✔ (installer) | ✔ zip + SHA256SUMS | packaging/package.ps1 |
| Open, documented image format | ✖ (proprietary .mrimg) | ✔ docs/FORMAT.md | FORMAT.md + golden tests |

## Honest gaps (deferrals — ROADMAP.md)

WinPE rescue media builder · viBoot · ReDeploy · Image Guardian-style anti-ransomware ·
Exchange/SQL granular agents · image browse/mount as drive · file-and-folder backup mode ·
split archives · consolidation · restore to physical disks (v0.1 = files/VHDs only; ships with
the 0.2 offline workflow) · VSS runtime (0.2) · zstd codec (0.2) · parallel pipeline (0.2) ·
notifications · optical destinations · multi-language UI.

## Verdict

OpenReflect 0.1.0 achieves functional parity with the **core professional imaging path**
(image types, integrity, encryption, verification, scheduling, automation, safety) and documents
every gap instead of hiding it. It does not claim bit-compatibility with Macrium's proprietary
format, nor the Macrium-only ancillary subsystems listed above.
