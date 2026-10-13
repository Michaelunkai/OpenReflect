# OpenReflect — User Guide (0.1.0)

## Concepts

- **Image (`.orf`)** — a point-in-time capture of a source (file, mounted VHD, volume). Regions
  (default 4 MiB) are hashed (SHA-256), compressed (deflate) and optionally encrypted
  (AES-256-GCM). Format: [docs/FORMAT.md](FORMAT.md).
- **Chain** — root full image + incremental/differential children. Incrementals compare with the
  previous image; differentials with the root. Unchanged regions are inherited; restore merges the
  chain newest-region-first.
- **Used-sector imaging** — all-zero regions are not stored. Restore writes zeros for them.
- **Dirty-shutdown flag** — interrupted images are marked incomplete; chains refuse to extend past
  them. Incomplete images can be resumed (`OrfImage.ResumeAsync` / re-run the job).

## Backing up

CLI:

```
OpenReflect.CLI create --source <file|device> --dest <dir> [--prefix p] [--kind full|incremental|differential]
                       [--parent <prev.orf>] [--compress deflate|none] [--level 1-9] [--chunk-size <512KiB..16MiB>]
                       [--encrypt --password-env VAR] [--iterations 600000] [--no-skip-zero] [--log-dir <dir>]
```

GUI: Backup tab → pick source (file or VHD path), destination, type, options → **Create image**.
Progress appears in the bar and tray tooltip.

Encrypted images: set an environment variable (e.g. `OPENREFLECT_PW`) and pass `--encrypt
--password-env OPENREFLECT_PW`. **Losing the password loses the image** — there is no recovery.

## Verifying

```
OpenReflect.CLI verify <image.orf> [--depth structure|manifest|full] [--password-env VAR]
```
Full depth re-hashes every stored region. GUI: Verify tab.

## Restoring (safety model)

1. **Plan** — always runs first and prints the region count, bytes and chain images. Writes nothing.
2. **Dry run** — `--dry-run` (CLI) or "Dry run" checkbox (GUI): proves the plan; writes nothing.
3. **Confirm** — `--confirm` (CLI) or the confirmation checkbox + dialog (GUI). Unconfirmed restores
   are refused with `RestoreSafetyGateBlocked`.
4. **Targets** — v0.1 restores to image files and mounted VHDs only. Physical devices are refused
   by design (see docs/RISKS.md §4); restore-to-disk ships with the 0.2 offline workflow.
5. Existing targets must be at least the image's logical size; a failed restore of a newly-created
   target is rolled back (deleted).

Encrypted restores take the password via `--password-env` / the GUI password field. A wrong
password fails at the first region with **no partial writes**.

## Scheduling

Create a job JSON (see ARCHITECTURE.md §5 for the full schema):

```json
{ "schemaVersion": 1, "name": "Nightly", "source": { "path": "D:\\vhd\\os.img" },
  "destination": { "directory": "E:\\backups", "fileNamePrefix": "os" },
  "chain": { "kind": "incremental" }, "verifyAfterWrite": true }
```

```
OpenReflect.CLI schedule add nightly E:\jobs\nightly.json daily 03:00
OpenReflect.CLI schedule list
OpenReflect.CLI schedule run nightly
OpenReflect.CLI schedule remove nightly
```

Incremental jobs automatically chain onto the newest matching image in the destination.

## Troubleshooting

| Symptom | Meaning / fix |
|---|---|
| `ChainIncomplete` | the newest image is crash-flagged; resume or remove it, then re-run |
| `WrongPassword` | wrong password (or corrupt image); fails fast, nothing written |
| `RestoreSafetyGateBlocked` | unconfirmed restore or a physical-device target (v0.1 refuses devices) |
| `RestoreTargetTooSmall` | existing target smaller than the image's logical size |
| `VssFailure` | VSS arrives in 0.2; v0.1 reads files/VHDs/idle volumes directly |
| `dotnet` build errors (MSB4236) | machine SDK is gutted — use `build.ps1` (pins the scoop SDK) |
