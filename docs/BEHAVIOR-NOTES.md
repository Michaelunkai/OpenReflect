# Macrium Behavior Notes (S05) — public-doc facts informing OpenReflect

Sources: official pages listed in FEATURE-MATRIX.md; local install observations (analysis/ARTIFACTS.md).
Own-words summaries — no copied text.

## Backup semantics

- Image types: **full / incremental / differential**; incrementals reference their predecessor,
  differentials reference the base (official legacy feature page + knowledgebase model).
- Verification: images embed integrity data and can be verified on demand or as part of the backup
  (kb docs; standalone `MRVerify.exe` exists in the local install).
- Encryption: AES-256 offered at backup time (official feature pages).
- Scheduling: backup **plans** executed by the persistent Macrium Service (service observed running
  on the analysis machine; official docs describe plans + retention).
- Used-sector vs forensic: imaging can skip unused sectors for speed/size; forensic captures
  everything including deleted-data remnants (kb docs).

## Consequences for OpenReflect's design

1. Chain model (full → inc/diff) with region-level change detection — matches SH-02/03/07.
2. Verification as a first-class, standalone-capable operation — SH-17/18, `verify` CLI.
3. Passwords supplied at creation time; never embedded in schedules — SH-28 (D10).
4. Plans run headless without a UI session — SH-26 (job JSON + Task Scheduler).
5. Retention/consolidation is a service-level concern — designed (ARCHITECTURE.md §5) but UI
   pruning deferred (ROADMAP).
