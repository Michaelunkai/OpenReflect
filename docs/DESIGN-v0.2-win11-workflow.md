# v0.2 design contract — win11 workflow (backup → offline restore)

Machine facts this design targets (measured 2026-10-13):
- Disk 0 = Samsung SSD 9100 PRO 2TB, GPT, boot+system. P1 EFI 256MB, P2 MSR 16MB, P3 C: 1861.57GB
  (~91GB used), P4 Recovery 1.08GB (WinRE on harddisk0/partition4). rmback's `1:1,1:3,1:4` (Macrium
  1-based) == EFI + C: + Recovery == everything except MSR. A whole-disk intelligent image is a
  strict superset and restores GPT + all partitions byte-exact.
- Macrium reference image set: 44.67 GB. F: (Ventoy USB SSD) has 480GB free. BitLocker off. Admin yes.

## 1. Format v2 (codec byte)
- FormatVersion 1 → 2. Header reserved byte 0x78 = compression codec (0 None, 1 Deflate, 2 Zstd).
- Reader accepts v1 (codec implied Deflate) and v2. Old binaries reading v2 get clean
  UnsupportedVersion. CRC already covers 0x78.
- Zstd via NuGet ZstdSharp.Port (managed port, MIT). Level mapping: clamp(level*2, 1, 19).

## 2. Parallel pipeline (create + verify)
- Engine: reader task → W workers (read, hash, zero-check, parent-dedup via serialized parent
  calls) → in-flight bounded results dict → single writer task appends in strict logical order
  (OrfWriter requires ascending index).
- OrfWriter gains PrepareChunk/AppendPreparedAsync so compression happens on workers; framing
  stays in Format.
- ISnapshotSession gains `IReadOnlyList<BackingRange>? BackedRanges` (default null = fully backed).
  A logical chunk fully outside every range is skipped without I/O (intelligent sector).
- Verifier: K independent OrfReader handles over the same file, workers per chunk, ordered failure
  accounting.

## 3. Intelligent sector = VSS snapshot + snapshot's own $Bitmap
- NEVER mix live allocation state with snapshot data: the allocation bitmap is parsed from the
  snapshot's own $Bitmap (MFT record 6) so skipped clusters are exactly the ones the snapshot's
  NTFS calls free. Direct (non-VSS) reads back up whole partitions (no bitmap skip) — no torn
  state class at all.
- NTFS parser scope: boot sector (BPB), $MFT cluster, MFT record 6, non-resident runlist of
  $DATA. Fallback when parse fails: back the whole partition (correct, slower) + log warning.
- VSS: AlphaVSS, one snapshot set for all NTFS volumes on the disk, crash-consistent-with-writers
  backup context, shadow devices read raw per extent. DeleteSnapshots on dispose. Any VSS failure
  → typed VssFailure → CLI falls back to raw read with a loud warning (Macrium parity).
- BackedRanges per disk = [0,1MiB) ∪ partition extents (bitmap-trimmed for VSS NTFS) ∪
  [size-1MiB, size). Gaps/MSR-cost ≈ zero I/O.

## 4. restore-disk (the guarded device writer)
Separate from `restore` (which still refuses devices). Gates, in order, all before ANY write:
1. Image complete (header flag).
2. Target resolved: \\.\PhysicalDriveN; disk size MUST equal image logical size (no resize in v0.2).
3. Running-OS gate: any volume on the target disk whose mount path is the running %SystemDrive%
   root ⇒ refuse unless OfflineEnvironment (set by the WinRE runner; running OS root is X:\ there).
4. Exclusive open (share-none). On sharing violation: FSCTL_DISMOUNT_VOLUME each partition volume
   of that disk (best effort), retry once, else refuse DestinationInUse.
5. Confirmed flag (CLI --confirm-device-write) + not DryRun.
Execution: ordered positioned writes of planned regions (decompress in parallel, write serial);
ZeroFillSource regions: skipped with SkipUnwrittenRegions, else explicit zero write. Post-write
verify ON by default: re-read every written region from the device and match stored SHA-256.
INCIDENT RULE STANDS: no test writes to a real disk; all device-write tests target VHDs.

## 5. Offline restore environment (WinRE, one-shot, self-expiring)
- Assets: C:\OpenReflectRestoreEnv\{orwinre.wim, boot.sdi, env-state.json}. orwinre.wim = copy of
  the system's Winre.wim (found via mountvol enumeration + \Recovery\WindowsRE probe), mounted
  with DISM, injected with: self-contained CLI folder \OpenReflect\, \OpenReflect\offline-restore.cmd,
  \Windows\System32\winpeshl.ini (LaunchApps → the cmd). WinRE payload untouched on disk;
  a COPY boots, never the registered one.
- BCD: well-known {ramdiskoptions} object (free on this system) gets ramdisksdidevice partition=C:
  + \OpenReflectRestoreEnv\boot.sdi; a new osloader entry mirrors the WinRE entry's settings
  (winload.efi, systemroot \Windows, detecthal, winpe) with device ramdisk=[C:]\OpenReflectRestoreEnv\orwinre.wim.
  Created persistently by setup; NEVER placed in bootsequence during this build (arming is a
  user-initiated orf-restore action).
- Arm = write F:\win11backup\restore-task.json (volume-GUID image path, target disk, size, options)
  + `bcdedit /bootsequence {guid}` (one-shot, self-expiring). Disarm = delete bootsequence + task.
- offline-restore.cmd: scans drive letters for \win11backup\restore-task.json (letters may differ
  in WinRE), runs the CLI with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, colors result, writes
  restore-done/failed log next to the task, waits for a keypress, then wpeutil reboot (keypress =
  user-initiated reboot; the script itself never reboots unprompted and never reboots from Windows).
- WinRE runtime risk (CLI inside WinRE) is the one untested-without-reboot variable: self-contained
  publish + invariant globalization; failure mode is a clean abort BEFORE any write (preflight
  first), leaving the disk untouched and the bootsequence consumed.

## 6. Scripts (the user-facing deliverables, F:\win11backup\)
- orf-backup.ps1 — rmback parity: admin check, Global mutex, reparse-point walk guards, name
  collision refusal, auto name backup_<ts>_<rand>, source = disk of C:, real-time 2s status line
  (GiB | pct | MB/s | elapsed | ETA) parsed from CLI `##PROGRESS` lines, mandatory full verify,
  verify-fail ⇒ delete the unverified new image and keep older sets, success ⇒ lock + cleanup of
  older .orf sets with unchanged-since-start checks. NEVER touches F:\win11recovery.
- orf-restore.ps1 — picks newest .orf, full verify, plan preflight, ensures restore env, arms.
  Modes: default arm+instructions (no reboot), -PlanOnly (dry), -Disarm, -SafeWrite (zero-fill
  instead of skip-unwritten), -Force re-arm. CLI at F:\backup\windowsapps\installed\OpenReflect\cli
  (stable install path, mirrors their mrauto.exe convention).

## 7. Evidence gates for "done"
- Existing 27 tests + new format/parallel/disk tests ALL GREEN via build.ps1.
- VHD e2e: GPT VHD (EFI fat32 + NTFS data with sparse file + random files) → intelligent backup
  (VSS+zstd) → full verify → restore-disk onto second VHD (both write modes) → attach → every
  file hash equal + chkdsk clean + partition table equal.
- Gate refusals: size mismatch, OS-on-target (live), unconfirmed, dirty image.
- Live C: VSS snapshot create/read/release smoke (read-only).
- Real backup run: actual orf-backup.ps1 against disk 0 into F:\win11backup + full verify +
  measured throughput/size vs the 44.67GB Macrium reference (restore NOT run — device write
  proof comes from the VHD e2e).
