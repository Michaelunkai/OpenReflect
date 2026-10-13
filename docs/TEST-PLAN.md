# Test Plan (S49) & Full Gate Results (S50–S52)

Suite: `tests\OpenReflect.Tests` — xUnit, net10.0-windows, 27 tests.
Official gate: `powershell -File build.ps1` (pins the healthy scoop SDK — machine dotnet is broken).

## Requirement → test traceability

Full matrix in docs/TRACEABILITY.md. Summary per domain:
- Format conformance (SH-10…16): FormatTests (9 tests) — roundtrip, golden index/manifest,
  compression, AES wrong-password, chain metadata, negative suite (magic/version/CRC/manifest/
  truncated/dirty-flag/missing-chunk).
- Engine (SH-01…09): CoreTests (7) — raw read, sparse full-image E2E, zero-region semantics,
  resume-after-abandon, incremental + differential chains, snapshot seam + VSS deferral,
  job-config validation.
- Verify/Restore (SH-17…24): RestoreVerifyTests (5) — tamper naming, byte-identical restore,
  incremental merge restore, encrypted roundtrip + clean wrong-password rollback, all safety gates.
- Ops (SH-25…31): OpsTests (6) — real Task Scheduler create/query/delete, headless job dry-run,
  CLI help/version/parse, CLI E2E create→verify→restore bytes-identical, GUI form instantiation,
  GUI service-path real backup.

## S50 — full gate (final run recorded)

`build.ps1` → build 9 projects OK, **tests: Passed! 27 / Failed: 0** (recorded in execution log).

## S51 — E2E matrix (executed)

| Scenario | Compress | Encrypt | Result |
|---|---|---|---|
| Full image → verify → restore | ✔ | ✖ | byte-identical (S38, S44) |
| Full image → verify → restore | ✔ | ✔ | byte-identical (S24) |
| Incremental chain → merge restore | ✔ | ✖ | equals mutated source (S40) |
| Differential vs root | ✔ | ✖ | stores only root-diff regions (S29) |
| Crash resume (abandon → resume → commit) | ✔ | ✖ | complete + readable (S23) |
| CLI end-to-end (create→verify→restore) | ✔ | ✖ | exit 0 × 4, byte-identical (S44) |

## S52 — failure-path suite (executed)

| Failure | Expected | Test |
|---|---|---|
| Corrupt chunk (bit flip) | verify fails, chunk named | S37 |
| Corrupt header CRC / magic / version | typed CorruptHeader/BadFormat/UnsupportedVersion | FormatTests.S30 |
| Index corruption | manifest mismatch refused | FormatTests.S30 |
| Missing parent in chain | ChainBroken | planner (RestoreVerifyTests.S40 negative path covered by design + S40) |
| Chain past incomplete image | ChainIncomplete | planner |
| Wrong password | WrongPassword, no partial writes, rollback | FormatTests.S28, RestoreVerifyTests.S41 |
| Unconfirmed restore | RestoreSafetyGateBlocked | S42, OpsTests.S44 |
| Physical-device target | RestoreSafetyGateBlocked (v0.1 lockout) | S42 |
| Undersized target | RestoreTargetTooSmall | S42 |
| Missing source (job) | SourceNotFound | OpsTests.S33 |
| Invalid job JSON fields | InvalidArgument / NotSupported | CoreTests.S19S33 |

## Defects found and fixed during the mission

1. GCM payload encrypted-then-plaintext-copied bug (FormatTests caught it — security-relevant).
2. FileStream leak on reader error paths (handle held → cleanup failures).
3. Reader key derivation before password assignment (ctor ordering).
4. Unsafe assert-message pattern in tests touching `.Error` on success results.
5. **Incident:** device-target restore reachable before gates complete → wrote to boot disk GPT;
   repaired from backup GPT; v0.1 now refuses ALL device targets (docs/RISKS.md §4).
