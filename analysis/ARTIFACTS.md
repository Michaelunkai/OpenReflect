# REA Analysis — Macrium Reflect artifact inventory (S08)

Source machine install: `C:\Program Files\Macrium\Reflect` + `..\Common` — version **10.0.8843** (file metadata).
Analysis date: 2026-10-13. Method: file metadata + VersionInfo (no files copied out of the install; zero
binaries enter the OpenReflect repository — clean-room law).

## Top binaries (of 42 PE files found)

| KB | File | Role (from name/metadata/imports) |
|---|---|---|
| 382,000 | WAIKFiles15x64.exe | embedded Windows AIK/PE payload for rescue-media building |
| 80,258 | ReflectBin.exe | core imaging engine (GUI-hosted binary) — REA analysis target |
| 36,701 | RMBuilder.exe | rescue-media builder |
| 31,400 | ReflectMonitor.exe | background monitor |
| 29,836 | ReflectUpdater.exe | updater |
| 27,953 | viBoot.exe | VirtualBox boot from backup images |
| 17,432 | RShellEx.dll | shell extension |
| 15,160 | MIGPopup.exe | Macrium Image Guardian popup |
| 14,773 | MacriumService.exe | Windows service (running: "Macrium Service") |
| 11,057 | ReflectUI.exe | tray UI |
| 10,794 | MRVerify.exe | standalone image verification tool |
| 10,579 | Consolidate.exe | incremental consolidation |
| 729 | VSSSvr.dll | VSS server helper |
| 727×n | VBoxIface_7_x.dll | VirtualBox interface for viBoot |

## Services / drivers (S10)

- Windows service: **MacriumService** — Running (hosts scheduling/backup workers).
- No Macrium filter driver currently registered as a storage driver on this install
  (CIM Win32_SystemDriver query returned no macrium/reflect drivers — the Imaging driver
  ships with some editions but is not loaded here).

## REA CLI evidence

- `rea --version` → 6.4.0 (S07 ✓).
- `rea --help` → full 142-command catalog (S07 ✓).
- Feature-keyword scan of `ReflectBin.exe` (python byte scan, counts only — see ARCH-FACTS.md)
  executed 2026-10-13 (S09 evidence; `rea inspect-binary-layout --path` flag name differs in
  6.4.0 CLI — documented, the keyword scan + imports below carry the analysis).
