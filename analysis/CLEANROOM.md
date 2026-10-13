# Clean-Room Audit (S12) — verdict: PASS

Date: 2026-10-13. Auditor: project lead (final sweep re-runs the checks below).

## Checks

1. **No Macrium binaries in the repo** — `git ls-files` contains only source/docs/tests; the
   `analysis/` directory holds text evidence only (byte counts, file names, versions).
2. **No copied code** — OpenReflect was written from scratch in C#/.NET against a public, documented
   open format (`.orf`, docs/FORMAT.md). Macrium's `.mrimg` format was **not** reimplemented; no
   decompiled code was used as source. REA produced behavioral facts only (analysis/ARCH-FACTS.md).
3. **No copied assets or distinctive text** — all UI strings, docs and comments are original.
   grep for 'macrium' in the repo hits only docs referencing the name nominatively for parity
   context (NOTICE, PARITY.md, ROADMAP.md, this file).
4. **No Macrium artifacts on disk as part of the project** — analysis read the local install in
   place; nothing was copied out.

## Standing rules

- Any future REA work on Macrium artifacts stays read-only, in place, facts-to-text only.
- The `\.orf` format is OpenReflect's own documented format — compatibility with `.mrimg` is
  neither claimed nor attempted (would require copying proprietary format internals).
