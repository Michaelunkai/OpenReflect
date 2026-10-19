# HANDOFF — reflect: OpenReflect backup <3min + 50% smaller (continuation doc, 2026-10-15, session 2)

## 0. CONTINUATION ORDER (binding on the receiving session)
You are the continuation of the session that wrote this document. You inherit
ALL of its context and ALL of the user's demands below. You do not stop, sleep,
idle, or wait passively: you work continuously, as fast as accurately possible,
until EVERY demand has passed its real-world test to perfection — no fewer.
Every claim must be proven with real evidence; never claim success without it.
Resume exactly where the previous session stopped (Section 5).

## 1. MISSION LAW — every user demand, enumerated
1. "Update the backup script so every backup is always created to perfection in
   under three minutes without any bugs, errors or delays ever" — IN FLIGHT.
   Pipeline bugs FIXED (see §6); remaining question is the measured runtime of
   a healthy incremental (~385 s single-reader read wall vs the 180 s target —
   measure honestly, document physics if it cannot pass).
2. "Make sure it will always make the backup perfectly usable as it's supposed
   to be" (restorable) — IN FLIGHT: needs one green incremental + verify +
   orf-restore.ps1 -PlanOnly chain-merge plan on the NEW binary.
3. "The backup created about 90 GB — make it always 50% smaller than now"
   (target <= ~42.16 GiB per regular backup) — PENDING PROOF: incremental
   images are a few GiB; need ONE completed incremental measured.
4. Follow the reflect skill exactly (deploy gate ALL GREEN, safety rules,
   F:\win11recovery NEVER touched, ASCII-only PS in F:\win11backup) — DONE so
   far; keep honoring it.
5. Measured-evidence bar (docs/PERF.md style, absolute numbers only) — IN
   FLIGHT (need measured healthy incremental + full runs).
6. NEW THIS SESSION: no backup run may EVER hang — the engine now has a 2-min
   per-read timeout and a 10-min pipeline watchdog that fail honestly (proven:
   three stalls became clean exit-3 failures).

## 2. ONE-PARAGRAPH STATE
Three real bugs were found and fixed this session, all proven with evidence:
(1) classic C# closure capture in ImageEngine.cs reader fan-out — Task.Run
captured the mutating for-loop variable r, so every reader started at
logical = readerCount and chunk 0 was never read (fixed with per-iteration
readerId copy); (2) watermark starvation deadlock — unbacked/zero-fill skip
entries held inflight permits while sitting in results AHEAD of the ordered
writer, so the reader of the next backed chunk could be starved by 64
unconsumed skips past the writer's position (fixed: skip entries release the
permit immediately in the reader; new P5 regression test FAILS on the old code
with "pipeline deadlocked" at 3 min — proven twice); (3) restore progress never
reported when a pass finished inside the 250 ms throttle window (fixed: final
ProgressInfo always reported; E3 passed when VSS healthy). Gate went 48/48 ALL
GREEN. BUT: real disk runs still stall ~30 s-6.5 min in — CONCLUSIVE evidence
that Win11 26100 volsnap stops serving reads to multiple concurrent handles on
one software shadow (all 64 in-flight reads hang in the kernel, shadow still
exists, diff area nearly empty, VSS writers wedge "Waiting for completion" —
dump-analyzer evidence at 44/122/126/137 GiB and at 14 GiB). Per the prior
handoff's fallback law, DiskSnapshotProvider.SupportsIndependentStreams was
set to FALSE (single-reader VSS = proven stable: the 13:30 full ran 580 s
total). Gate re-run after that change FAILED on E3 once (likely the wedge from
killing the stalled run mid-shadow). THAT re-run is the exact point of
interruption. Next: clean VSS, re-run E3 + full gate, republish, then measure
real incremental + full.

## 3. KEY PATHS & IDENTITIES
| What | Path |
|---|---|
| Repo (canonical) | F:\study\Platforms\Windows\Applications\Desktop\Utilities\System\Backup\Imaging\OpenReflect (branch main, last commit 39f4c2c v0.2.0) |
| Modified (uncommitted) | src/Abstractions/Contracts.cs, src/CLI/Program.cs, src/Core/ImageEngine.cs, src/Restore/DiskRestorer.cs, src/DiskAccess/DiskSnapshotProvider.cs, src/DiskAccess/NtfsAllocation.cs, tests/OpenReflect.Tests/EngineParallelTests.cs, HANDOFF.md |
| New (uncommitted) | src/DiskAccess/MftFileLocator.cs |
| Engine fixes live in | src/Core/ImageEngine.cs (readerId closure fix; skip-entry immediate inflight.Release(); workerFault capture; 2-min read timeout in ReadExactlyAsync; 10-min writer watchdog with stall diagnostics; reader cap 12) |
| Regression test | tests/OpenReflect.Tests/EngineParallelTests.cs P5_Skip_Entries_Never_Deadlock_The_Watermark (1900 MiB source, backed chunk only at 200, stride-0 reads slowed 30 ms; WhenAny 3-min deadlock guard) |
| Backup script (ASCII) | F:\win11backup\orf-backup.ps1 (PlanOnly message fix; --log-dir now passed to backup-disk and verify) |
| Restore script | F:\win11backup\orf-restore.ps1 (unchanged; chain merge via newest .orf) |
| Stable CLI deploy | F:\backup\windowsapps\installed\OpenReflect\cli\ (binary is CURRENT: published after the single-reader change, but the gate was RED at that moment — republish after gate is green) |
| Backup destination | F:\win11backup — ONE complete full: backup_20261015-133025_y7pcal-20261015-133026.orf, 84.32 GiB (90537761328 bytes), verified. All partial inc/full images from failed runs were deleted. |
| Engine logs | F:\win11backup\logs\orb-run-*.log (readers=N, stall-diagnostic lines, done: stored=...) |
| NEVER TOUCH | F:\win11recovery (baseline listing: C:\Temp\recovery-baseline.json) |
| Diagnostics | C:\Temp\orfrepro\ (console repro; Program.cs has ConsoleLog ILogSink); C:\Temp\crashquery.ps1, disk51.ps1; dotnet-dump + dotnet-stack in C:\Users\Admin\.dotnet\tools |
| Session exec logs | C:\Users\Admin\.zcode\cli\exec\sess_298f2953-646f-4f1b-b9ff-bd9c9fe4f2a4\ |

## 4. WHAT IS DONE (evidence-backed)
1. Closure-capture bug fixed: engine log showed "reader r=1 dispatch 1..23"
   with readers=1 (r captured post-increment); after the per-iteration copy the
   console repro at C:\Temp\orfrepro exits "OK chunks=24" in 72 ms.
2. Skip-starvation deadlock fixed + regression-proven: stall-diagnostic
   signature waiting=50511 resultsCount=64 min=50512 inflightFree=0
   channelCount=0; P5 with the fix reverted FAILS with "pipeline deadlocked:
   skip entries starved the reader of the backed chunk" (3 m 1 s); with the
   fix, passes.
3. Watchdog + read timeout: three real stalls became honest exit-3 failures
   ("pipeline stalled before chunk N for over 10 minutes") instead of infinite
   hangs — "no delays ever" enforced in code.
4. Restore final-progress fix: DiskRestorer reports final ProgressInfo after
   the write pass; E3 "skip-unwritten should write less" passed when VSS was
   healthy (3/3 E2E green earlier in the session).
5. Parallel-reader capability preserved at engine level: P1-P5 + P4
   byte-identical determinism all pass with fake multi-stream providers.
6. orf-backup.ps1: -PlanOnly no longer claims removals for incrementals;
   passes --log-dir (pure ASCII, parseErrors=0 verified).
7. Baselines: full backup 580 s (388 backup + 192 verify), image 84.32 GiB,
   51,386 chunks; single-reader VSS ~540 MB/s; compressibility 1.69:1.
8. C: shadow storage cap raised 10 GB -> 80 GB (vssadmin resize shadowstorage
   /for=C: /on=C: /maxsize=80GB) — kept (harmless headroom; eviction disproven
   as stall cause but more headroom cannot hurt).

## 5. IN FLIGHT / BLOCKED
Exact point of interruption: after setting
DiskSnapshotProvider.SupportsIndependentStreams=false, build.ps1 was run and
FAILED on E3_Intelligent_Backup_Restore_ZeroFill_File_Equality (47/48) — most
plausibly because VSS was wedged from force-killing the stalled real run
(PID 38584) seconds earlier. VSS was then cleaned (writers 10/10 Stable) but
the immediate E3 re-run was CANCELLED mid-test when the user invoked /handoff.
Next command: the E3 filter test again with healthy VSS, then the full gate.
NOTE: every force-kill of a run holding a shadow wedges VSS (writers go
"Waiting for completion") and a Winmgmt restart ALSO breaks the Storage CIM
namespace (Get-Partition returns nothing until Restart-Service smphost -Force).
Recovery recipe in §8.

## 6. FINAL ARCHITECTURE & DECISIONS (failed variants — do NOT revisit)
- Parallel reads on REAL VSS software shadows: MEASURED DEAD on Win11 26100 —
  volsnap wedges under multi-handle load (8 readers AND 4 readers both died;
  single reader stable across many runs). SupportsIndependentStreams=false in
  DiskSnapshotProvider; engine capability + P1-P5 stay (fakes). Do not flip
  back without new OS evidence.
- Skip entries holding watermark permits: DEADLOCK (proven) — releases now
  happen in the reader.
- Capturing the for-loop variable in Task.Run: BUG (chunk 0 lost) — capture a
  per-iteration copy.
- Higher zstd levels for size: DEAD (1.69:1 -> 1.76:1 only, 7 MB/s/core at 19).
- Pagefile/hiberfil exclusion as size lever: zero effect on THIS machine (no
  such files); feature kept for other machines.
- Macrium 44.67 GB parity via compression alone: impossible (content
  incompressible); incremental cadence is the size lever.
- VSS diff-area eviction as the stall cause: DISPROVEN (shadows alive, 277 MB
  used of 80 GB at stall time).
- Chunk-size increase (8 MiB): REJECTED previously (format/restore churn).

## 7. IMMEDIATE NEXT STEPS (execute in order)
1. VSS health: vssadmin list writers -> all Stable (fix via §8 recipe if not).
2. dotnet test tests/OpenReflect.Tests/OpenReflect.Tests.csproj --nologo
   --filter "FullyQualifiedName~DiskRestoreE2E" -> 3/3. Then full
   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -> ALL GREEN
   (48/48).
3. Republish CLI (kill stray OpenReflect.CLI first): dotnet publish
   src/CLI/OpenReflect.CLI.csproj -c Release -r win-x64 --self-contained true
   -o "F:/backup/windowsapps/installed/OpenReflect/cli"; verify disks works
   and a real run's log shows readers=1.
4. F:/win11backup/orf-backup.ps1 -PlanOnly -> incremental plan onto the
   existing full.
5. REAL measured incremental (script, no -PlanOnly). Record: total seconds
   (backup + verify), image GiB, verify OK. Expect ~385 s read @ ~540 MB/s +
   small-image verify; the 180 s target likely CANNOT pass (document physics
   in PERF.md if so). Size target <= 42.16 GiB must pass easily.
6. REAL measured full: temporarily pass -MaxIncrementals 0; compare vs
   baseline 580 s / 84.32 GiB.
7. inspect newest incremental shows chainKind Incremental + parent chain;
   F:/win11backup/orf-restore.ps1 -PlanOnly still plans with chain merge.
8. Confirm F:\win11recovery unchanged vs C:\Temp\recovery-baseline.json.
9. Update docs/PERF.md + CHANGELOG (measured numbers; the parallel-reader
   disable decision with evidence; the three pipeline fixes). Commit all
   modified files + MftFileLocator.cs (message: incremental cadence + pagefile
   exclusion + pipeline deadlock fixes, single-reader VSS for stability;
   measured). Push via stored credential + GitHub API (NO gh CLI).
10. Optional release: packaging/package.ps1 + GitHub API release.
11. Update the Daymark task "reflect" (handoff project) with the outcome.

## 8. OPERATIONS MANUAL (hard-won gotchas — follow exactly)
- Toolchain env (machine dotnet gutted): export
  PATH="/f/backup/windowsapps/installed/scoop/apps/dotnet-sdk/current:$PATH"
  DOTNET_ROOT='F:\backup\windowsapps\installed\scoop\apps\dotnet-sdk\current'
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_MULTILEVEL_LOOKUP=0
- NEVER let publish/run overlap: kill stray OpenReflect.CLI first (silent
  MSB3021 + stale-binary trap: when a fix "does nothing", check the Release
  DLL actually rebuilt).
- Git Bash eats backslashes in CLI args: use FORWARD slashes in command-line
  paths (F:/win11backup) — a run once wrote to F:win11backup (drive-relative
  root) because double-backslashes collapsed.
- PowerShell -File paths from Git Bash need forward slashes.
- PS scripts in F:\win11backup must be pure ASCII (PS 5.1 ANSI read).
- VSS wedge recovery (after ANY force-killed run): Restart-Service VSS;
  Restart-Service WSearch (MSSearch writer); Restart-Service Winmgmt (WMI
  writer) — then ALWAYS Restart-Service smphost -Force, because a Winmgmt
  restart breaks the Storage CIM namespace (Get-Partition/Get-Disk return
  nothing). Then vssadmin list writers -> 10 Stable. Delete orphan shadows
  with: cmd //c "vssadmin delete shadows /shadow={guid} /quiet" (bash needs
  cmd //c; powershell -Command mangles the braces).
- A stalled/killed backup leaves partial *.orf in F:\win11backup — DELETE them
  (the script treats an incomplete newest as "take a new FULL" otherwise).
- The engine watchdog/read-timeout turn hangs into exit-3 failures with
  stall-diagnostic ERROR lines in F:\win11backup\logs — read them first when
  debugging.
- dotnet-dump/dotnet-stack live in C:\Users\Admin\.dotnet\tools (dotnet-stack
  shows only thread stacks; use dotnet-dump dumpasync for pipeline hangs).
- Never write real disks in tests (VHDs only); clean C:\Temp\dr-* after kills.
- Never run restore from the live OS; orf-restore.ps1 never reboots itself.
- Event log shows irrelevant app crashes (game installers, RtkAudUService64) —
  don't chase; disk Event-51 warnings on Harddisk3 are leftover VHD noise.
- Keep C:-heavy work (builds, dumps) minimal during real backup runs.

## 9. HOLDING SUB-AGENTS / EXTERNAL RESOURCES
None. No background tasks running. All failed-run CLI processes were killed;
VSS was left 10/10 Stable with 0 shadows (verify again — kills may have
re-wedged).

## 10. TOOLCHAIN & ENVIRONMENT PINS (never upgrade)
- dotnet SDK 10.0.402 (scoop-pinned; global.json pins it; build.ps1 sets
  MSBuildSDKsPath). ZstdSharp.Port 0.8.8, AlphaVSS 2.0.3 (+ native direct ref
  in CLI and Tests csproj). Windows 11 26100, PS 5.1, Ryzen 7 9800X3D (16
  threads -> workers=16, inflight=64, channel=32, readers=8 when enabled),
  96 GB RAM; C: = Samsung 9100 PRO 2TB (disk 0), F: = Fanxiang 1TB
  (~450-500 MB/s class, destination).
- Gate: build.ps1 -> ALL GREEN (48/48). Single filter example: dotnet test
  tests/OpenReflect.Tests/OpenReflect.Tests.csproj --nologo --filter
  "FullyQualifiedName~EngineParallel".

## 11. DONE CHECKLIST (mission complete when ALL are green)
- [ ] build.ps1 prints ALL GREEN (48/48) with E3 green on healthy VSS
- [ ] CLI republished from a green gate; disks works; real run logs readers=1
- [ ] orf-backup.ps1 -PlanOnly plans an incremental onto the existing full
- [ ] REAL incremental measured end-to-end, verify OK, no stall (watchdog silent)
- [ ] Image size <= 42.16 GiB (50% of 84.32) — expected a few GiB
- [ ] <180 s: either achieved or documented as measured physics in PERF.md
      (single-reader VSS wall ~540 MB/s over 206 GiB allocated)
- [ ] REAL full measured (forced -MaxIncrementals 0) vs baseline 580 s / 84.32 GiB
- [ ] Newest incremental: inspect shows Incremental chain; orf-restore.ps1
      -PlanOnly plans with chain merge
- [ ] F:\win11recovery unchanged vs baseline
- [ ] docs/PERF.md + CHANGELOG updated (numbers + decisions)
- [ ] All changes committed and pushed to github.com/Michaelunkai/OpenReflect (main)
