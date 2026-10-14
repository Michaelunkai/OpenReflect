# HANDOFF — OpenReflect v0.2 "win11 workflow" (continuation doc, 2026-10-13)

## 0. CONTINUATION ORDER (binding on the receiving session)
You are the continuation of the session that wrote this document. You inherit
ALL of its context and ALL of the user's demands below. You do not stop, sleep,
idle, or wait passively: you work continuously, as fast as accurately possible,
until EVERY demand has passed its real-world test to perfection — no fewer.
Every claim must be proven with real evidence; never claim success without it.
A demand stated at the very start of a long session carries the same weight as
one stated minutes ago. Resume exactly where the previous session stopped.
Work SOLO — the user explicitly ordered "Continue without sub agents".

## 1. MISSION LAW — every user demand, enumerated (no exceptions)
1. Install/configure/integrate/test/repair/launch latest REA using the logged-in
   ZCode subscription, no API keys — status: DONE (F:\backup\windowsapps\installed\rea
   = rea-agents 6.4.0; reports in F:\backup\windowsapps\installed\REA-mission\).
2. Output a PowerShell one-liner to run REA — status: DONE (delivered in session).
3. /sub 10 /steps 60: build an open-source app working "literally exactly in any
   aspect as Macrium Reflect with all its professional features", in a NEW folder,
   using REA — status: DONE (OpenReflect v0.1.0 at the repo below; GitHub release
   v0.1.0 with portable zip SHA256 F2D2755A...; 27/27 tests; full docs).
4. "Continue without sub agents" — status: ACTIVE LAW (solo execution from here on).
5. CURRENT GOAL (/goal, in flight): OpenReflect CLI must back up exactly like the
   user's `rmback` function (EFI+C:+Recovery system-disk image) — status: IN FLIGHT
   (engine 95% done; scripts not yet written).
6. Same goal: backup destination ONLY F:\win11backup; NEVER touch F:\win11recovery
   (the Macrium folder) — status: not yet violated; keep it that way in scripts.
7. Same goal: backups must be "flawless ... same way reflect pro does, but even
   faster with real-time progress and making the backup even smaller" — status:
   IN FLIGHT (intelligent-sector + zstd + parallel implemented; real numbers pending).
8. Same goal: restore script like `macres` — uses the LATEST created backup in
   F:\win11backup, restores it automatically as fast as possible — status:
   IN FLIGHT (DiskRestorer + WinRE offline plan implemented, env builder not written).
9. Restore/backup scripts must NEVER reboot by themselves; the receiving session
   must NOT run a restore and must NOT reboot the machine — status: standing law.
10. Do not stop compiling and testing "until we made it sure" / "only when you
    literally have no doubt this works flawlessly" / "million percent" — status:
    active bar; every claim needs test evidence.
11. Output the FULL PATHS of the backup script and the restore script at the end —
    status: PENDING (final deliverable).
12. Never fabricate results; real evidence only — status: standing law.
13. Clean-room law: no Macrium code/formats/assets; REA gave behavioral facts only —
    status: DONE, keep honoring (no 'macrium' strings in .cs files).
14. NEVER write to a real disk in tests; all device-write tests target VHDs only
    (GPT-incident lesson, documented docs/RISKS.md §4) — status: standing law.

## 2. ONE-PARAGRAPH STATE
OpenReflect v0.2 is ~85% done: format v2 (zstd codec byte at header 0x78), parallel
create pipeline (watermark + ordered dictionary — the earlier ring-slot TCS design
was killed by a stale-completion race), parallel verify, the new DiskAccess project
(Win32 IOCTLs, GPT/MBR pure parsers, NTFS $Bitmap parser with USA fixup,
MultiExtentStream, volume enumerator, VSS via AlphaVSS), DiskRestorer (hard gates:
size match, running-OS refusal, exclusive open + dismount retry, post-write verify),
CLI commands (disks, backup-disk, inspect, restore-disk with ##PROGRESS JSON lines),
and 45 tests of which 44 pass. THE ONE BLOCKING ITEM: E3 (VHD e2e) proves the
intelligent backup DROPS chunk 0 (the GPT) when the first partition starts below
1 MiB — the chunk-granular backed-skip loses sub-chunk margins and non-aligned
partition tails. The fix (merge backed ranges + expand to chunk grid before the
containment check) is ALREADY APPLIED in src/Core/ImageEngine.cs
(MergeAndExpandToChunkGrid — verified present); the post-fix E3 rerun was the exact
command interrupted. Run it next.

## 3. KEY PATHS & IDENTITIES
| what | where |
|---|---|
| Repo | F:\study\Platforms\Windows\Applications\Desktop\Utilities\System\Backup\Imaging\OpenReflect |
| Git | main branch, last commit 117b0c3 "0.1.0"; 20 files modified/uncommitted (all v0.2 work) |
| GitHub | github.com/Michaelunkai/OpenReflect (push via stored git credential + API, no gh CLI) |
| Design contract | docs\DESIGN-v0.2-win11-workflow.md in the repo (READ IT FIRST) |
| Test project | tests\OpenReflect.Tests (45 tests: FormatTests, CoreTests, RestoreVerifyTests, OpsTests, FormatZstdTests, EngineParallelTests, DiskAccessTests, DiskRestoreE2ETests) |
| Backup destination | F:\win11backup (exists, empty) |
| NEVER touch | F:\win11recovery (holds Macrium backup_20261013_154749_197_ded82080-00-00.mrimgx, 44.67 GB — the size reference to beat) |
| Deploy target for CLI | F:\backup\windowsapps\installed\OpenReflect\cli (stable install path, mirrors their mrauto.exe convention) |
| Offline restore env (to build) | C:\OpenReflectRestoreEnv\{orwinre.wim, boot.sdi, env-state.json} |
| System disk | disk 0 = Samsung 9100 PRO 2TB GPT: P1 EFI 260MB@1MiB, P2 MSR 16MB@261MiB, P3 C: 1861.57GiB (~91GB used, BitLocker off), P4 Recovery 1.08GB (WinRE on harddisk0\partition4) |
| WinRE BCD facts | WinRE entry {13417136} + device options {1341713c} (ramdisk=[\Device\HarddiskVolume4]\Recovery\WindowsRE\Winre.wim); well-known {ramdiskoptions} object is FREE on this system |
| WinRE copy source | find via mountvol enumeration probing \<Recovery\WindowsRE\Winre.wim per \\?\Volume{guid}\ |
| dotnet | scoop SDK 10.0.401 at F:\backup\windowsapps\installed\scoop\apps\dotnet-sdk\current |
| Scratch/VHD tests | %TEMP%\dr-* (self-cleaning in test finally blocks) |
| Memories | C:\Users\Admin\.zcode\cli\memories\projects\installed-aa536333e1670a5b\memory\openreflect-project.md, openreflect-gpt-incident-lesson.md |

## 4. WHAT IS DONE (all evidence-backed)
- Format v2: codec byte 0x78 (0 None/1 Deflate/2 Zstd), reader accepts v1+v2, writer
  emits v2; zstd via ZstdSharp.Port 0.8.8; PreparedChunk API for parallel workers.
  Evidence: FormatZstdTests 6/6 green.
- Parallel engine: reader task → W workers (zero-check, parent-dedup via serialized
  ParentGate, PrepareChunk) → ConcurrentDictionary by logical index → ordered writer
  with SemaphoreSlim watermark (workers*4). Ring-slot TCS design REMOVED (stale
  TaskCompletionSource on slot reuse caused phantom completions + SemaphoreFull).
  Evidence: EngineParallelTests 2/2, full suite 44/45 (only E3 open).
- Parallel verify: N independent OrfReader handles, workers per chunk. Tested via
  suite (verify tests green).
- DiskAccess project: Win32 (CreateFileW+FILE_FLAG_OVERLAPPED, IOCTLs, SafeFileHandle),
  DiskLayout (GPT/MBR pure parsers + live read; BackedRanges = margins+partitions),
  NtfsAllocation (boot sector → MFT record 6 → runlist → $Bitmap, USA fixup applied),
  MultiExtentStream (lazy per-extent streams), VolumeEnumerator (FindFirstVolume +
  IOCTL_STORAGE_GET_DEVICE_NUMBER + drive-letter map), VssSnapshotSet (AlphaVSS:
  InitializeForBackup → SetContext(Backup) → SetBackupState → GatherWriterMetadata →
  StartSnapshotSet/AddToSnapshotSet → PrepareForBackup → DoSnapshotSet →
  GetSnapshotProperties → {OriginalVolumeName: SnapshotDeviceObject};
  DeleteSnapshotSet on dispose).
  Evidence: DiskAccessTests 7/7 green INCLUDING live VSS snapshot of C: and live
  disk-0 layout read (2000398934016 B, 4 GPT partitions correctly typed).
- DiskRestorer: PlanAsync/PreflightAsync/ExecuteToDiskAsync. Gates in order: device
  path shape → size match (RestoreTargetTooSmall) → running-OS-on-target refusal
  (RestoreSafetyGateBlocked unless OfflineEnvironment) → Confirmed → exclusive open
  (FSCTL_DISMOUNT_VOLUME retry once) → ordered parallel write → optional post-write
  verify (re-read + SHA-256 vs stored chunk hashes). Evidence: E1 + E2 green
  (size-mismatch refusal and running-OS refusal even when Confirmed=true).
- CLI: `disks` (live-verified listing), `backup-disk` (intelligent+VSS+zstd,
  ##START/##PROGRESS/##DONE JSON lines), `inspect`, `restore-disk`
  (--plan/--confirm-device-write/--skip-unwritten/--offline/--task-file). Builds green.
- E2E discovered+fixed two REAL data-loss bugs: (1) ring-slot stale completion race
  (rewritten); (2) chunk-granular backed-skip drops the GPT chunk when the head
  margin is sub-chunk and drops non-chunk-aligned partition tails — fix applied in
  ImageEngine.MergeAndExpandToChunkGrid (merge ranges, then expand to chunk grid,
  then containment check). Post-fix rerun NOT yet executed (interrupted).

## 5. IN FLIGHT / BLOCKED
Exact point of interruption: the python patch that applied MergeAndExpandToChunkGrid
to src/Core/ImageEngine.cs COMPLETED (grep confirms 2 references), but the follow-up
command `dotnet test ... --filter FullyQualifiedName~DiskRestoreE2E` was cancelled.
IMMEDIATE BLOCKER TO CLEAR: rerun E3 and confirm RawHasGpt passes both immediately
after the write pass and after detach/attach (those asserts are already in
DiskRestoreE2ETests.E3). If E3 still fails with "GPT must be on the restored disk",
inspect which regions the plan contains for chunk 0 (CLI: restore-disk --plan or a
debug print of plan.Regions[0..3]) and check the backup image stored-chunk list
(inspect JSON chunkCount vs expectation ~27 chunks for a 512MB VHD).

## 6. FINAL ARCHITECTURE & DECISIONS (failed variants — do NOT revisit)
- Whole-disk intelligent image (head 1MiB + partitions + tail 1MiB) supersedes
  per-partition imaging: restores GPT + all partitions byte-exact, superset of
  rmback's 1:1,1:3,1:4 (Macrium 1-based disk numbering).
- Intelligent sector = VSS shadow + $Bitmap parsed FROM THE SNAPSHOT (never the
  live volume bitmap — COW deletes would desync). Direct (non-VSS) reads back whole
  partitions, never bitmap-trimmed. NTFS-only trimming; EFI/MSR/Recovery full.
- FAILED: ring-slot TCS pipeline (stale completed TCS on slot reuse → phantom
  writer progress → SemaphoreFullException). Use watermark+dict everywhere.
- FAILED: IOCTL_DISK_GET_LENGTH_INFO on this stack (ERROR_INVALID_FUNCTION) → use
  GET_DRIVE_GEOMETRY_EX 0x000700A0 (sector size at byte 20, disk size at 24).
- FAILED: GetVolumePathNamesForVolumeNameW (ERROR_INVALID_PARAMETER) → drive-letter
  map via GetLogicalDriveStringsW + GetVolumeNameForVolumeMountPointW.
- FAILED: referencing AlphaVSS only transitively through DiskAccess ProjectReference
  — NuGet reduces native runtimeTargets to "_._" markers downstream. FIX: direct
  PackageReference AlphaVSS 2.0.3 in BOTH CLI and Tests csproj (natives then land in
  bin\runtimes\win-x64\native and NATIVE_DLL_SEARCH_DIRECTORIES resolves them;
  DefaultVssAssemblyResolver reads that AppContext data — no manual copy needed).
- FAILED: `using AlphaVSS;` — the namespace is Alphaleonis.Win32.Vss.
- FAILED: FileStream(SafeFileHandle, ..., isAsync:true) on a non-overlapped handle →
  Win32.Open now passes FILE_FLAG_OVERLAPPED.
- Restore safety: file-target Restorer keeps refusing devices; DiskRestorer is the
  only device writer and refuses the running-OS disk unless OfflineEnvironment.
- GPT-incident law: tests never write real disks (E-tests assert diskNumber != 0).
- Python heredoc patches are dangerous (\n, \2 octal, \w mangling poisoned files
  twice — one file was left binary-corrupt). Prefer Write/Edit tools; if python,
  use raw strings and re-verify file encoding afterwards.

## 7. IMMEDIATE NEXT STEPS (execute in order, exact commands)
1. cd repo; run: dotnet test tests/OpenReflect.Tests/OpenReflect.Tests.csproj
   --nologo --filter "FullyQualifiedName~DiskRestoreE2E"
   (env prefix below in §10). Expect 3/3. If RawHasGpt still fails, dump
   plan.Regions[0..3] and the image's stored chunks (inspect) and fix the grid
   union logic — do NOT weaken the asserts.
2. Full gate: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
   → expect "ALL GREEN" (45 tests).
3. Publish CLI: dotnet publish src/CLI/OpenReflect.CLI.csproj -c Release -r win-x64
   --self-contained true -o "F:\backup\windowsapps\installed\OpenReflect\cli"
   Then verify: F:\backup\windowsapps\installed\OpenReflect\cli\OpenReflect.CLI.exe
   disks  (lists disk 0 with 4 partitions).
4. Write F:\win11backup\orf-backup.ps1 per docs/DESIGN-v0.2-win11-workflow.md §6
   (rmback parity: admin check, Global\OpenReflectBackupWriter mutex, reparse-point
   walk guards, name-collision refusal, auto name backup_<ts>_<rand>, disk of C: as
   source, 2-second status line parsed from ##PROGRESS lines, mandatory full verify,
   verify-fail deletes the unverified new image, success cleanup of older .orf with
   unchanged checks, -PlanOnly). NEVER references F:\win11recovery.
5. Test orf-backup -PlanOnly, then decide the real run (§11 gate 6): disk 0 backup
   is ~45-60GB → F: has 480GB free → run it for real evidence; it also validates
   the exact user path end to end. Measure elapsed + size vs Macrium's 44.67GB.
6. Write F:\win11backup\orf-restore.ps1 per design §6: newest .orf by LastWriteTime,
   full verify, restore-disk --plan preflight, ensure offline env, write
   F:\win11backup\restore-task.json (volume-GUID image path, disk number+size,
   skipUnwritten default true, verifyAfter true), arm via bcdedit /bootsequence,
   print "ARMED — restart when ready...", NEVER reboot, -PlanOnly/-Disarm/-Force/
   -SafeWrite modes.
7. Build the WinRE offline env (design §5): mountvol-scan for \Recovery\WindowsRE\
   Winre.wim → copy Winre.wim+boot.sdi to C:\OpenReflectRestoreEnv\ → DISM mount →
   inject self-contained CLI folder \OpenReflect\ + offline-restore.cmd (scans
   drive letters for \win11backup\restore-task.json, sets
   DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, runs restore-disk --task-file ...,
   writes restore-done/failed marker, waits for keypress, wpeutil reboot only on
   keypress) + \Windows\System32\winpeshl.ini (LaunchApps → the cmd) → commit →
   remount read-only to validate files → BCD: bcdedit /set {ramdiskoptions}
   ramdisksdidevice partition=C: + ramdisksdipath \OpenReflectRestoreEnv\boot.sdi;
   create osloader entry mirroring WinRE (path \windows\system32\winload.efi,
   systemroot \Windows, detecthal Yes, winpe Yes, device/osdevice
   ramdisk=[C:]\OpenReflectRestoreEnv\orwinre.wim,{ramdiskoptions}).
   NEVER run bcdedit /bootsequence during testing. Validate entry with bcdedit /enum.
8. Full suite + build.ps1 green again after all script work.
9. Docs: CHANGELOG 0.2.0, README/USER-GUIDE (two scripts + offline restore flow),
   PERF.md real numbers, ROADMAP update, FEATURE-MATRIX delta; commit; push;
   GitHub release v0.2.0 with new portable zip + SHA256 (package via
   packaging/package.ps1 — note both package.ps1 and docs-check.ps1 need the
   $root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
   fix if they resolve wrong).
10. Final report to the user: the TWO script paths
    (F:\win11backup\orf-backup.ps1, F:\win11backup\orf-restore.ps1) + evidence
    table (tests, real backup run, VHD e2e) + explicit statement that restore was
    armed-tested but never executed and the machine was never rebooted.

## 8. OPERATIONS MANUAL (hard-won gotchas — follow exactly)
- Shell is Git Bash on Windows: $_ and \-escapes get mangled inline. Write
  PowerShell/python to a FILE and run it; use raw strings in python; re-check file
  encodings after python writes (a file was left binary-corrupt once).
- dotnet ONLY works with the env prefix (machine dotnet is gutted, MSB4236):
  export PATH="/f/backup/windowsapps/installed/scoop/apps/dotnet-sdk/current:$PATH"
  DOTNET_ROOT='F:\backup\windowsapps\installed\scoop\apps\dotnet-sdk\current'
  MSBuildSDKsPath='F:\backup\windowsapps\installed\scoop\apps\dotnet-sdk\current\sdk\10.0.401\Sdks'
  DOTNET_CLI_TELEMETRY_OPTOUT=1
- Tests run from repo root with `dotnet test tests/OpenReflect.Tests/...`.
- xunit assertion law: NEVER Assert.True(x.IsSuccess, x.Error?.ToString()) — the
  message argument evaluates .Error on success and throws. Use AssertSuccess(r).
  This bit three times.
- diskpart: GPT conversion auto-creates MSR as partition 1 — assign the LARGEST
  partition (test helper does this). Each diskpart invocation needs its own
  `select vdisk file="..."`; never re-attach an attached vdisk in one script.
- After raw device writes, Windows may serve stale partition info to newly attached
  VHDs — the E3 RawHasGpt asserts (raw LBA reads) are the truth, not Get-Partition.
- VSS: tests create+delete snapshots of C: (safe, read-only). If VSS is broken,
  backup-disk falls back to direct reads with a warning — for the REAL backup run
  VSS MUST be up (check service "Volume Shadow Copy" + "MS Software Shadow Copy
  Provider", and that no vssadmin list writers is in a failed state).
- The AlphaVSS native load needs Ijwhost.dll + AlphaVSS.x64.dll in
  bin\runtimes\win-x64\native — guaranteed by the DIRECT PackageReference; never
  set ExcludeAssets on it.
- User preferences: zero popups, no questions, real-time console progress, hates
  small-percentage talk — give absolute numbers (seconds, GB, %).

## 9. HOLDING SUB-AGENTS / EXTERNAL RESOURCES
None. All sub-agents are dead (rate-limited earlier in the session); the user
ordered solo continuation. No external resources held.

## 10. TOOLCHAIN & ENVIRONMENT PINS (never upgrade)
- .NET SDK 10.0.401 (scoop pin, see §8 env prefix). global.json pins 10.0.401.
- NuGet: ZstdSharp.Port 0.8.8, AlphaVSS 2.0.3 (+ AlphaVSS.Native.NetCore 2.0.3).
- Windows 11 (build 26100), PowerShell 5.1 default, diskpart/dism/chkdsk in PATH.
- Test command: dotnet test tests/OpenReflect.Tests/OpenReflect.Tests.csproj
  --nologo; official gate: powershell -File build.ps1 → "ALL GREEN".
- Git push: use stored git credential + GitHub API (NO gh CLI).
- Model law if ever dispatching agents: GLM-5.3-Flash (cheapest) — but solo is law.

## 11. DONE CHECKLIST (mission complete when ALL are green)
- [ ] E1+E2+E3 DiskRestoreE2E green (E3 includes RawHasGpt immediately-after-write
      AND after detach/attach, file-hash equality for every test file, GPT
      equality, unconfirmed-refusal, E4 skip-unwritten writes strictly less than
      zero-fill and files still equal)
- [ ] build.ps1 → "ALL GREEN" (45+ tests, 0 failed)
- [ ] CLI deployed at F:\backup\windowsapps\installed\OpenReflect\cli and `disks`
      works from there
- [ ] orf-backup.ps1 exists at F:\win11backup\orf-backup.ps1; -PlanOnly verified;
      REAL backup run of disk 0 into F:\win11backup completed with exit 0, full
      verify OK, elapsed + final size recorded and compared against Macrium's
      44.67 GB (faster/smaller claims backed by these numbers only)
- [ ] F:\win11recovery untouched throughout (verify by listing before/after)
- [ ] orf-restore.ps1 exists at F:\win11backup\orf-restore.ps1; -PlanOnly verified
      against the real backup (latest pick + verify + preflight report);
      -Disarm verified; NO reboot performed by the session; restore NEVER executed
- [ ] WinRE env built at C:\OpenReflectRestoreEnv with injected CLI verified by
      read-only remount; BCD osloader entry exists per bcdedit /enum; bootsequence
      NOT set at handoff time
- [ ] Real-machine safety gates proven: restore-disk refuses disk 0 from live
      Windows even with --confirm-device-write (E2)
- [ ] Docs updated (CHANGELOG 0.2.0, README, USER-GUIDE, PERF with measured
      numbers, ROADMAP), committed, pushed to GitHub, release v0.2.0 with portable
      zip asset + SHA256
- [ ] Final report outputs BOTH script full paths + evidence table; explicitly
      states no restore was run and no reboot happened
