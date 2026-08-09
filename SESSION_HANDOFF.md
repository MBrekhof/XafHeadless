# Session handoff — 2026-08-09

**State: clean.** `master` @ `2f02dc6`, pushed, working tree clean, no branches besides `master`, no hosts
left running. Nothing is half-done.

## What happened

Started from "NPO-001, what is the question" and ended with a recorded direction for the whole platform.
Three things in order: built NPO-001, corrected two backlog premises that were wrong, then took stock and
set direction.

### 1. NPO-001 shipped (`4cfe0ff`)

Non-persistent `[DomainComponent]` types now have a wire representation. `Opportunity_ListView` renders real
aggregated data in the client; before this it projected metadata fine and loaded nothing, because OData
cannot serve a type with no `DbSet`.

- `NonPersistentRegistry` + `OutlookInspiredPopulators` + `GET api/nonpersistent/{type}`, attached from
  `builder.ObjectSpaceProviders.Events.OnObjectSpaceCreated` (dxdocs 403164 names exactly this for WebApi).
- Same response envelope as OData, so the whole grid binding is reused rather than forked.
- Cap is model-declared (`IModelListView.TopReturnedObjects`), applied after population because
  `ObjectsGettingEventArgs` carries no skip/top. Measured: `@odata.count = 10000`, 5,000 returned.
- **A defect the green tests missed and the screenshot caught:** a New button on a computed view with
  nowhere to save. Both allow-set inputs said yes; nothing represented "there is no write route". Fixed in
  the projector, now asserted in an API test *and* the E2E.

### 2. Two backlog premises were wrong (`b55a2d3`, `722eb03`)

Checking the installed 26.1 demos rather than only OutlookInspired's module model:

- **PIVOT-001's "not model-declared" was false.** A Blazor pivot is fully declared —
  `PivotFieldArea`/`PivotSummaryType` on ordinary `ColumnInfo` nodes. It was read off the *module* model
  while the declaration lives in the *app* model.
- **CHART-001's conclusion held, its reason didn't.** `SettingTypeName` points at a Razor component holding
  the series (`ArgumentField`, `ValueField`, `SummaryMethod`). Inventing a chart contract really would be
  invention.
- **MODEL-001 (1245) carded**, then its own premise invalidated by spike: the blocker is not the model
  *layer*, it is the model *extenders*. Merging app-level XAFML alone would have silently discarded the
  attributes.

### 3. Direction set — ARCH-001 (`a4f0b9e`), README aligned (`2f02dc6`)

**Headless is a growth path for individual painful views, not a replacement for XAF.** Recorded in
`docs/DONE.md` as a decision (same pattern as PH2-003) so it does not get re-litigated.

Two late corrections it rests on, both material:

- "Losing controllers makes it useless" is too strong — `IHeadlessCommand` already ships, and an action's
  *declaration* is model-declared. What is lost is **automatic** behaviour projection.
- The performance claim needs precision: headless adds a hop per request. The wins are interaction latency,
  server load/scale, and work off the request path.

**The cost test, per app:** does the ViewController *contain* the business logic, or just *trigger a
service*? First is the rewrite metered out; second is ~10 lines per action with no drift.

## Where to pick up

Nothing is blocked and nothing is mid-flight. Reasonable next moves, roughly in order of value:

1. **`RPT-001` (1230) and `CRUD-002` (1235) are in Review awaiting your Confirm Done** — agents cannot set
   Done by design; both already carry full bodies and conclusions. A click each in the UI.
2. **The remaining open cards are the ones ARCH-001 actually cares about**: `FILE-001` (1234),
   `EXPORT-001` (1236), `EDIT-002` (1241), `LOOKUP-002` (1240), `RPT-002` (1244), `GRID-006` (1223),
   `GAP-010` (559). These are all list/detail/report path — the path the direction says matters.
3. **`FEAT-000` (1238) still reads as the old roadmap** — its progress list does not record NPO-001 as done
   and its sequencing predates ARCH-001. Worth a rewrite or a close.
4. **Backlogged, deliberately**: `CHART-001` (1228), `PIVOT-001` (1227), `DASH-001` (1229),
   `MODEL-001` (1245). Deprioritised, not refuted — evidence banked on each.

## Traps worth knowing before you run anything

- **`Api.Tests` and `E2E` are integration suites against running hosts.** Api `:5200`, Web `:5220`,
  JobServer `:5300`, plus a **smtp4dev** container for one report test. Only `Components.Tests` (113) runs
  standalone. Without the hosts you get failures that look exactly like regressions.
- **Three failures are pre-existing and environmental**, verified against a clean `master` stash with all
  three hosts up: the `JobExecutionRecord reaches Success` family (optimistic-concurrency clash) and the
  smtp4dev one. Do not chase them.
- **The tenant data lives in `OutlookInspiredDemo_company1`, not the `XafHeadlessDemo` host catalog.**
  Querying the host catalog shows 0 quotes and 0 orders and looks like an unseeded machine. It isn't.
- **A green suite is not a rendered page.** The New-button defect passed every test and was only visible in
  the screenshot. Keep opening the E2E screenshots.

## Current numbers

`Api.Tests` 85/85 · `Components.Tests` 113/113 · `E2E` new NPO test green · solution builds 0 warnings.

## Reference

- Stock-take, evidence-linked: https://claude.ai/code/artifact/6f65840b-f625-4443-86ee-927cfd9f84c5
- `docs/DONE.md` — ARCH-001 (direction) and NPO-001 (implementation) are the top two entries
- `README.md` — now carries a **"What it costs — read this before scoping"** section stating the actions gap
  plainly; it previously claimed lookup editors were display-only, which was false since LOOKUP-001
