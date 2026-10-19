# OpenReflect

**Professional-grade Windows disk imaging — completely free, forever.**
Full / incremental / differential whole-disk images, intelligent used-sector imaging with VSS
consistency, guarded bare-metal restore through a one-shot WinRE boot, an open documented file
format, real-time progress with absolute numbers (GiB, MB/s, ETA), and a 49-test green gate.
Everything Macrium Reflect Pro does for its core job — imaging and restoring your entire Windows
disk — OpenReflect does **for free and fully open source**.

> Status: **0.2.0** — whole-disk imaging + offline restore workflow shipped, **49/49 tests green**
> (`build.ps1` → `ALL GREEN`). Honest parity gaps: [docs/PARITY.md](docs/PARITY.md),
> [ROADMAP.md](ROADMAP.md). Independent clean-room project — see [NOTICE](NOTICE).

---

## Why this exists — free, and better where it counts

Macrium Reflect Pro costs hundreds of dollars per year and is closed source. OpenReflect delivers
the same essential promise — *your entire disk, imageable and restorable, byte-identical* — with
advantages money can't buy:

| | Macrium Reflect Pro | OpenReflect |
|---|---|---|
| Price | paid license / subscription | **free, MIT-licensed** |
| Image format | proprietary `.mrimgx` | **open, byte-level documented** ([docs/FORMAT.md](docs/FORMAT.md)) — your backups are never hostage to one vendor |
| Inspection | you trust the tool | every chunk hash-verifiable, three verify depths, failing chunks **named** |
| What it does | imaging, plus a suite you may not need | does one thing, deeply: **image → verify → restore** |

Feature-for-feature (incrementals, encryption, scheduling, WinRE bare-metal restore, real-time
progress): see the complete matrix in [docs/FEATURE-MATRIX.md](docs/FEATURE-MATRIX.md) and the
parity ledger in [docs/PARITY.md](docs/PARITY.md).

## How the reverse engineering works (clean-room, and why it's legitimate)

OpenReflect reaches Macrium-class behavior through **black-box behavioral analysis, never
decompilation** — a textbook clean-room method:

1. **Observe, don't copy.** We studied *behavior*: what a professional image contains (partition
   tables, used-sector bitmaps, `$Bitmap` allocation semantics, `$MFT` layout), how incremental
   chains track changed regions, how a restore must sequence to be byte-exact — all observable
   from public NTFS documentation, on-disk structures, and the outputs backups produce.
   **No Macrium code, binaries, or assets were decompiled, copied, or included. Ever.**
2. **Specify first.** Findings are written as our own specifications
   ([docs/SPEC.md](docs/SPEC.md), [docs/BEHAVIOR-NOTES.md](docs/BEHAVIOR-NOTES.md)), then
   implemented from scratch in C#/.NET against those specs.
3. **Prove parity by measurement.** Every parity claim is backed by a test or a measured run —
   e.g. a real 2 TB system-disk image: **309 s backup + 168 s full verify = 477 s end-to-end**
   ([docs/PERF.md](docs/PERF.md)), and hard-won engine facts (VSS shadow-read semantics, dismount
   ordering, `$Bitmap` magic checks) each guarded by a regression test.

The result is a tool that behaves like the pros because we *understood* the domain — not because
we copied anyone's work. That is why it can be MIT-licensed and free.

---

## Features (complete list, 0.2.0)

| | |
|---|---|
| 🖼 **Whole-disk imaging** | images every partition of a physical disk (GPT/MBR), including the EFI and recovery partitions |
| 🧠 **Intelligent sector imaging** | parses NTFS `$Bitmap` + `$MFT` to copy only used sectors; zero regions are skipped and zero-filled on restore — faster backups, smaller images |
| 🧩 **Full / incremental / differential** | region-hash chains: unchanged regions are inherited from the parent, only changed sectors are stored |
| 🧊 **VSS consistency** | images the disk through a Volume Shadow Copy snapshot so a running Windows produces a crash-consistent image |
| 🔐 **AES-256-GCM encryption** | PBKDF2-HMAC-SHA256 (100k–5M iterations, 600k default); the password lives only in an environment variable, never in files or job JSONs |
| 🗜 **Compression** | zstd (fast, default) or deflate per region, levels 1–9; stored only when actually smaller |
| ✅ **Three-depth verification** | `structure` → `manifest` → `full` (re-reads and re-hashes every chunk); failing chunks are named |
| ⏪ **Byte-identical restore** | chain merge back to image files / mounted VHDs, and **whole physical disks** with size-match, running-OS refusal, and exclusive-open guards |
| 💿 **Bare-metal restore (WinRE)** | one-shot `bcdedit /bootsequence` arm → reboot → WinRE auto-restores from the task file → next boot is your restored Windows. Self-expiring, `-Disarm` cancels, never reboots itself |
| 💥 **Crash safety** | dirty-shutdown flag; interrupted images are detectable; the reader/writer pipeline survives worker faults and converts hangs into honest errors |
| 📊 **Real-time progress** | machine-readable `##PROGRESS <json>` lines with absolute numbers — GiB done, MB/s, ETA — plus a human summary `##DONE <json>` |
| 📅 **Scheduling** | Windows Task Scheduler integration: daily / weekly / monthly / at-logon, headless job JSON |
| 🖥 **CLI + GUI** | full command line for automation; WinForms GUI with live progress and tray |

## Install

**No build needed — download and run:**

1. Grab `OpenReflect-<ver>-win-x64-portable.zip` (+ `SHA256SUMS.txt`) from the
   [Releases page](https://github.com/Michaelunkai/OpenReflect/releases).
2. Verify the hash, extract anywhere, run `OpenReflect.CLI.exe` (or `OpenReflect.GUI.exe`).

**Or build from source** (dotnet SDK 10; this machine must use the pinned SDK via `build.ps1`):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1     # → ALL GREEN = exit 0
powershell -ExecutionPolicy Bypass -File packaging\package.ps1
```

## Usage — every feature, with real commands

`OpenReflect.CLI.exe disks` — **always start here**: lists disks, partitions, volumes so you pick
the right `--disk N`.

### Back up an entire Windows disk (the flagship workflow)

```powershell
# full image of the disk that hosts C: (VSS-consistent, intelligent sectors, zstd)
OpenReflect.CLI.exe backup-disk --disk 0 --dest E:\backups --name sys

# a week later — incremental: only sectors changed since the parent
OpenReflect.CLI.exe backup-disk --disk 0 --dest E:\backups --name sys --parent E:\backups\sys-<...>-full.orf
```

Progress prints live: `##PROGRESS {"gib":41.2,"total":83.4,"mbps":540,"etaSec":128}` — absolute
numbers, never vague percentages. Options: `--compress zstd|deflate|none --level 1-9`,
`--no-intelligent` (raw sectors), `--no-vss`, `--encrypt --password-env MY_PW`.

### The one-script workflow (recommended)

The repo ships two production scripts (used daily on the author's machine):

```powershell
# BACKUP — images disk 0 into F:\win11backup, keep-only-latest, ~5 min for 2 TB
powershell -ExecutionPolicy Bypass -File workflow\orf-backup.ps1            # real run
powershell -ExecutionPolicy Bypass -File workflow\orf-backup.ps1 -PlanOnly  # preview only

# RESTORE — verifies the newest image in full, then arms a one-shot WinRE restore
powershell -ExecutionPolicy Bypass -File workflow\orf-restore.ps1 -PlanOnly # dry-run: verify + plan, arms nothing
powershell -ExecutionPolicy Bypass -File workflow\orf-restore.ps1 -Force    # ARM (reboot manually to restore)
powershell -ExecutionPolicy Bypass -File workflow\orf-restore.ps1 -Disarm   # cancel a pending restore
```

Safety model of `orf-restore.ps1`: refuses unless the image passes a **mandatory full hash
verify**, the target size matches, and the OS is not running from the target disk. The actual
write happens offline in WinRE; the boot entry is one-shot and self-expiring, so the boot after
the restore is your normal — restored — Windows. It never reboots itself.

### Verify & inspect any image

```powershell
OpenReflect.CLI.exe verify backup.orf --depth full     # structure|manifest|full
OpenReflect.CLI.exe inspect backup.orf                 # header facts: size, chain, encryption, chunks
```

### Restore to a file or mounted VHD (safe, from the live OS)

```powershell
OpenReflect.CLI.exe restore backup.orf --target D:\restored.img --dry-run
OpenReflect.CLI.exe restore backup.orf --target D:\restored.img --confirm
```

Physical-disk restore is **guarded** and normally runs offline in WinRE (above); from the live
OS it refuses by design (`--plan` to see the plan, `--confirm-device-write --offline` only for
an unmounted, non-OS disk).

### Encrypt, decrypt, schedule

```powershell
# encrypted image — password from env var, never written to disk
$env:OPENREFLECT_PW = '...'
OpenReflect.CLI.exe create --source D:\data.img --dest E:\backups --prefix secret --encrypt --password-env OPENREFLECT_PW

# scheduler: nightly 03:00 full backup from a job JSON
OpenReflect.CLI.exe schedule add nightly job.json daily 03:00
OpenReflect.CLI.exe schedule list        # / run nightly / remove nightly
```

Job JSONs are headless and documented in [docs/USER-GUIDE.md](docs/USER-GUIDE.md). The **GUI**
(`OpenReflect.GUI.exe`) wraps the same engine: pick disk → destination → compress/encrypt → watch
live progress; sit in the tray while scheduled jobs run.

## Documentation

- [docs/USER-GUIDE.md](docs/USER-GUIDE.md) — jobs, chains, encryption, restore safety
- [docs/FEATURE-MATRIX.md](docs/FEATURE-MATRIX.md) · [docs/PARITY.md](docs/PARITY.md) — vs Macrium Reflect, honestly
- [docs/FORMAT.md](docs/FORMAT.md) — the open `.orf` format, byte level · [docs/SPEC.md](docs/SPEC.md) ·
  [docs/BEHAVIOR-NOTES.md](docs/BEHAVIOR-NOTES.md) · [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- [docs/DESIGN-v0.2-win11-workflow.md](docs/DESIGN-v0.2-win11-workflow.md) — the offline restore workflow
- [docs/PERF.md](docs/PERF.md) — measured numbers · [docs/RISKS.md](docs/RISKS.md) — safety law ·
  [docs/TEST-PLAN.md](docs/TEST-PLAN.md) · [docs/TRACEABILITY.md](docs/TRACEABILITY.md) ·
  [ROADMAP.md](ROADMAP.md) · [CHANGELOG.md](CHANGELOG.md) · [workflow/](workflow/) — production scripts
  + offline-env rebuild tooling

## License

MIT — see [LICENSE](LICENSE). Independent clean-room project; not affiliated with Macrium/Paramount
Software UK Ltd. "Macrium Reflect" is their trademark, referenced nominatively for parity context.
