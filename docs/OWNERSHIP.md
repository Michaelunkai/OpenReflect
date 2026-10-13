# OpenReflect — Module Ownership & Dependencies

## Pinned dependencies (v0.1.0)

| Package | Version | Where | Why |
|---|---|---|---|
| *(none)* | — | Abstractions | Zero-dep contract assembly (ARCHITECTURE.md rule) |
| ZstdSharp.Port | **deferred to 0.2** | Format | v0.1 uses built-in DEFLATE framing (ADR-13 in ARCHITECTURE.md); codec is self-describing via record flag |
| AlphaVSS | 2.0.x | Core | VSS snapshot orchestration (netstandard2.0, consumable from net10.0). Fallback: P/Invoke `vssapi` behind `ISnapshotProvider` seam |
| Microsoft.Win32.TaskScheduler | **not used** | Schedule | v0.1 wraps `schtasks.exe` (zero deps, scriptable, testable); COM wrapper deferred |
| xunit / xunit.runner.visualstudio | template | Tests | Standard |

Rule: any new dependency requires an entry here with justification.

## Module ownership (who may edit what)

| Path | Owner module |
|---|---|
| `src/Abstractions/**` | contracts only — changes require updating all consumers |
| `src/Format/**` | .orf read/write, CRC, crypto framing |
| `src/Core/**` | engine pipeline, VSS, job JSON, file logger |
| `src/Verify/**` | verification depths |
| `src/Restore/**` | restore planning + safety gates + raw write |
| `src/Schedule/**` | schtasks integration + job runner |
| `src/CLI/**`, `src/GUI/**` | front ends only — no business logic |
| `tests/OpenReflect.Tests/**` | `<Module>*Tests.cs` files |
| `docs/*.md` | see file headers |
