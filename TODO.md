# XafHeadless — TODO

The **26.1 seed is the living code** (see `README.md` §The 26.1 seed). Its lineage: the 25.2
kill-gate **PASSED** (2026-07-12 — a closed, historical decision, not a pending item), and migration **MIG-001** (26.1.3/net10 on the OutlookInspired demo module,
Blazor Web App client, dual-phase E2E) completed 2026-07-12 (`docs/DONE.md`).

Items below are re-prioritized post-migration: what's left to prove on the platform, now that a
**disposable demo database** (not a shared production dev DB) makes exercising the riskier paths
cheap and safe.

## P1: High — the platform's remaining unproven core (now cheap+safe on disposable demo data)

_GAP-001, GAP-003 complete (the CRUD surface is proven server-side). See `docs/DONE.md`._

_BUG-001 (byte[] `IsList` members projected a broken `Byte_ListView` nested view — now classified as an `image` item, no more 404) fixed 2026-07-13 — see `docs/DONE.md`. Rendering the `image` as an `<img>` is [[UI-001]]._

_BUG-002 (nested tabs over a non-OData-exposed child type showed a raw 404 — now the projector omits the unreachable tab) fixed 2026-07-13 — see `docs/DONE.md`._

## P2: Medium — platform breadth

_GAP-004 (nav menu, minimal scope) done 2026-07-12 — see `docs/DONE.md`._

_GAP-002 (conditional appearance — per-row colors + enum rules, incl. the non-column enum-fix) done 2026-07-13 — see `docs/DONE.md`._

_UI-001 (client UI enhancement — Office White DevExpress theme, image rendering for byte[] members, styled nav/login/chrome) done 2026-07-13 — see `docs/DONE.md`._

_DOC-001 (overhaul `docs/HOW-TO-IMPLEMENT.md` — single-tenancy as the common path, multi-tenancy boxed as the important variant; folded in theme/image/nested-child gotchas; added a "Run it locally" section) done 2026-07-13 — see `docs/DONE.md`._

_GRID-001 (grid column chooser + group-by box + header context menu, client-side over a capped in-memory load) done 2026-07-13 — see `docs/DONE.md`._

_GRID-002 (server-side grouping/paging for large views via OData `$apply` — hybrid binding, landed via a backport of the companion headless implementation's GRID-001 work) done 2026-07-14 — see `docs/DONE.md`._

_GRID-003 (auto filter row for all column types — lifted the GAP-005 scalar-only restriction now that filtering is client-side) done 2026-07-13 — see `docs/DONE.md`._

#### MIG-002: Headless-migration readiness — gap list + service boundaries for an existing XAF Blazor app

**Planning artifact (owner asked 2026-07-13: "if we wanted to migrate an existing production XAF Blazor app to
this platform, what's left to build, and what should be its own service?").** This is NOT a commitment — the
earlier verdict on the app that prompted the question was **skip** (reliability + ViewControllers argue against
replacing a working Blazor monolith; see the DOC-001 record / strategy notes). It's the scoped answer to "if we
did," kept generic so it applies to any similarly-shaped target app. Owner's stated priorities anchor it:
**performance & reliability (top)**; ViewControllers used for **(a) parameterized background jobs** and **(b)
server-side grid filtering**; **data entry with validation**; lookup-editors-that-*create* objects **not**
important.

**A — Still to implement IN the headless platform (the gap list):**
- **Write-capable lookup editor** — pick an *existing* related object during data entry. Today lookups are
  display-only (PH2-005 was deferred "no consumer"; a real app IS the consumer). *Create-new-from-lookup stays
  out of scope* (owner: not important).
- ~~**Server-side grouping/paging for large views** — [[GRID-002]]~~ done 2026-07-14. ~~**Full auto filter row** — [[GRID-003]]~~ done 2026-07-13.
- **Inline nested-collection editing** (master-detail edit, not just navigate) if the target app edits children in place.
- **New-object creation UI** (client form; the server side is proven — GAP-003).
- **DashboardView rendering** (GAP-004 skipped it) if the target app has dashboards; **charts/pivot/scheduler**
  and any other DevExpress editors it uses (rich-text/HTML/tokens/…) — needs an **editor inventory**.
- **File/attachment upload** — `byte[]` is display-only after BUG-001/UI-001; uploads need an editor + storage (see B).
- **Complex ViewController behaviors** that aren't "params → command": popups, wizards, dependent/conditional
  actions. Audit the target app's controllers — the simple "accept params → start a job" ones map to command
  endpoints (the proven pattern); the rest need bespoke client UI or don't cross the wire.
- **App-level XAFML**: PH2-003 decided module-level is the contract, so any app-project customizations must move
  into modules first. Also check **localization** and the **audit trail** module if used.

**B — Should be its OWN server/service (NOT the headless API request path):**
- **Background jobs → Hangfire (or similar).** The owner's #1 ViewController use ("accept params → start a
  background job") must not run inline in the API request: the **command endpoint enqueues** a Hangfire job; a
  **separate worker process executes** it (retries, scheduling, dashboard, survives API restarts). Keeping long/
  heavy work off the request path directly serves the **reliability** priority — this is the single most
  important boundary.
- **Email → MailKit** in a mail/notification service (or run as a Hangfire job) — never inline in a request.
- **Reporting service** — DevExpress report rendering pulls heavy native deps (Skia) and is CPU-heavy; run it as
  its own service/container that produces PDFs/exports, invoked async (Hangfire) and delivered via storage/email.
- **File/blob storage service** — S3 / Azure Blob / filesystem behind upload/download endpoints; don't stream
  blobs inline through OData.
- **Scheduled/recurring tasks** — Hangfire recurring jobs (or a scheduler service), replacing any XAF
  scheduler/worker.
- *(Consider)* **real-time push** — a SignalR hub as a separate concern if the target app pushes live updates.

**Method:** the gap list is only as good as an **inventory of the target app's ViewControllers, actions,
reports, editors, and background/scheduled work** — do that audit first. No code here; this is the
migration-planning artifact.

#### GAP-010: Link / Unlink actions for nested list views (association write path)

**Status 2026-07-13 — SERVER + projection DONE** (owner: "server + aggregation projection only now"); the
**client Link/Unlink UI remains**. Done (see `docs/DONE.md`): `LinkController` link/unlink endpoints (secured
ObjectSpace + collection `Add`/`Remove`, aggregated-collection rejected, `CanWrite`-gated, 422 validation
contract) + the projected `LayoutNode.Aggregated` flag (`IMemberInfo.IsAggregated`). **Remaining (client):** a
**Link picker** dialog ("pick an existing object" — reuse the [[MIG-002]] write-capable lookup picker) + an
**Unlink** button on the selected nested row, shown only when `Aggregated == false`. (The demo has no laid-out
non-aggregated nested tab, so a browser E2E needs a dev-only shared-collection fixture — deferred with the UI.)

**Owner remark 2026-07-13:** nested list views need **Link** (associate an *existing* object with the master's
collection) and **Unlink** (remove the association — **not** delete the object) — the standard XAF collection
actions. Today a nested tab ([[BUG-002]]-filtered to OData-exposed child types) is **read/navigate only**; the
write path for to-many associations is missing.

- **Server:** endpoints to add/remove an object to/from a master's collection member, through a secured
  `IObjectSpace` — resolve the master + the target object by key, then mutate the collection (`member.Add`/
  `Remove` on the reference collection). For a **many-to-many via a join entity** (e.g. `Customer.Employees` →
  `CustomerEmployee`) Link/Unlink creates/deletes the **join row**, not the endpoint objects. Gate on
  `CanWrite` for the association member; run validation on commit (same **422** contract as the save endpoint).
  **Verify the XAF pattern against installed 26.1 source** (`LinkUnlinkController` / how the framework
  manipulates the collection member and the join) before implementing — don't hand-roll association semantics.
- **Client:** a **Link** button on the nested tab → a "pick an existing object" dialog (reuse the
  **write-capable lookup picker** from [[MIG-002]] A) scoped to linkable candidates; an **Unlink** button on the
  selected nested row. Refresh the nested grid after either.
- **Scope/XAF distinction:** Link/Unlink applies to **non-aggregated (shared)** collections; **aggregated
  (composite)** children are owned by the master and use **New/Delete** instead (a separate flow — GAP-003 proved
  server-side create). Project which collections are aggregated vs. shared (model/`IMemberInfo.Aggregated`) so
  the client shows the right actions per nested list. **Unlink ≠ Delete** — Unlink must never delete the object.

Server + client. Only applies to nested types that are OData-exposed ([[BUG-002]]). Verify every DevExpress API
against dxdocs / installed 26.1 source, not memory.

#### PH2-003: App-level XAFML diffs — decide where they live

Module-level model is projected; app-level `Model.xafml` customizations are invisible to the headless
host. Module-level remains the de facto contract until this is
explicitly decided one way or the other (load app XAFML in the host, or declare module-level final).

**DECIDED 2026-07-12 (owner) — module-level model IS the platform contract.** App-level `Model.xafml`
customizations are out of scope for the headless host: the seed grounds on the OutlookInspired *module*
(the host loads modules via `RequiredModuleTypes`, never an app project), and every projection/render path
is proven module-level. Apps wanting customizations honored should put them in a module. Loading app-level
XAFML remains a possible **Phase-2 feature** (host references the app project's `Model.xafml`, merges its
diffs before projection) if a real target app ever needs app-layer customization — but it is not on the
roadmap. This item is closed as a decision; no code.

#### PH2-005: Dedicated read-only lookup endpoint

Each lookup target currently requires widening `options.BusinessObject<T>()` exposure. Before
SEC-001, that widening grew the *unguarded write surface* too — now that OData writes are middleware-
blocked host-wide (405), that specific risk is closed; this item is now purely about scalability (a
`/api/lookup/{type}` read-only endpoint scales better than growing the general OData exposure list
per lookup target).

**DEFERRED 2026-07-12 (autonomous loop) — assessed, no current consumer (YAGNI).** A dedicated
`/api/lookup/{type}` endpoint would feed a lookup *dropdown editor* (fetching the list of selectable
options). That editor does not exist: the client's lookup rendering is **display-only** (LookupEditor
degrades to a badge — Task 9/10), reference *display* works via `$expand` on the parent, and GAP-001's
reference *writes* resolve keys in-process via `IObjectSpace.GetObjectByKey` (which doesn't even need the
target OData-exposed). So nothing currently needs this endpoint, and the exposure-list approach works.
Build it together with a write-capable lookup-dropdown editor (a future client feature) — at that point it
has a real consumer. Not worth building speculatively now; recorded here so it's a conscious deferral, not
an oversight.

_All buildable P2 items resolved: GAP-005 + GAP-007 + GAP-009 done (see `docs/DONE.md`); GAP-004, GAP-002,
PH2-003 parked with owner decisions above; PH2-005 deferred (YAGNI). What remains below is P3 hardening._

## P3: Low — hardening/consolidation

_PH2-002 (save-contract hardening) done 2026-07-12 — see `docs/DONE.md`._

_PH2-006 (consolidations + stale comments) done 2026-07-12 — see `docs/DONE.md`. Two follow-up findings
became `DATA-001` and `TEST-001` below; two additive bits (`$metadata` cross-check, runtime-validation-in-
`Required`) remain deferred, noted in the PH2-006 DONE record._

_DATA-001 done 2026-07-12 — see `docs/DONE.md`. **Correction:** it turned out to be a **non-bug for the EF
Core provider** (`EFCoreTypeInfoSource.InitTypeInfo` co-sets `IsDomainComponent = IsPersistent`, so the two
predicates were always equivalent; PH2-006's "Direction A" was XPO-style reasoning). Landed as a
behavior-preserving reconciliation (one shared predicate, drift-proof) + a dev-only inverse-less `LookupProbe`
consistency guard._

_TEST-001 (parallel-test same-row race) done 2026-07-12 — see `docs/DONE.md`. `[DoNotParallelize]` on the two
mutating test classes + deterministic `$orderby`; 5× consecutive green._

_GAP-008 (per-user layout prefs, server-side) done 2026-07-12 — see `docs/DONE.md`._

_MT-001 (host self-seeds the tenant DB) done 2026-07-12 — see `docs/DONE.md`._

_SEC-002 (delete the leftover POC account from the original POC's dev database) done 2026-07-12 — see `docs/DONE.md`._

Remaining actionable follow-ups are the two consolidation findings above (**DATA-001**, **TEST-001**) plus
the GAP-008 minors noted in its DONE record; **GAP-002** and **PH2-005** stay deferred (owner-reviewed).

_SVR-001 (JobServer — background jobs + report rendering as a separate service; folds in **SVR-002** unique
index on `JobDefinition.JobTypeName`, **SVR-003** the DevExpress OData host-shared-BO read fix, and **SVR-004**
the Api publish fix) done 2026-07-21, merged to master — see `docs/DONE.md`._

_Owner follow-up (not code): file the **DevExpress support ticket** for the OData constant-parameterization
framework defect — draft ready at [`docs/notes/devexpress-ticket-odata-shared-bo.md`](docs/notes/devexpress-ticket-odata-shared-bo.md); bundle the deferred `$select`→`edmModel` bug into it._

---

**Closed by the migration** (not tracked above — see `docs/DONE.md` MIG-001):
- **SEC-001** (OData write guard) → `Middleware/ODataReadOnlyMiddleware.cs`, Task 1.
- **PH2-001** (KeyMember projection) → `ViewMetadata.KeyMember`, Task 2.
- **Required-flag/save mismatch** → closed by migrating to a model with real `[RuleRequiredField]`
  rules (`Employee`), Task 2; see `docs/notes/save-contract.md`.
- **Auto-mode / render-mode-freedom proof** → dual-phase (Server + WebAssembly) E2E, Tasks 3–4.
