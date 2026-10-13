# OpenReflect — Architecture (S13)

Status: CONTRACT (implementers build against this; deviations require an ADR in this file)
Pinned TFM: net10.0 (libraries, CLI, tests), net10.0-windows (GUI, WinForms)
Solution: `OpenReflect.slnx` (9 projects). See `docs/OWNERSHIP.md` for module table and pinned NuGet versions.
Companion documents: `docs/FORMAT.md` (byte-level .orf contract), `docs/RISKS.md`, `docs/TRACEABILITY.md`, `docs/OWNERSHIP.md`.

---

## 1. Module responsibilities

| Module | Project | Responsibility | Depends on |
|---|---|---|---|
| Abstractions | `OpenReflect.Core.Abstractions` | Contracts only: interfaces, records, enums, `Result<T>`, error codes. Zero dependencies, zero logic. | — |
| Core | `OpenReflect.Core` | Orchestration engine: VSS snapshot lifecycle, dirty-block bitmap, fixed-size chunking, SHA-256 hashing, dedup decisions against `IChunkStore`, compression/encryption delegation, pipeline plumbing, job execution, file logger implementation, job-JSON loading. | Abstractions, Format |
| Format | `OpenReflect.Format` | The `.orf` file format: header/index/manifest/footer read+write, zstd chunk framing (ZstdSharp.Port), AES-256-GCM framing, PBKDF2 key derivation, CRC-32 verification, dirty-shutdown flag management. Knows nothing about VSS, disks, scheduling, or UI. | Abstractions |
| Restore | `OpenReflect.Restore` | Restore planning and execution: chain merge (root→newest chunk map), target validation, dry-run, safety gates (system-disk overwrite protection), raw write to target disk/volumes, post-restore verify hook. | Abstractions, Format |
| Verify | `OpenReflect.Verify` | Image and chain verification at three depths (structure / manifest / full hash). | Abstractions, Format |
| Schedule | `OpenReflect.Schedule` | Windows Task Scheduler integration (register/unregister/list/run-once), job persistence, retention pruning invocation. | Abstractions, Core (job execution handoff) |
| CLI | `OpenReflect.CLI` | Command front end: `create`, `restore`, `verify`, `schedule`, `jobs`. Parses args, loads job JSON, wires engines, prints `ProgressInfo` and `Result` errors. No business logic. | Core, Restore, Verify, Schedule, Abstractions |
| GUI | `OpenReflect.GUI` | WinForms front end: wizard (source→options→schedule→run), progress view, restore wizard with safety-gate confirmation dialog. No business logic. | Core, Restore, Verify, Schedule, Abstractions |
| Tests | `OpenReflect.Tests` | xUnit suite. Golden-vector tests for FORMAT.md, engine round-trips on sparse file fixtures, chain merge tests, safety-gate tests. | All libraries |

Rule: nothing above Abstractions may be referenced downward; Abstractions references nothing.

## 2. End-to-end dataflow (create image)

```
Physical disk (\\.\PhysicalDriveN)
   │  (1) snapshot
   ▼
VSS snapshot (AlphaVSS; fallback P/Invoke vssapi.dll — see RISKS R1)
   │  frozen, consistent read handle
   ▼
(2) Dirty-block bitmap (one bit per chunk_size region; region = 4 MiB default)
    • Full:   all regions dirty.
    • Inc/Diff: read parent chain index → regions with a newer chunk in chain are clean;
                region also clean if chain's per-region digest matches live re-hash
                (cheap path: hash only header 64 KiB of region when bitmap says clean).
   ▼
(3) Chunker — fixed 4 MiB chunks over the linear concatenation of volumes
    (volume order = ascending disk LBA; chunk logical offset = index × chunk_size)
   ▼
(4) Hash stage — SHA-256 per dirty chunk (pipelined, see §3)
   ▼
(5) Dedup stage — IChunkStore.TryLocateAsync(sha256):
    hit    → skip (region inherited from parent chain; nothing appended)
    miss   → continue
   ▼
(6) Compress — zstd (ZstdSharp.Port) level from ImageOptions (default 7); keep if smaller
   ▼
(7) Encrypt — AES-256-GCM if enabled; per-chunk 96-bit nonce = nonce_prefix(32b) || counter(64b LE),
    counter = chunk sequence number in THIS file; key from PBKDF2-HMAC-SHA256 (600k iters)
   ▼
(8) IImageWriter.AppendChunk — chunk record frames to destination .orf (see FORMAT.md §5)
   ▼
(9) Finalize — chunk index + SHA-256 manifest + footer written; dirty-shutdown flag cleared
```

Restore dataflow is the inverse: chain merge (§6.3) → per-region newest chunk → decompress → decrypt → verify SHA-256 → raw write at logical offset → (post-restore verify hook).

## 3. Threading model

- One `Channel<T>`-based producer/consumer pipeline per job. Stages connected by bounded channels (`Capacity = 4 × stage width`), backpressure via `WaitToWriteAsync`.
- Stage widths (v1):
  - Read stage: 1 (sequential disk reads; VSS snapshot handle is not thread-safe for parallel reads).
  - Hash stage: `min(Environment.ProcessorCount, 8)` parallel hashers keyed by chunk index (order restored downstream).
  - Compress/Encrypt stage: `min(Environment.ProcessorCount, 8)`.
  - Write stage: **exactly 1** — the .orf file is written by a single writer; chunk records are appended in ascending chunk-index order and the stored file offset of each record is recorded in the in-memory index.
- All stages are `async`; no `Task.Run` inside Format (it exposes pure functions); parallelism lives in Core only.
- Cancellation: the job's `CancellationToken` is passed to every stage. On cancel, stages drain bounded channels and the engine returns `Result.Fail(ErrorCode.Cancelled)`; the partial file is left with the dirty-shutdown flag SET and the writer is `AbandonAsync`-ed (footer never written).
- UI progress: engines accept `IProgress<ProgressInfo>?`; engines marshal through `Progress<T>`-safe callbacks — engines never touch UI threads. Progress emitted at most every 250 ms or 64 chunks, whichever first (decision D7).
- GUI thread model: WinForms UI thread owns all control mutation; background work via engines' async APIs only. CLI is single-threaded console + async engines.
- Locking: engines are single-job-per-instance. One `IImageEngine` instance = one active job. Sharing an instance across concurrent jobs is a programming error (exception policy, §4).

## 4. Error model

Two tiers.

**Tier 1 — recoverable/operational failures → `Result<T>` / `Result` with `Error(ErrorCode, Message, Detail)`.** Everything a user can plausibly react to: missing disk, access denied, wrong password, corrupt image, full destination, broken chain, safety gate rejection, cancelled job. Public engine interfaces (S15 stubs) return `Result`/`Result<T>` exclusively; they never throw for these cases.

**Tier 2 — programming errors and unrecoverable environment faults → exceptions.** Null args to non-nullable params, violated interface contracts (e.g., `ReadChunkAsync` on a reader that failed `OpenAsync`), out-of-memory, internal invariant breaks. These propagate uncaught to the front end's top-level handler (CLI: `catch (Exception ex)` → log + exit code 3; GUI: global handler dialog). Engines may log and rethrow.

**Exception policy rules:**
1. `OperationCanceledException` is caught ONLY at the engine API boundary and converted to `Result.Fail(ErrorCode.Cancelled)`. All internal code lets OCE propagate untouched.
2. Format layer returns `Result` for all I/O verification failures (bad magic, CRC mismatch, GCM tag failure); it throws only on contract misuse.
3. `Result.Value` throws `InvalidOperationException` if accessed while `!IsSuccess` — callers must branch on `IsSuccess` first (compiler-visible pattern shown in stub docs).
4. No `try { } catch { return null; }` anywhere. No exception swallowing in libraries; logging + swallow is allowed only inside fire-and-forget progress callbacks.

**Error codes** are the contract between modules and the UI. The authoritative list is `ErrorCode` in `src/Abstractions` (S15). Ranges: 1–9 argument/validation, 10–19 source access, 20–29 destination, 30–39 format, 40–49 crypto, 50–59 chain, 60–69 restore, 70–79 scheduling, 80 cancellation/lifecycle, 99 internal. UI maps codes to user-facing text; libraries never format user-facing prose beyond `Error.Message` (single-sentence, no exception dumps).

**Logging contract (decision D8, derives from S13 requirement):** `ILogSink.Write(LogLevel level, string component, string message, Exception? exception)`. Core ships a file-logger implementation: one `orb-run-<yyyyMMdd-HHmmss>.log` per job run under `%ProgramData%\OpenReflect\logs\` (or `--log-dir`), UTF-8, line format `<utc ISO-8601> [<level>] <component>: <message> [<exception>]`, flushed per line, no third-party logging dependency (decision D9). Front ends pass the sink into engines via options; libraries never configure logging themselves.

## 5. Configuration — job JSON schema (v1)

One JSON file per job, stored in `%ProgramData%\OpenReflect\jobs\<jobId>.job.json`. Schema version field is mandatory; readers reject `schemaVersion > 1` with `ErrorCode.NotSupported` and MUST ignore unknown properties (forward compatibility).

```json
{
  "schemaVersion": 1,
  "jobId": "6f0e2a5c-9d1a-4c3e-b7a2-2f8d1e5c9b01",
  "name": "Nightly system disk",
  "source": {
    "diskNumber": 0
  },
  "destination": {
    "directory": "E:\\backups\\win10",
    "fileNamePrefix": "sys"
  },
  "chain": {
    "kind": "incremental",
    "maxDeltaDepth": 7,
    "fullIntervalDays": 30
  },
  "options": {
    "chunkSizeBytes": 4194304,
    "compression": "zstd",
    "compressionLevel": 7,
    "encryption": "aes256gcm",
    "kdfIterations": 600000,
    "passwordEnvVar": "OPENREFLECT_JOB_6F0E2A5C"
  },
  "schedule": {
    "kind": "daily",
    "time": "03:00",
    "daysOfWeek": [],
    "runMissedIfMachineAsleep": true
  },
  "retention": {
    "keepFullChains": 4,
    "pruneOrphanDeltas": true
  },
  "verifyAfterWrite": true,
  "logDirectory": "C:\\ProgramData\\OpenReflect\\logs"
}
```

Constraints (validated by Core; violations → `ErrorCode.InvalidArgument`):
- `schemaVersion` = 1 (integer, required).
- `source.diskNumber` ≥ 0 and present on machine.
- `chunkSizeBytes` ∈ { 512 KiB, 1 MiB, 2 MiB, 4 MiB, 8 MiB, 16 MiB } (powers of two, 0x80000..0x1000000); default 4 MiB. Pinned in header; cannot change within a chain.
- `compression` ∈ {"none","zstd"}; `compressionLevel` 1–22 when zstd, else 0.
- `encryption` ∈ {"none","aes256gcm"}; when aes256gcm, `passwordEnvVar` is REQUIRED — passwords are never stored in the job file (decision D10); Core reads the env var at run time, errors `ErrorCode.InvalidArgument` if unset.
- `kdfIterations` ∈ [100000, 5_000_000]; default 600000. Stored in header; restore of an image only needs whatever the header says.
- `chain.kind` ∈ {"full","incremental","differential"}; `maxDeltaDepth` ≥ 1 (engine forces a full when depth would exceed it); `fullIntervalDays` ≥ 1.
- `schedule.kind` ∈ {"disabled","daily","weekly","monthly","logon","idle"}; `daysOfWeek` only valid for weekly (subset of ["mon".."sun"]); `time` "HH:mm" local.
- `retention.keepFullChains` ≥ 1; pruning deletes whole chains only (root+its deltas), never individual chunks.

`fileNamePrefix` → produced files: `<prefix>-<yyyyMMdd-HHmmss>-<full|inc|diff>.orf`.

## 6. Key design decisions (ADR summary)

- **D1 — Fixed-size chunking (not content-defined).** Disk images are consumed by fixed logical offsets on restore; CDC buys nothing for whole-disk imaging and breaks the region-inheritance dedup model. Fixed 4 MiB regions give O(1) incremental merge. Rejected: FastCDC/Rabin (restore-path complexity, no dedup win on block devices).
- **D2 — Dedup via chain index lookup, no global content store.** v1 dedup scope = the image's chain. A cross-chain pool is a v2 idea (RISKS deferral list).
- **D3 — Format is a separate assembly from the engine** so Verify/Restore and third-party tooling can read .orf without VSS/Core. Enables format-level golden tests without Windows disks.
- **D4 — Encryption/compression framed per chunk** (FORMAT.md §5/§7): a single corrupted byte damages one chunk, not the archive; enables per-chunk parallel verify.
- **D5 — Dirty-shutdown flag + finalize-ordered writes** (index → manifest → footer → flag clear, each fsynced): crash at any point yields a detectably incomplete but structurally navigable file. Salvage reads are possible; Verify reports `ImageIncomplete`.
- **D6 — `Result`-first public APIs** with `ErrorCode` taxonomy (§4) so CLI/GUI need no exception mapping layer.
- **D7 — Progress throttling 250 ms/64 chunks** keeps Channels unblocked on fast NVMe without starving UI.
- **D8/D9 — Minimal `ILogSink` + built-in file logger, no logging framework.** Zero-dep Abstractions stays clean; ops logs stay greppable. Rejected: Microsoft.Extensions.Logging (dependency weight unjustified for one sink).
- **D10 — Passwords via environment variable or interactive prompt; never in job JSON or logs.**
- **D11 — AlphaVSS primary VSS path, native `vssapi` P/Invoke fallback** behind an internal `ISnapshotProvider` seam so R1 materializing does not ripple through Core (see RISKS R1).
- **D12 — Restore safety gates are part of the contract**, not UI: `IRestorer` refuses (Result-fails, `ErrorCode.RestoreSafetyGateBlocked`) to write to a disk marked bootable/system unless `RestoreOptions.AllowOverwriteSystemDisk` is true AND the plan was produced with matching target disk. GUI adds a confirmation dialog on top; CLI requires `--i-understand-this-wipes-the-target`.

## 7. Failure and recovery behavior

| Failure | Behavior |
|---|---|
| Crash mid-backup | Partial .orf with dirty flag set, no footer. Next run detects it via `IImageReader.OpenAsync` (`IsComplete=false`); user may `verify --salvage` (v1: report only) or delete. Chain unusable until the flagged image is removed; engines refuse to append to a chain whose newest image is incomplete (`ChainIncomplete`). |
| Destination full mid-write | Writer Result-fails `DestinationFull`; same dirty-flag semantics as crash; job logged, chain not extended. |
| Wrong password | `ErrorCode.WrongPassword` on first GCM tag check (chunk 0); no partial writes on restore (dry-run plan succeeds, execute fails fast before target is touched). |
| VSS failure | `ErrorCode.VssFailure` with Detail from provider; job offers non-VSS raw read path ONLY for non-system disks (explicit `allowRawRead` option flag, default false). |
| Verify failure | `VerifyResult.Success=false` with per-chunk `FailedChunks`; engines do NOT auto-delete anything. |
| Task Scheduler missing/job deleted externally | `IScheduler` returns `Result` failures; Core re-registers on next GUI/CLI `schedule apply`. |

## 8. What is explicitly NOT in v1

See `docs/RISKS.md` §3 (deferral list): split archives, cross-chain dedup pool, Linux/BCD boot-menu restore environment, WinPE builder, ransomware protection, Thunderbird/Exchange granular restore — v1 is engine + format + CLI + basic GUI + scheduler, functionally equivalent to Macrium Reflect Free's core imaging path.
