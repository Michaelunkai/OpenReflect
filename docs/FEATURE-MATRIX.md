# Macrium Reflect — Official Feature Matrix (S01)

Sources (accessed 2026-10-13): [macrium.com](https://www.macrium.com) ·
[products/home](https://www.macrium.com/products/home) ·
[reflectfree](https://www.macrium.com/reflectfree) ·
[Image Guardian](https://www.macrium.com/mig) ·
[legacy feature page](http://sm1.macrium.com) ·
[knowledgebase](https://kbx.macrium.com) · local install 10.0.8843 (analysis/ARCH-FACTS.md).

| # | Feature | v1-relevant | Notes |
|---|---|---|---|
| 1 | Full / incremental / differential disk images | YES | core |
| 2 | Image verification (integrity checking) | YES | core |
| 3 | AES backup encryption | YES | core |
| 4 | Compression | YES | core |
| 5 | Scheduling / backup plans | YES | core |
| 6 | Used-sector + forensic (all-sector) imaging modes | YES (used-sector) | forensic 🕐 |
| 7 | VSS-consistent snapshots of live volumes | YES | core |
| 8 | Rapid Delta Restore | partial | v0.1 restores changed regions via chain merge 🕐 fast-path |
| 9 | Rapid Delta Incrementals (changed-block tracking) | partial | region-hash model in 0.1 🕐 CBT |
| 10 | Image browsing / mounting as virtual drive | NO | 🕐 0.2+ |
| 11 | WinPE rescue media builder | NO | 🕐 0.2+ |
| 12 | viBoot (boot backups as VirtualBox VMs) | NO | 🕐 |
| 13 | ReDeploy (restore to different hardware driver injection) | NO | 🕐 |
| 14 | Macrium Image Guardian (anti-ransomware) | NO | 🕐 |
| 15 | File & folder backup (non-imaging) | NO | 🕐 |
| 16 | SQL Server / Exchange granular backup agents | NO | 🕐 |
| 17 | Email / webhook notifications | NO | 🕐 |
| 18 | Retention / consolidation of chains | partial | prune model designed; UI pruning 🕐 |
| 19 | Encryption of metadata + image | YES | core |
| 20 | BitLocker-aware imaging | partial | raw-read handles BitLocker volumes as raw 🕐 aware mode |
| 21 | Standalone verification tool | YES | `verify` CLI |
| 22 | Multiple compression levels | YES | core |
| 23 | GPT/UEFI + MBR disk handling | partial | images raw layout incl. GPT (proven by incident repair) |
| 24 | Split archives | NO | 🕐 |
| 25 | Boot menus / dual-boot rescue | NO | 🕐 |
| 26 | Server editions (Server Plus/Server) | NO | agents 🕐 |
| 27 | Deployment Kit / SiteManager (enterprise) | NO | out of scope |
| 28 | Technician's portable license | NO | out of scope |
| 29 | Incremental chains with consolidation | partial | chains without consolidation 🕐 |
| 30 | Progress, ETA, tray notifications | YES | core |
| 31 | Event-log / file logging | YES | core |
| 32 | Password policies (KDF strength) | YES | PBKDF2 600k default |
| 33 | Network destinations (SMB paths) | YES | file paths |
| 34 | Disk cloning (direct disk→disk) | NO | 🕐 0.2 (restore-to-disk lands with offline workflow) |
| 35 | Backup to DVD/BD optical | NO | 🕐 |
| 36 | PowerShell / automation interface | partial | CLI + job JSON cover headless use |
| 37 | Task Scheduler-based or service-based execution | YES | schtasks-based in 0.1 |
| 38 | Multi-destination backups | NO | 🕐 |
| 39 | Pre/post backup scripts | NO | 🕐 |
| 40 | Verified restore (verify-after-restore) | YES | restore + verify compose |
| 41 | Windows Event Log integration | NO | 🕐 |
| 42 | Romanian/… multi-language UI | NO | out of scope |

44 features catalogued; v1-relevant rows feed docs/SPEC.md shalls.
