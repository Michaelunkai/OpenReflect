# Requirement → Component Traceability (S18)

SH ids from docs/SPEC.md. Test evidence references the xUnit suite (27 tests, all green — docs/TEST-PLAN.md).

| SH | Component | Test evidence |
|---|---|---|
| SH-01 full | Core.ImageEngine + Format.OrfWriter | CoreTests.S24, OpsTests.S44 |
| SH-02 incremental | Core.ImageEngine (region dedup) | CoreTests.S29 |
| SH-03 differential | Core.ImageEngine (root reference) | CoreTests.S29 |
| SH-04 used-sector | Core.ImageEngine (zero skip) + Restore (zero-fill) | CoreTests.S22, RestoreVerifyTests.S38 |
| SH-05 snapshot seam | Core.SnapshotProviders | CoreTests.S20 |
| SH-06 chunk sizes | Format.OrfWriter (shift 19..24) | FormatTests.S25 |
| SH-07 chain dedup | Core.ImageEngine vs IImageReader hashes | CoreTests.S29 |
| SH-08 resume | Format.OrfWriter.ResumeAsync | CoreTests.S23 |
| SH-09 dirty flag | Format (flags bit0) + Restore (ChainIncomplete) | FormatTests.S30, Restore planner |
| SH-10 open format | docs/FORMAT.md + Format assembly | FormatTests.S25/S26 |
| SH-11 CRC+manifest | Format.OrfPrimitives | FormatTests.S26/S30 |
| SH-12 per-chunk SHA | Format records | FormatTests.S25, CoreTests.S24 |
| SH-13 compression | Format.Transforms (deflate) | FormatTests.S27 |
| SH-14 AES-256-GCM | Format.Transforms + PBKDF2 | FormatTests.S28, RestoreVerifyTests.S41 |
| SH-15 corruption detection | Format.OrfReader | FormatTests.S30 |
| SH-16 forward compat | Format readers skip unknown; JobConfig ignores unknown keys | FormatTests.S30, CoreTests.S19S33 |
| SH-17 verify depths | Verify.Verifier | RestoreVerifyTests.S37 |
| SH-18 failing chunks named | VerifyResult.FailedChunks | RestoreVerifyTests.S37 |
| SH-19 chain-merge restore | Restore.Restorer.PlanAsync | RestoreVerifyTests.S40 |
| SH-20 dry-run | RestoreOptions.DryRun | RestoreVerifyTests.S42, OpsTests.S44 |
| SH-21 confirm + device lockout | Restorer gates (incident hardening) | RestoreVerifyTests.S42 |
| SH-22 target size/lock | Restorer | RestoreVerifyTests.S42 |
| SH-23 wrong password fail-fast | Format GCM + Restorer rollback | RestoreVerifyTests.S41 |
| SH-24 byte-identical | Restorer | RestoreVerifyTests.S38/S40, OpsTests.S44 |
| SH-25 Task Scheduler | Schedule.SchtasksScheduler | OpsTests.S32 |
| SH-26 headless jobs | Schedule.JobRunner | OpsTests.S33 |
| SH-27 job validation | Core.JobConfig | CoreTests.S19S33 |
| SH-28 password policy | JobConfig + ImageOptions.PasswordProvider | CoreTests.S19S33, RestoreVerifyTests.S41 |
| SH-29 CLI | CLI.Program | OpsTests.S43/S44 |
| SH-30 GUI tabs | GUI.MainForm | OpsTests.S45 (instantiation), S47 (service path) |
| SH-31 progress+tray | MainForm Progress<>/NotifyIcon | S45 smoke (build+launch) |
| SH-32 GUI confirm dialog | MainForm restore flow | code + S45 |
| SH-33 run logs | Core.FileLogSink | CoreTests (log wiring), manual |
| SH-34 typed errors | Abstractions.Result/ErrorCode | exercised by every suite |
| SH-35 build gate | build.ps1 | S34 run (docs/TEST-PLAN.md) |
| SH-36 packages | packaging/package.ps1 | S35/S36 staging |
| SH-37 clean-room | analysis/CLEANROOM.md | S12 |
| SH-38 honest parity | docs/PARITY.md | S53 |
| SH-39 ≥1GB perf | docs/PERF.md | S54 |
| SH-40 suite green | docs/TEST-PLAN.md | S50 |
