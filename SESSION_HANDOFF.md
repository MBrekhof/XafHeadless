# Session handoff — 2026-09-10

**State: clean.** `master`, working tree clean after the final commit, no branches besides `master`. All
hosts and the smtp4dev container stopped at the end of the session.

## What happened

A Codex review of the whole repo, then a fix round on what it found.

1. **Codex reviewed `e02b29c`**: 24 findings. Verdict "not ready for multi-tenant deployment" — true, but the
   repo already says so in `docs/DEVIATIONS.md`; the demo has one tenant by design. The useful payload was
   the correctness batch nobody had written down. Each candidate was verified in the code before carding
   (TEST-002's lesson: no phantom findings).
2. **Nine cards minted** (1605–1613): BUG-010…015, LOOKUP-003, TEST-004 in P2; SEC-003 in P3 as the single
   "if headless ever goes real" production-gap note.
3. **Eight fixed the same day**, one commit per card, all in `docs/DONE.md` with commit SHAs. Codex reviewed
   the fix range for correctness only: seven commits clean, two test findings on TEST-004, fixed in
   `81b5baf`. The eight cards are in **Review awaiting your Confirm Done**.
4. **The "three environmental failures" were the tests.** Since 2026-08-09 the handoff said the
   `JobExecutionRecord reaches Success` family fails with an optimistic-concurrency clash and not to chase
   it. TEST-004 keyed the JobServer tests on their own run's correlation id instead of "the latest record on
   the shared catalog" and all 20 passed, twice. The auto-memory note about this is corrected.

## Where to pick up

1. **Confirm Done on 1605–1612** (a click each; conclusions cite the commits).
2. **`RPT-001` (1230) and `CRUD-002` (1235)** were already in Review from 2026-08-09 and still are.
3. **SEC-003 (1613)** is Backlog on purpose. The README now says what is tested (one tenant, one admin).
4. Everything else in `TODO.md` is as the 2026-08-09 handoff left it: `FILE-001`, `EXPORT-001`, `EDIT-002`,
   `LOOKUP-002`, `RPT-002`, `GRID-006`, `GAP-010` are the list/detail/report-path cards ARCH-001 cares about.

## Traps worth knowing before you run anything

- **`Api.Tests`, `JobServer.Tests` and `E2E` are integration suites against running hosts.** Api `:5200`,
  Web `:5220`, JobServer `:5300`, plus the **smtp4dev** container (`docker start xafheadless-smtp`). Only
  `Components.Tests` runs standalone. Start the hosts from the built exes with `--urls`, and run the test
  projects with `--no-build` while a host is up — otherwise the build tries to overwrite the running exe and
  fails with MSB3027.
- **`JobExecutionRecord` gained a nullable `CorrelationId` column.** Both hosts schema-update on mismatch
  (Api `DatabaseVersionMismatch`, JobServer `HostDatabaseInitializer`), verified live this session.
- **No known-environmental failures remain.** If a JobServer test fails now, chase it.
- **The tenant data lives in `OutlookInspiredDemo_company1`, not the `XafHeadlessDemo` host catalog.**
- **A green suite is not a rendered page.** The LOOKUP-003 screenshot is what caught that the first version
  of its E2E could pass with the bug present (it could pick the order's own employee).

## Current numbers

`Components.Tests` 114/114 · `Api.Tests` 85/85 · `JobServer.Tests` 20/20 · `E2E` 21/21 · solution builds
0 warnings.

## Reference

- `docs/DONE.md` — the round's entry is at the top, one bullet per card with its SHA.
- Codex session for the original review: `codex resume 01a08bea-44fa-74f0-88f7-a1a09958dbbd`.
