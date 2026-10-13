# REA Architecture Facts — Macrium Reflect 10.0.8843 (S11)

Behavioral/architectural facts derived with REA + local binary evidence. **Clean-room: byte counts of
feature keywords in `ReflectBin.exe` (no code, strings are not copied into OpenReflect).** Each fact cites
its evidence. These facts informed OpenReflect's design (see docs/ARCHITECTURE.md) and the parity matrix.

| # | Fact | Evidence |
|---|---|---|
| F1 | VSS is the snapshot mechanism (heavy runtime references) | keyword scan: `VSS` ×249 UTF-16, `ShadowCopy` ×5, `VSSSvr.dll` present in install |
| F2 | The native image format is `.mrimg` (plus XML metadata sidecars `.mrimg.xml` family) | `.mrimg` ×43 UTF-16; `mrimg` ×58 |
| F3 | Incremental + differential backup types are first-class | `Incremental` ×84, `Differential` ×52 UTF-16 |
| F4 | AES-256 encryption built into the imaging path | `AES` ×306 ascii, `AES-256` ×29 |
| F5 | Verification is a core operation with a standalone tool | `Verify` ×92, `checksum` ×83+, `MRVerify.exe` in install |
| F6 | Rescue media (WinPE) is a major subsystem | `WinPE` ×1154 ascii, WAIKFiles15x64.exe (382 MB embedded payload), RMBuilder.exe |
| F7 | viBoot boots backups as VirtualBox VMs | viBoot.exe + VBoxIface_7_0/7_1.dll |
| F8 | Exchange + SQL granular backup agents exist | `Exchange` ×240, `SQL` ×262 UTF-16 |
| F9 | BitLocker awareness in the imaging path | `BitLocker` ×124 UTF-16 |
| F10 | Scheduling runs via a persistent Windows service | MacriumService running; MacriumBackupMessage.exe for notifications |
| F11 | Rapid Delta Restore/Incremental concept present | `delta` ×14/×11, `rapid` ×1 (marketing term, sparse in binary) |
| F12 | Image Guardian (anti-ransomware write protection) ships separately | MIGPopup.exe + macrium.com/mig product page |

## What OpenReflect v0.1 takes from this

- VSS-first design confirmed as the professional approach → OpenReflect keeps the `ISnapshotProvider`
  seam with VSS planned for 0.2 (RISKS R1), raw-read for files/VHDs in v0.1.
- Chain types (full/inc/diff) and verification-as-first-class match OpenReflect's implemented model.
- AES-256 with PBKDF2 matches the professional baseline (OpenReflect: AES-256-GCM, 600k iterations).
- WinPE rescue, viBoot, Exchange/SQL agents, Image Guardian → confirmed as **separate subsystems**
  (honest v0.1 deferrals, see docs/PARITY.md and ROADMAP.md).
