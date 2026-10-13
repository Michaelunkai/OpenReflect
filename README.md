# OpenReflect

**Open-source Windows disk imaging — full, incremental and differential images with an open,
documented file format.** A clean-room, functional equivalent of the professional core of
Macrium Reflect: region-level incremental chains, used-sector imaging, AES-256-GCM encryption,
three-depth verification, byte-identical restore, Windows Task Scheduler integration, CLI + GUI.

> Status: **0.1.0** — core engine complete, 27/27 tests green. Honest parity gaps are listed in
> [docs/PARITY.md](docs/PARITY.md) and [ROADMAP.md](ROADMAP.md). Independent clean-room project —
> see [NOTICE](NOTICE).

## Features (0.1.0)

| | |
|---|---|
| 🖼 **Full / incremental / differential images** | region-hash chains; unchanged regions are inherited, changed ones stored |
| 🧹 **Used-sector imaging** | all-zero regions are skipped; restore zero-fills them |
| 🔐 **AES-256-GCM encryption** | PBKDF2-HMAC-SHA256, 100k–5M iterations (600k default); passwords never stored in job files |
| 🗜 **Compression** | per-region deflate, levels 1–9; stored only when smaller |
| ✅ **Verification** | structure / manifest / full re-hash depths; failing chunks are named |
| ⏪ **Byte-identical restore** | chain merge to image files and mounted VHDs (physical devices ship in 0.2 with the offline workflow) |
| 💥 **Crash safety** | dirty-shutdown flag; interrupted images are detectable and resumable |
| 📅 **Scheduling** | Windows Task Scheduler integration, headless job JSON |
| 🖥 **CLI + GUI** | full command line; WinForms shell with progress + tray |

## Quick start (CLI)

```powershell
# full image of a volume-mounted VHD or any file source
OpenReflect.CLI.exe create --source D:\vhd\win11.vhdx-mounted.img --dest E:\backups --prefix sys

# incrementals
OpenReflect.CLI.exe create --source ... --dest E:\backups --prefix sys --kind incremental --parent E:\backups\sys-<...>-full.orf

# verify + restore (dry-run first!)
OpenReflect.CLI.exe verify E:\backups\sys-<...>-full.orf
OpenReflect.CLI.exe restore E:\backups\sys-<...>-full.orf --target D:\restored.img --dry-run
OpenReflect.CLI.exe restore E:\backups\sys-<...>-full.orf --target D:\restored.img --confirm
```

Encrypted backups: add `--encrypt --password-env OPENREFLECT_PW` and set that environment variable.

## Build

`dotnet` on this machine's PATH is broken (gutted machine-wide SDKs) — **always build via**:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

which pins the healthy SDK (`F:\backup\windowsapps\installed\scoop\apps\dotnet-sdk\current`,
10.0.401), builds `OpenReflect.slnx` (9 projects) and runs the 27-test suite. Exit 0 = ALL GREEN.

## Packaging

```powershell
powershell -ExecutionPolicy Bypass -File packaging\package.ps1
```
→ `dist\OpenReflect-0.1.0-win-x64-portable.zip` + `SHA256SUMS.txt`.

## Documentation

- [docs/USER-GUIDE.md](docs/USER-GUIDE.md) — jobs, chains, encryption, restore safety
- [docs/FORMAT.md](docs/FORMAT.md) — the open `.orf` image format, byte level
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · [docs/SPEC.md](docs/SPEC.md) ·
  [docs/TRACEABILITY.md](docs/TRACEABILITY.md) · [docs/TEST-PLAN.md](docs/TEST-PLAN.md)
- [docs/PARITY.md](docs/PARITY.md) — feature parity vs Macrium Reflect, with honest gaps
- [ROADMAP.md](ROADMAP.md) · [CHANGELOG.md](CHANGELOG.md)

## License

MIT — see [LICENSE](LICENSE). Independent project; not affiliated with Macrium/Paramount Software
UK Ltd. "Macrium Reflect" is their trademark, referenced nominatively for parity context.
