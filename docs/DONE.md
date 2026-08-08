# XafHeadless — DONE

#### GRID-005: Refuse an impossible lookup sort up front instead of recovering after the click (ID: 1222)

**Done 2026-08-08.** Implements the upgrade path BUG-005 recorded. `LookupMetadata` projected
`ObjectType`/`KeyMember`/`DisplayMember` and **no type** for the display member, so the client could not
tell that sorting a lookup column meant `$orderby` over something OData cannot order by; the ceiling was
enforced only *after* the failure. `ProjectLookup` now classifies the display member through the same
`ClassifyDataType` every other member goes through and ships it as `LookupMetadata.DisplayDataType`.
`GridBinding.IsServerSortable` refuses `lookup`/`image`/`collection` display members, `IsServerGroupable`
inherits the same ceiling (`groupby((Store/Emblem))` fails exactly as `$orderby` does), and `XafListView`
binds `DxGridDataColumn.AllowSort` to it in server mode — verified `bool?` with column-over-grid
precedence against dxdocs rather than assumed.

**BUG-006's recorded root cause was wrong, and the wire proved it.** That record said Store's display
member `Emblem` is `Edm.Binary`. It is not: `CustomerStore` carries `[XafDefaultProperty(nameof(Emblem))]`
and `Emblem` is a **reference to the `Emblem` entity** (`HasOne(store => store.Emblem).WithMany(...)` in
`OutlookInspiredDbContext`), so `$orderby=Store/Emblem` orders by a **navigation property**. The projector
answered `"lookup"`, not `"image"`, which is what surfaced it — the RED test had been written to the old
claim and failed on it. This widens the fix: a lookup-of-a-lookup is *any* entity whose default property
is a reference, which is far more common than a blob default property.

**A null `DisplayDataType` stays sortable** — it means the host predates the field, not that the column is
unsortable — so an older Api keeps working. BUG-005's `StripShaping` remains the backstop regardless:
`AllowSort=false` stops the header *click*, but `SortIndex`/`SortBy` still sort in code (dxdocs), so a
layout persisted before this ceiling existed can still re-apply a sort the server cannot serve.

Tests: `Api.Tests` **72/72** (pins that every projected lookup carries its display member's type, and that
Store's is a reference), `Components.Tests` **102/102** (+3 predicate tests including the unknown-type
back-compat case). `LookupSortCeilingE2ETests` proves it live and was **watched to fail** with the
predicate reverted — without the ceiling the click destroys the grid outright (the header locator resolves
to 0 elements), which is precisely the failure BUG-005 was recovering from. Two traps that E2E hit, both
written into the test: in Server render mode the OData calls are issued by the Blazor circuit
**server-side**, so `Page.Request` never sees them and a network assertion passes vacuously; and GAP-008
persists layout, so a toggle assertion is stateful across runs and would leave `Order_ListView` sorted for
`DateFilterE2ETests` — it clears the prefs before and in `finally`.

Files: `ViewMetadataProjector.cs`, `ViewMetadataDtos.cs`, `Contracts/ViewMetadata.cs`, `GridBinding.cs`,
`XafListView.razor`, `ListViewMetadataTests.cs`, `KnownModel.cs`, `GridBindingTests.cs`,
`LookupSortCeilingE2ETests.cs`.

#### UI-002: Modernist theme — restyle the client from the design handoff (ID: 1213)

**Done 2026-08-08** (branch `feature/modernist-theme`, merged to master). Implements
`XAF Form Styling POC/design_handoff_modernist_xaf/README.md`: flat, architectural, all-Archivo,
red-on-light-grey, zero corner radius, 2px rules, flush-left everything. **Styling only** — auth flow, grid
binding and save/validation behaviour untouched. `XafHeadless.Web/wwwroot/modernist-theme.css` is linked
last in `App.razor` so it wins over both the Classic theme's Bootstrap layer and `app.css`.

**Why the DevExpress half looks the way it does.** Office White is a *Classic* theme, which exposes **no
public CSS variable API** (only Fluent has `--dxds-*`), and it defines its radii/colours as `--dxbl-*`
variables **on the component's own selector** — so a `:root` override is inert. The overrides redefine those
variables at matching specificity (doubled classes), verified against the installed 26.1 theme CSS rather
than assumed. `--dxbl-*` is documented as internal; the supported alternative for a permanent restyle is a
custom Classic theme built from the SCSS sources (dxdocs Blazor/404360) or `Themes.BootstrapExternal`.

**Three things the handoff's drop-in selectors could not have known**, found by inspecting rendered DOM
(the handoff's own step 3): grid **pager** buttons are `.dxbl-btn-outline-secondary.dxbl-pager-page-btn`
(not `-standalone`) and colour from `--dxbl-pager-*`, which is where the old orange still showed through;
**header cells** are filled with `color-mix(#000 5%, header-bg)` from a 5-class selector, so the grey band
survives any variable change; **filter-row editors** are stripped of fill and border by the in-place editor
rule, which read as an empty row.

`HighlightRowOnHover="true"` was added to `XafListView` because it defaults to **false** (dxdocs
`DxGrid.HighlightRowOnHover`) — the handoff's row-hover tint is impossible in CSS alone. Also bumped the
DevExpress package refs **26.1.3 → 26.1.4** in the same branch: the installed demos pull
`DevExpress.Document.Processor 26.1.4`, which pins `DevExpress.Data 26.1.4`, so 26.1.3 refs no longer
restore at all (hard NU1107).

Verified with C# Playwright against both hosts: computed styles for every screen in the handoff's screen
list plus hover/focus/disabled/error states, screenshots reviewed for login, list and detail. One E2E test
needed adapting — `DateFilterE2ETests` matched a header caption exactly, and `text-transform: uppercase`
makes `innerText` return the transformed text; the assertion is about column identity, so it is now
case-insensitive.

#### BUG-003: Date filter earned a 400 that terminated the circuit — and the grid served 10 rows, not 25 (ID: 1214)

**Done 2026-08-08.** Two defects behind one failing E2E test (`OrderServerMode_DateFilterRow_...`), both
live-diagnosed against running hosts, and **neither caused by the restyle** (reproduced with the styling
stashed, twice).

**1. `date()` instead of instant literals.** The date filter row sent a `[day, next-day)` range as UTC
instant literals. These members are `Edm.DateTimeOffset` in the EDM while the CLR property is `DateTime?`,
so the binder finds no operator for the pair and answers 400: *"The binary operator GreaterThanOrEqual is
not defined for the types 'Nullable&lt;DateTime&gt;' and 'Nullable&lt;DateTimeOffset&gt;'."*
`ApiClient.GetPageAsync`'s `EnsureSuccessStatusCode` then threw out of a DevExpress grid callback with
nothing handling it, **terminating the Blazor circuit**. It fired on filter *apply* — the test only reported
at the clear step because the circuit was already gone. Probed live for what the host accepts: a no-offset
literal fails to *parse*, but `date(path) op yyyy-MM-dd` works (200, and the right row count).
`ODataFilterTranslator` now emits that, which also drops the `ToUniversalTime()` step — itself a
wrong-answer bug that pushed a late-evening wall time onto a neighbouring date. Ceiling recorded as
**GRID-006** (`date()` is not SARGable).

**2. `PageSize` silently reset to 10.** `GridPersistentLayout.PageSize` is `int?` carrying
`[JsonIgnore(WhenWritingDefault)]`, so a null is omitted from the persisted blob and deserializes back as
null; applying that layout resets `DxGrid.PageSize` to its documented default of **10**, overriding the
markup's 25. Only `Order_ListView` was affected — the one view with persisted prefs.
`GridBinding.RestorePageSize` refills the markup value when the blob carries none; a persisted user choice
still wins.

#### DIAG-001: Instrument the runtime so failures name themselves (ID: 1215)

**Done 2026-08-08.** Implements `docs/superpowers/specs/2026-08-08-runtime-diagnostics-design.md`, with
**no new dependencies**. Motivated by BUG-003: an OData 400 killed the circuit and left no evidence in
either host's log, because `EnsureSuccessStatusCode`'s message carries neither the request nor the cause,
and an OData 400 is a normal *response* the Api never logged.

- **`ApiRequestException`** carries method, absolute URL (**query string included** — for a bad `$filter`
  that IS the evidence), status, and a bounded 2 KB excerpt of the response body. The paths that degrade by
  design (null view metadata, empty menu, dropped prefs) behave exactly as before but now log a warning.
- **`GridCustomDataSource.ExceptionHandler`** is set at last (the documented hook, dxdocs 26.1) — never
  setting it is precisely why a failed fetch escaped a DevExpress callback and killed the circuit.
- **`ErrorBoundary`** in `MainLayout`, recovering on `LocationChanged` so one failure cannot wedge the app;
  on-screen detail is Development-only via `DiagnosticsOptions` (each host passes its own
  `IsDevelopment()`), and `CircuitOptions.DetailedErrors` is set in Development.
- **The Api logs every 4xx/5xx** with method, path, query string, status, elapsed and user — chosen over
  `UseHttpLogging`, which logs everything and needs an `IHttpLoggingInterceptor` to narrow to failures.

Verified by fault injection, not inspection: a real 400 produced the log line and an on-screen message
carrying the server's reason with the app alive; with the grid handler temporarily removed the same failure
reached the `ErrorBoundary` (circuit **not** terminated, navigating away cleared it). **It found BUG-004
within minutes of existing.**

#### BUG-004: Dotted model member paths were broken four ways at once (ID: 1216)

**Done 2026-08-08.** An XAF ListView column can name a dotted **model** path — `Order_ListView` really has
`Customer.Name` — which the projector classifies as a plain string with `Lookup == null`. The client assumed
every non-lookup column was a flat property, so: `$orderby` and `$filter` passed the dot through (400 *"The
child type 'Customer.Name' in a cast was not an entity type"*), `$expand` covered only lookup-classified
columns so the wire carried no `Customer` at all, and the cell read `row["Customer.Name"]` — a key no OData
payload has — rendering the column **permanently blank**. Probed the accepted forms first
(`$orderby=Customer/Name` and `$filter=contains(Customer/Name,'a')` are both 200).

Fixed as **one seam, not four patches**: `GridBinding.PathSegments` turns both kinds of nested column into
segments (a classified lookup contributes `Member` + `DisplayMember`; a dotted member contributes its own),
and `FieldFor`, `OrderPathFor`, `BuildExpand` and `MaterializeRow` all derive from it, so the two cases
cannot drift apart again. A flat column yields one segment, leaving its behaviour byte-identical. `TryWalk`
replaced `LookupDisplay` outright (a lookup *is* a one-hop walk) and `ExpandClause` nests, so hop depth
stopped being a silent ceiling. Verified live: the column shows values, sorts, and filters. Checked that
`Product_ListView`'s other dotted member `PrimaryImage.Data` is `DataType=image`, so `VisibleColumns`
filters it before `BuildExpand` and **no blob is pulled into rows**.

#### BUG-005 / BUG-006 / BUG-007: A rejected sort or grouping must not outlive the click (ID: 1217)

**Done 2026-08-08.** Sweeping every projected list view for blank columns, then sorts, then filters, then
grouping, found **one defect exposed by two different ceilings**.

**BUG-005 — the defect (the amplifier).** DxGrid's `LayoutAutoSaving` **persisted the shaping that had just
failed**, so every later load replayed it and the view rendered nothing — recoverable only by clearing the
stored prefs by hand, which happened twice mid-sweep. This is the only thing that was actually fixed; the
two ceilings below are correct by design and were deliberately left alone.

- **BUG-006 — sort:** `Store` is a lookup whose display member `Emblem` is **a reference to the `Emblem`
  entity** (`CustomerStore` carries `[XafDefaultProperty(nameof(Emblem))]`; `HasOne(store => store.Emblem)
  .WithMany(...)`), so the sort orders by a **navigation property**; the server is explicit — *"The
  `$orderby` expression must evaluate to a single value of primitive type."* **Cause corrected 2026-08-08
  by GRID-005:** this record originally said `Emblem` was `Edm.Binary` (a blob). It is not — the live
  projection answered `"lookup"`, and the demo module's source confirms a reference. Same 400, different
  and considerably more common cause (any entity whose default property is a reference).
- **BUG-007 — grouping:** grouping by `InvoiceNumber` (55k distinct) trips `EnforceGroupCeiling`'s
  `NotSupportedException`. **That part is correct and deliberate** (GRID-001 chose to fail loud rather than
  render thousands of headers), and cardinality is a runtime property no static ceiling can see.

Neither was predictable client-side at the time — `LookupMetadata` projected no type for a display member —
so the ceiling is enforced after the fact. (**GRID-005** has since projected that type, so the *sort* half
is now refused up front; grouping keeps the after-the-fact recovery, because cardinality stays invisible.) `GridBinding.StripShaping` drops `SortIndex`/`SortOrder`/`GroupIndex` while
column order, widths and `PageSize` survive, and `OnGridDataError` calls it when the failure is
**attributable to the layout**: an `ApiRequestException(400)` (a query we built wrongly) or a
`NotSupportedException` (our own ceilings). A 5xx or a network blip is not the layout's fault and leaves the
layout alone. The recovery is best-effort and never masks the error the user is already shown. Upgrade path
to refusing such a sort up front: **GRID-005**.

Verified live for both, in separate runs so the evidence is unambiguous: the click still fails visibly with
0 circuit terminations, the prefs come back without the offending shaping (and the host logs the drop), and
a **full reload of the same view recovers** with rows and no error.

**The rest of the sweep was clean**, and the successful paths were proven too, not just the failures:
sorting works on every column of all 7 views; Order's filter row filters on all 8 filterable columns (the 3
disabled ones are the documented enum/lookup ceiling); nested master-detail tabs render on Order (4 tabs)
and Employee (2); and server-side grouping *succeeds* — 3 buckets whose counts sum to exactly 54,999 (the
true total), expansion pages children with the group criteria baked into `$filter` (wire-verified), and
two-level grouping yields leaf children. Zero 4xx, zero circuit deaths.

#### DOC-002: Document the JobServer's dev settings (its absence 500s every request) (ID: 1212)

**Done 2026-08-08.** Running `XafHeadless.JobServer.Tests` for the first time on a fresh clone exposed a
README gap, not a code bug: the one-time setup named only `XafHeadless.Api`'s
`appsettings.Development.json`, but the JobServer ships the same deliberately-empty `IssuerSigningKey`. With
no key the JWT middleware throws `IDX10703: … key length is zero` on **every** request — including the
anonymous `/health` endpoint — so the host looks booted and answers 500 to everything. Two non-obvious
points now stated in the README: the JobServer key must **match** the Api's (it *validates* the JWTs the Api
mints; the tests authenticate at `:5200` and present the token to `:5300`), and the suite needs `--no-build`
while its host runs, or the rebuild collides with the locked exe and the run fails before a single test
executes. With the key in place the suite is **12/12** — the last never-run suite in the repo.

#### SVR-001: JobServer — background jobs + report rendering as a separate service (ID: 1065)

**Done 2026-07-21** (branch `feat/jobserver-svr-001`, dispatches A–K + follow-ups SVR-002/003/004; merged
to master). Stood up **`XafHeadless.JobServer`** (`:5300`) — a UI-less XAF host that runs background work
off the API request path via **Hangfire**, so long/heavy jobs never block a request and survive an API
restart. One end-to-end demo job, **`EmailOrdersReport`**: renders the demo's Orders report (`ReportDataV2`)
to a PDF in a **tenant-isolated child scope**, stores a `ReportArtifact`, and emails it via **MailKit**;
enqueued from the Api through `POST api/commands/EmailOrdersReport` into shared Hangfire SQL storage (the Api
is client-only, the JobServer is the sole worker). `JobDefinition`-driven **cron** scheduling
(`ScheduleSyncService` reconciles rows → Hangfire recurring jobs and writes `NextRunUtc` back), a client
**"Run Now"** button, and full CRUD over `JobDefinition`/`JobExecutionRecord` through the existing generic
client with **zero new per-view client code** (`ReportArtifact`/`EmailArchive` kept internal). Four
host-owned shared BOs land in the host catalog via the same `.WithSharedBusinessObjects` path
`UserLayoutPref` uses.

**Architecture wrinkle resolved (Task 1.2):** the Api is multi-tenant and the Orders report lives in the
tenant DB while the Job* BOs are host-owned. Verified against installed 26.1 source that the demo module
cannot be hosted without multi-tenancy at all (its `Updater` requires `ITenantProvider`), so the JobServer
copies the Api's full multi-tenancy wiring; the render sets `TenantId` and logs on as the tenant admin
inside a **fresh child DI scope**, while the outer job scope stays null-tenant so the recorder's shared-BO
writes hit the writable host branch (tenant isolation verified real — `ITenantProvider` is `AddScoped`).

**Deliberate simplifications vs. the companion headless implementation's pattern** (single-job-type demo host): no
`XafJobScopeInitializer`/service-user logon (a `NoOpJobScopeInitializer` satisfies the dependency — every
worker write goes through `INonSecuredObjectSpaceFactory`), no `JobExecutionCapture` AsyncLocal, no
`DirectJobDispatcher`/`UseHangfire` toggle, one `JobDispatchService` case.

**Folded-in follow-ups.** **SVR-002** (`323e497`) — unique index on `JobDefinition.JobTypeName` (GAP-008
`[DisableDeferredDeletion]`+`[Index]` pattern), enforcing one row per type. **SVR-003** (`9056205`) —
root-caused and fixed a real **DevExpress OData framework defect**: constant-parameterization made
`$filter`/`$top` literals read as `default(T)` against the multi-tenant standalone shared-BO DbContext, so
host-shared BOs returned empty for string/GUID `$filter` and `$top`; fixed with a global MVC convention
(`EnableConstantParameterization=false`) — **so "zero-new-client-code CRUD" holds for host-shared BOs at the
cost of one server-side setting** — plus `SaveController` 409-on-duplicate. A **DevExpress support ticket**
remains to be filed (draft: [`notes/devexpress-ticket-odata-shared-bo.md`](notes/devexpress-ticket-odata-shared-bo.md);
the deferred `$select`→`edmModel` bug bundles into it). **SVR-004** (`1d4e9ea`) — fixed NETSDK1152 on
`dotnet publish XafHeadless.Api` (targeted MSBuild exclusion of the JobServer's `appsettings*.json` from the
Api publish; each host keeps its own config).

**Verification.** New `XafHeadless.JobServer.Tests` — boot, Run Now → Success, cron `NextRunUtc` fill/clear,
`%PDF-` render (direct SQL, since `ReportArtifact` has no wire surface), live smtp4dev email delivery, and
the M-4 recipient guard — green alongside the Api, Components, and dual-render E2E suites. Every dispatch
A–K was individually reviewed **0 Critical / 0 Important**; email delivery, tenant isolation, and PDF
validity were live-proven, and every DevExpress API was verified against installed 26.1 source, not memory.
Deviations: `docs/DEVIATIONS.md`.

#### GRID-004: backport the companion headless implementation's date-filter, dateonly, and bounded-group-fetch fixes (ID: 1064)

**Done 2026-07-20** (commits `af45a3c` + `ab6a92d`). Transplant of the companion headless implementation's
`4db6a49` + `de0bb25` commits, including their review fixes; the bug was live here too — date cells materialized as ISO strings, so
DxGrid's value-type sniffing typed date columns as TEXT, the filter row emitted string criteria
(`contains`/`eq 'text'`) the server 400s against `Edm.DateTimeOffset`, and **the unhandled 400
killed the whole Blazor circuit**. Landed: typed DateTime materialization (cells render formatted
dates); an explicit metadata-driven `DxDateEdit` filter cell in BOTH binding modes committing the
`[day, next-day)` range (nothing sniffed); zone-converted instant `$filter` literals; the
`"dateonly"` classification (DateOnly = `Edm.Date` — excluded from server filter/group instead of
receiving a rejected/day-shifted literal; dormant here, the demo has no DateOnly member, guarded by
unit tests); `MaxServerGroups`/`EnforceGroupCeiling` + the `$top`-after-`$apply` bounded group fetch
(the origin's group ceiling had never been backported); and the nested-tab count-probe fold (one
request per master-row click). **E2E findings of this repo's own:** the demo's Order data has no day
under one page (35–67/day) AND the default order clusters same-day rows, so the server-mode test
asserts on day VALUES (filtering to the newest day must flip every visible OrderDate cell to a text
absent from the baseline page; clearing restores the baseline) with settle-polling (mid-render cells
read as ""); the in-memory Employee test stays count-based. Verified: Components.Tests 95/95,
Api.Tests 64/64, full E2E 7/7 twice consecutively, screenshots inspected. Full-lane backport review:
**0 Critical / 0 Important** — transplant fidelity confirmed hunk-for-hunk against the origin,
all origin review-fixes (I1/M1/M6) carried, multi-tenancy clean, vendor grep clean; minors are
documented data-coupled E2E flake windows, accepted.

#### POC-001: Execute the headless-XAF kill-gate POC

**Completed 2026-07-12.** GATE VERDICT: **PASS** (10 PASS / 2 PASS-with-ceiling).
Built across 11 planned tasks (implementation intentionally discarded post-gate; the record is the product): standalone XAF WebApi host over a production XAF module
with security-trimmed metadata projection, validating save contract, command endpoint; Blazor WASM
client (XafListView/XafDetailView) rendering real views of the POC entity purely from projected metadata;
Playwright E2E suite + committed evidence (`docs/evidence/`). Executed in ~1 day of orchestrated
subagent implementation vs the 3-weekend budget. Abort condition never tripped (no model/security
logic re-implemented — confirmed by per-task reviews). Origin: `C:\Projects\xaf-blazor-critique.md` Part 4.

#### MIG-001: Migrate to 26.1 on the OutlookInspired demo module (ID: 1218)

**Completed 2026-07-12.** Migrated the platform seed to DevExpress 26.1.3 / .NET 10, re-grounded on
DevExpress's own `OutlookInspiredDemo.Module` (multi-tenant headless hosting — multi-tenancy proved
required, not optional, for this module). Client restructured from a WASM-standalone app into a
Blazor Web App (`InteractiveAuto`), with a dual-phase E2E proving the same smoke path passes
identically in the Server render phase and the WebAssembly render phase. **SEC-001** (OData write
guard) and **PH2-001** (`KeyMember` projection) — both open items from the POC's final review — were
closed in the process. Suites: 48 unit / 22 API / 1 dual-phase E2E, all green.

#### GAP-001: Lookup/enum member WRITES

**Completed 2026-07-12** (commit `8726e87`, autonomous TODO loop). Closed the confirmed latent 500
(gotcha 12): reference/lookup members (`Order.Customer`/`Store`/`Employee`) sent a scalar FK key were
being JSON-deserialized straight into the referenced business-object type. `SaveController` now detects
reference members with the same predicate DevExpress's own `ReferenceMemberModifier` uses
(`IsAssociation`, or a persistent member whose type is itself persistent — verified against installed
26.1 source, not assumed) and resolves the incoming key via `IObjectSpace.GetObjectByKey` against the
referenced type's key-member type. **Enum writes needed zero code** — the demo enum types carry
`[JsonStringEnumConverter]`, so the existing deserialize already round-trips string names; proven by a
test that passed even in the TDD red-state run. Folded the two same-code-path **PH2-002** hardening bits:
unresolvable reference key → 400, malformed route/reference key → 400 (were 500), with catch clauses
narrowed to the actual conversion exceptions so genuine faults still surface as 500. Tests: +6 in
`SaveReferenceAndEnumTests` (reference/enum/scalar round-trips with finally-restore, three 400 paths);
full `XafHeadless.Api.Tests` suite 28/28. Implementer + reviewer + fix pass, review clean.
**Still open in PH2-002 (P3):** unknown-member → 400, per-member `CanWrite` → 403 (deliberately deferred).

#### GAP-003: New-object creation flow

**Completed 2026-07-12** (commit `82e201c`, autonomous TODO loop). Completes the CRUD surface — Read
and Update were proven end-to-end; Create was the last gap. Added a keyless `POST api/save/{type}` on
`SaveController`: `os.CreateObject(clrType)` → the **shared** `ApplyChanges` helper (same
reference/enum/scalar resolution GAP-001 built for update) → the **shared** `CommitWithValidation`
helper (same `IValidator`@Committing path + 422 contract) → **201 Created** with `{ key }`. The key is
the server-generated `BaseObject.ID` Guid the client never sends — the concrete evidence of
`ObjectSpace.CreateObject` default-value semantics "over the wire" (no separate GET-defaults endpoint
was built: no consumer yet, the client sends the members it wants and the server fills the rest). The
update path was refactored to share both helpers with zero behavior change (200/400/404/422 preserved,
existing suite passed unmodified). For create-test hygiene, added an env+admin double-gated
`DELETE api/test-fixtures/{type}/{key}` (gated identically to `EnsureRestrictedRole`) so the
create-success test deletes its row in `finally`. Targets: create-success = `Order` (creatable +
deletable); create-invalid(422) = `Employee` (real `[RuleRequiredField]` rules; it is `[ForbidDelete]`,
so it can never be a cleanup target — but the 422 path blocks commit, so nothing persists). Tests: +2
in `SaveCreateTests`, full `XafHeadless.Api.Tests` suite 30/30. Implementer + reviewer, review clean
(SPEC ✅ / QUALITY Approved, no Critical/Important). Minor, disclosed: the test-infra delete returns 404
(not 400) on a malformed key.

#### GAP-005: Filter UI

**Completed 2026-07-12** (commit `c4dd647`, autonomous TODO loop). Enabled the DevExpress `DxGrid`
filter row on list views and translated its `CriteriaOperator` into an OData `$filter`, AND-combined
with the existing master-detail filter in **both** `GetItemsAsync` and `GetItemCountAsync` (count must
match the filtered set). Purely client-side — the server `$filter` path was already proven. New
`ODataFilterTranslator` covers the operator set the filter row emits (`eq/ne/gt/ge/lt/le`,
`contains/startswith/endswith`, `eq/ne null`, `and/or`) with `'`-escaping, culture-invariant numbers,
and UTC ISO-8601 dates. **Ceiling (documented):** scalar columns only — enum columns render a caption
and lookup columns a flattened display value, neither safe to translate against the real OData property,
so a scalar-only `filterPathByField` is passed (a missing entry skips the clause; no raw-name fallback,
unlike sort) **and** the filter-row editor is hidden on those columns (`FilterRowEditorVisible=false`)
so a user isn't misled into typing a filter that's silently dropped. Tests: +17 `ODataFilterTranslatorTests`
(exact `$filter` strings incl. quote-escaping, negated-null, FieldName≠path, master-AND-user combine,
out-of-scope operator → null); `Components.Tests` 65/65; E2E extended with a scalar filter narrowing
55,000 rows to 1, green in both Server and WebAssembly render phases. Implementer + reviewer + fix pass,
review clean (SPEC ✅ / QUALITY Approved). The reviewer independently corroborated the operator set as an
exact match for dxdocs' real `GridFilterRowOperatorType` surface. **Deferred (documented):** enum/lookup
column filtering; local-time date literals (UTC-only today).

#### GAP-007: JWT persistence

**Completed 2026-07-12** (commit `562bccd`, autonomous TODO loop). The in-memory JWT (`AuthState`, Scoped
per render context) dropped on any hard reload or the InteractiveAuto **WASM render-mode takeover**,
bouncing the user to `/login`. Now it persists to plain **`sessionStorage`** (via `IJSRuntime` — NOT
`ProtectedBrowserStorage`, which is Server-circuit only and would break the WASM phase) and restores on the
first interactive render. Client-only; `AuthState` stays Scoped. Design: `PersistAuth` (hosted in
`MainLayout`) restores in `OnAfterRenderAsync(firstRender)` and persists on every `AuthState.Changed`, with
a `try/finally` guaranteeing the `RestoreAttempted` latch flips even if the read throws; **no false
`/login` bounce** because `SetToken(stored)` runs before `MarkRestored()` (so the token is present the
instant the latch flips) and every `/login` decider is gated on `RestoreAttempted`; `AuthGuard` wraps
protected pages (shows `Loading…` until the token is present — blocking any null-token fetch/401 — and, once
restore is attempted with no token, redirects to `/login` itself, covering client-side navigation). The E2E
Phase B no longer force-re-logins after the takeover — it **proves the persisted session survives** (the
authenticated list renders, no re-login), which also removes the old GAP-007 re-login hack. Implementer +
opus reviewer + opus fix pass (SPEC ✅; the reviewer hand-verified the no-false-bounce ordering, prerender
safety, and 401-clear path). Two pre-existing test-fixture flakes (DxTextBox login commit-on-blur; DxGrid
sort-flip timing) were surfaced and hardened — test-only. Tests: `AuthStateTests` +3; `Components.Tests`
68/68; dual-render-mode E2E green (persistence proof). **Known residual:** a rare pre-existing E2E flake
(login/sort timing, outside the persistence path) — noted for the final review.

#### GAP-009: N-view sweep

**Completed 2026-07-12** (commit `2a4071c`, autonomous TODO loop). Substantiates the platform's central
**compression claim** — that projection + rendering are view-agnostic, so any model view renders end-to-end
with **zero per-view client code**. Added `Customer` and `Product` (both already OData-exposed) as two new
list/detail data points alongside Order/Employee → **4 pairs (8 views) now proven**. Entirely test-only:
**zero `XafHeadless.Components` (rendering) code changed** — the proof is that `Components.Tests` stays
68/68 unchanged while two brand-new view types render through the same generic `/list/{ViewId}` and
`/detail/{ViewId}/{key}` routes. `NViewSweepMetadataTests`: 10 `[DataRow]`-parameterized API tests (list
columns + KeyMember, non-empty detail layout, OData rows) across all 4 pairs; `SmokeTests`: a new E2E
navigating to Customer/Product lists (>0 rows via the generic client) + opening a Customer detail. Five
per-view quirks documented — all **pre-existing graceful degradations handled by existing client code**,
none needing a fix: phantom `Byte_ListView` node for byte-array members; data-dead Customer
`Employees`/`Quotes` nested lists (OData sets unregistered); `collection`-typed columns filtered by
`GridBinding`; Product's `!Available` appearance unstyled (GAP-002 parked); all editors resolve to
supported kinds. Tests: `Api.Tests` 40/40 (+10); E2E 2/2 (5 consecutive); `Components.Tests` 68/68
unchanged. Implementer + reviewer, review clean (SPEC ✅ / QUALITY Approved) — the reviewer independently
re-ran `git diff` to verify zero-product-code and cross-checked all 5 quirks against the demo source.
**Minor, for final review:** a duplicated `Flatten` test helper (3 copies in `Api.Tests` now — PH2-006
consolidation candidate); a latent one-shot `CountAsync()` race in the pre-existing, untouched
`RunSmokeAsync` (hasn't flaked; future-ticket).

#### GAP-004: Navigation menu projection (minimal scope)

**Completed 2026-07-12** (commit `2d17466`, interactive — owner chose **minimal** scope over a faithful nav
tree). Replaced the client's hardcoded navigation with a real menu projected from the model. New
`GET api/model/navigation` (`NavigationProjector`) walks the model's nav tree — flattened via
`IModelRootNavigationItems.AllItems` (XAF flattens it, groups auto-excluded) — and filters to items whose
view is a **ListView**, whose target type is **OData-exposed**, and which the user **CanRead**
(security-trimmed exactly like `ViewMetadataProjector`; `CanNavigate` folded into `CanRead`). Client renders
a flat `NavMenu` sidebar (only when authenticated), and post-login lands on the first projected nav item
instead of a hardcoded route. Minimal scope, owner-chosen: no groups, no icons, no faithful tree — the
Welcome dashboard, admin/reports items, and `@CurrentUserID` "My Details" fall out of the filter naturally.
**Notable correctness catch:** the exposure filter uses `IOptions<WebApiOptions>.BusinessObjects` (the exact
list `options.BusinessObject<T>()` populates), **not** the OData EDM entity sets — the EDM sets are too broad
(they include transitive association/lookup targets like `ApplicationUser` that 404 on `api/odata/{type}`),
verified live and against `TypesInfoEdmModelCustomizer` source. Demo result: a 5-item menu (Employee,
Evaluation, Customer, Order, Product); `Employee_ListView` is the model's first item → the new post-login
landing. Tests: `NavigationMetadataTests` +3 (inclusion / exclusion / per-user CanRead via the restricted
fixture); `Api.Tests` 43/43; a new E2E asserting the menu renders and a real `NavLink` click navigates +
renders the grid; E2E 3/3 across 3 runs; `Components.Tests` 68/68 unchanged. Implementer + reviewer, review
clean (SPEC ✅ / QUALITY Approved) — reviewer independently confirmed the exposure root-cause against source
and that the `MainLayout` re-render tweak preserves the GAP-007 no-false-bounce invariant. **Minor, for final
review:** a fresh login fires `GET api/model/navigation` up to 3× concurrently (harmless idempotent GET; the
optional nav cache was deferred); a user with zero readable ListViews lands back on `/login` (spec-literal
empty-fallback edge, no such demo user).

#### PH2-002: Save-contract hardening remainder

**Completed 2026-07-12** (commit `669c658`, interactive P3). Finished the save-contract hardening on the
shared `SaveController.ApplyChanges` helper (so both update and create paths get it): an **unknown or
non-writable (collection) member → 400** (was silently skipped), and a **per-member `CanWrite` → 403** asked
via the framework `IsGrantedExtensions.CanWrite` (never re-implemented), returning this API's structured JSON
`{error}` (naming the member) **before** commit. Check order is member-exists → CanWrite → resolve/set, so a
403 can't be masked. **Notable:** DevExpress's own commit-time security already 403s some Order writes, so a
status-code-only test false-greens against unfixed code — the 403 test asserts the JSON response *shape* (vs
the framework's `text/plain`), and a control test proves the deny is member-specific; the explicit pre-commit
check earns its place (short-circuits before resolving other members + consistent JSON contract). The
restricted fixture gained a type-level `Write:Allow` on Order + a member `Write:Deny` on `PONumber` (with a
duplicate-row guard, since `AddMemberPermission` is non-idempotent and the fixture persists cross-run). Tests:
+3 in `SaveReferenceAndEnumTests`; `Api.Tests` 46/46; build clean. Implementer + reviewer, review clean (SPEC
✅ / QUALITY Approved) — reviewer independently verified the CanWrite overload, the fixture safety, and that
the existing client never sends collection members. **Minors folded into PH2-006:** the stale `ApplyChanges`
method-summary comment; a one-line note on the fixture's member-name-only duplicate guard. (Reference-branch
CanWrite is structurally covered by the single gate before the branch; untested sub-case noted.)

#### PH2-003: App-level XAFML diffs — decide where they live (ID: 539)

**Closed as a decision 2026-07-12 (owner) — module-level model IS the platform contract.** Module-level model
is projected; app-level `Model.xafml` customizations are invisible to the headless host, and that is now the
deliberate contract rather than an open gap. App-level customizations are out of scope: the seed grounds on
the OutlookInspired *module* (the host loads modules via `RequiredModuleTypes`, never an app project), and
every projection/render path is proven module-level. Apps wanting customizations honored should put them in a
module. Loading app-level XAFML remains a possible **Phase-2 feature** (host references the app project's
`Model.xafml`, merges its diffs before projection) if a real target app ever needs app-layer customization —
but it is not on the roadmap. No code.

#### PH2-006: Consolidations + stale comments

**Completed 2026-07-12** (commit `7f1f475`, interactive P3). Cleanup pass — comments + proven-equivalent
extractions, no behavior change. Extracted the byte-identical enum-value `Canon` from `GridBinding` +
`DetailBinding` into a shared `EnumValueCanon.Canonicalize` (`Components.Tests` 68/68 unchanged proves
equivalence); **deliberately did NOT fold in `EnumEditor`'s own `Canon`** — it genuinely differs (`string`
vs `string?`, no null/bool arms) so folding would change behavior. De-duplicated the twin `Flatten` test
helper into `MetadataTestHelpers`. Fixed the stale `SaveController.ApplyChanges` summary comment (now covers
the PH2-002 400/403 short-circuits) + a note on the write-deny guard, and removed a dead
`System.Globalization` using left by the extraction. The `AuthState`/`EditorMap` "stale comments" were
already correct (no churn). The `Config.Json 8.0.1` pin was **kept** with a rationale comment — removing it
breaks the net10 test build (no CPM to source a floating default), so it's a real requirement, not stale.
Two additive bits (`$metadata` cross-check, runtime-validation-in-`Required`) deferred, not built. Implementer
+ reviewer, review clean (SPEC ✅ / QUALITY Approved) — reviewer byte-verified the extraction is
behavior-identical and corroborated the finding below via DevExpress's own `ReferenceMemberModifier`. **Two
findings spun out to new TODO items:** the lookup-predicate divergence (→ `DATA-001`) and a pre-existing
parallel-test flake (→ `TEST-001`).

#### MT-001: Host self-seeds the tenant DB

**Completed 2026-07-12** (commit `35a54fc`, interactive P3). Added `.WithTenantDatabaseUpdater()` to the
`AddMultiTenancy` chain so a fresh machine self-seeds its tenant DB via the demo's own `DataGenerator`,
removing the manual "run the demo Blazor app once" step (deliberately omitted at migration time). The risk
(it triggers a version check against the already-seeded company1
tenant DB) was source-verified away: the updater is **lazy** (fires per-logon for the resolved tenant, at
most once per tenant per host process, `ConcurrentDictionary`-guarded), and the demo's `DataGenerator.Execute()`
is gated on `GetObjectsCount(Customer)==0` — so it **cannot** re-seed a populated tenant, and the
`Ensure*User/Role` helpers only mutate newly-created objects. **No-op proof on company1:** clean host start
(no `DatabaseVersionMismatch`), `Api.Tests` 46/46, every company1 count unchanged **except** Orders (+3 =
`SaveCreateTests` create/cleanup churn, not a bulk reseed — a reseed would be +55,000 and move every count),
`api/odata/Order?$top=1` still returns a real row. The fresh-tenant (`company2`) self-seed path is enabled
but not exercised (it would trigger the multi-minute 55k-row generation). One line + a verification comment;
committed after controller diff-review (the exhaustive source-verified no-op proof made a separate review
subagent unnecessary for a one-line config change).

#### GAP-008: Per-user layout customization (server-side, design intent)

**Completed 2026-07-12** (commit `05506af`, interactive P3 — owner chose the server-side design-intent
approach over client-localStorage). Per-user layout prefs that follow the user across devices. New host-owned
entity `UserLayoutPref {UserKey, ViewId, PrefsJson}` registered via `WithSharedBusinessObjects` (lives in the
host DB, **not** OData-exposed); new `GET/PUT api/prefs/{viewId}` (`[Authorize]`) keyed **strictly** to
`ISecurityStrategyBase.UserId` — the user key comes only from the authenticated identity, never the request,
so a user can only touch their own prefs (per-user isolation proven by a two-identity isolation test). Client
(Order_ListView only): DxGrid `LayoutAutoSaving`/`LayoutAutoLoading` persist column order+width via the
endpoint, auth-gated (GAP-007 timing); `FilterCriteria`/`SearchText` stripped (`CriteriaOperator` round-trips
unsafely via System.Text.Json per dxdocs). Tests: `PrefsTests` +3; `Api.Tests` 49/49; `Components.Tests`
68/68; E2E +1 (resize→reload→restored width) 4/4. Implementer + **opus** reviewer, review clean (SPEC ✅ /
QUALITY Approved) — the reviewer fully traced the isolation and source-verified the write path.
**Multi-tenancy note:** XAF makes shared host BOs read-only from a tenant-resolved request, so the write goes
through a fresh DI scope (`TenantId==null` host context) + `INonSecuredObjectSpaceFactory` (source-verified,
framework-only, cleanly disposed; the `userKey` LINQ predicate is the sole isolation layer — the isolation
test is its regression guard). **IMPORTANT operational caveat:** adding a host-owned entity to an *existing*
host catalog needs an EF Core migration — `CheckCompatibilityType.DatabaseSchema` behaves like
`EnsureCreated` (full schema on a fresh catalog, no incremental table add), so the disposable dev host catalog
was dropped+recreated (self-reseeds Admin/Tenant/TaxRate; the 55k tenant data untouched). A non-disposable
deployment would need migrations. **Minor follow-ups — DONE 2026-07-12** (commit `6170692`): a `(UserKey,ViewId)`
**unique index** (via `[DisableDeferredDeletion]` + a plain `[Index(IsUnique=true)]` — the DevExpress-shipped
pattern for a shared BO with no reachable `OnModelCreating`, mirroring DX's own `UserToken`; verified live,
`GCRecord` absent, unfiltered index — the "filtered on `GCRecord IS NULL`" idea was an XPO convention that
doesn't apply to this EF Core `BaseObject`), with a re-read-and-update retry on the concurrent-first-write
`DbUpdateException`; a **64 KB blob cap** (413 before any DB access); the E2E cleanup PUT **wrapped in
`finally`**; and a **nav-absence test** asserting `UserLayoutPref` + `LookupProbe` never appear in
`api/model/navigation`. Per-user isolation unchanged; `Api.Tests` 53/53, `Components.Tests` 68/68, E2E 4/4.

#### SEC-002: Clean up the leftover restricted POC account (the original POC's dev database)

**Completed 2026-07-12** (interactive P3 — DB-admin op, not a repo change). Deleted the empty-password
`HeadlessPOC_Restricted` role + user left in a separate dev database by the original 25.2 POC. First **verified it
was orphaned** — the current 26.1 tests use a different fixture (`restricted@company1.com` in the disposable
demo DB), and `testsettings.json` confirms it — and that removing it had **zero collateral** (0 other users
linked to the role). Removed it with a single tightly-ID-scoped transaction (`XACT_ABORT`/`TRY-CATCH`, so any
FK error rolls back with nothing half-deleted): the user, its login-info, its 2 role-links, then the role and
its 1 type-permission + 1 member-permission. Verified after: user + role gone (0 rows), `Default` role intact,
16 users remain, and **no dangling links reference the deleted IDs**. (Owner picked "delete" over
password-protect / admin-UI. One `sqlcmd` gotcha: the delete first failed on `QUOTED_IDENTIFIER OFF` against
XAF's `GCRecord` filtered indexes — the transaction rolled back cleanly — and succeeded with
`SET QUOTED_IDENTIFIER ON`.) **FYI surfaced, not touched:** 2 unrelated pre-existing orphaned link rows from a
*different* long-deleted user remain in that DB — the production app's own data hygiene, out of scope here.

#### DATA-001: Reconcile the lookup-classification predicate (turned out to be a non-bug)

**Completed 2026-07-12** (commit `e3025a0`, follow-up fix — owner chose "reconcile + guard"). PH2-006 flagged
`ViewMetadataProjector`'s `ClassifyDataType` vs `ProjectLookup` lookup predicates as a latent divergence, but
**that was XPO-style reasoning — it is a non-bug for the EF Core provider.** Source-verified
(`EFCoreTypeInfoSource.InitTypeInfo`) that EF Core **co-sets `IsDomainComponent = IsPersistent = true` on
every mapped entity**, so the two predicates were always equivalent and cannot disagree; the `IsAssociation`
clause was dead for entity references. A genuinely inverse-less reference (`LookupProbe.Ref`, `IsAssociation
== false` verified live) already classified consistently on the *old* code — no RED was achievable. Landed as
a **behavior-preserving cleanup + guard**: one shared `IsLookupMember` predicate
(`!IsList && (MemberTypeInfo.IsDomainComponent || MemberTypeInfo.IsPersistent)`) used by both call sites so
they can never disagree by construction (drift-proof, and would matter if the provider ever changed); plus a
**Development-only** dev-gated `LookupProbe` host fixture (absent from production) providing the model's only
inverse-less-reference coverage, with 2 consistency-guard tests (green pre- and post-change). Behavior-
preserving: every demo lookup member unchanged; `Api.Tests` 51/51, `Components.Tests` 68/68. **PH2-006's
Direction-A recommendation is corrected** (Direction A is unreachable for EF; Direction B — a non-persistent
DomainComponent target — would need a host module / `AdditionalExportedTypes` path this host doesn't have).

#### TEST-001: Parallel-test same-row race flake

**Completed 2026-07-12** (commit `d0177e8`, follow-up fix — test-only). `SaveReferenceAndEnumTests` and
`SaveCreateTests` mutate/restore shared Order rows and, under MSTest method-level parallelism, raced on the
same `(row, member)` via `$top=1`-no-`$orderby` "first row" picks — an intermittent read-after-write flake
seen across several runs. Fixed with **`[DoNotParallelize]`** on both mutating classes (MSTest never runs a
`DoNotParallelize` class concurrently with anything, so no two mutating tests overlap; the read-only metadata
tests stay parallel), plus a **deterministic `$orderby=InvoiceNumber asc`** on the 5 mutating row-pick queries
so the target row is stable and never an in-flight created/deleted Order. Verified with **5 consecutive full
`Api.Tests` runs green** (51/51 each, 255/255, 0 failures); no product code touched.

#### GAP-002: Conditional appearance projection (per-row colors + enum rules)

**Completed 2026-07-13** (feature commit `3965d0a`, enum-fix commit `9f77fd2`). The last capability gap:
project the model's declarative `[Appearance]` `ViewItem` rules and paint per-row colors/styles in the
client. **Owner scope:** colors (not the demo's strikeout) + reliable enum rules.

**Spike first** (`do the spike on the client criteria evaluator`, recorded on the old TODO note): client-side
criteria eval is mechanically trivial — `DevExpress.Data.Filtering.ExpressionEvaluator` +
`EvaluatorContextDescriptorDefault(typeof(object))` evaluates a parsed `CriteriaOperator` over the grid's
`ExpandoObject` rows natively (~3 LOC; DX's default descriptor special-cases `ExpandoObject`). The spike named
the two real ceilings — a criteria referencing an absent member throws, and enum literals need a caption↔name
reconciliation — both closed below.

**Server (`ViewMetadataProjector`):** `ProjectAppearance` reads the conditional-appearance rules from the model
via `AppearanceController.GetRulesFromModel` (rules live on `IModelClass`; the `ConditionalAppearance` module
loads through `RequiredModuleTypes` even though the WebApi builder never calls `AddConditionalAppearance`),
honors `Context` exactly like `AppearanceController.IsRuleFitToContext`, and emits `FontColor`/`BackColor`/
`FontStyle` (colors as `#RRGGBB`) on both list and detail metadata. **Client (`AppearanceEvaluator`):**
evaluates each rule's `Criteria` per grid row with the framework `ExpressionEvaluator` and applies the
color/background/font-style via `DxGrid.CustomizeElement`; a missing-member guard (+ try/catch) stops a rule
that references an absent member from crashing the row-render loop.

**Two verified corrections to the original premise:** (1) OData returns the enum member **NAME** (not the
caption), so the enum-literal rewrite is implemented in the corrected caption→name direction (proven
load-bearing against synthetic metadata); (2) appearance criteria routinely reference **non-displayed** members
(e.g. `Evaluation.Rating`), so `MaterializeRow` additively carries criteria-referenced members into each row
from the raw OData JSON (no effect on rule-less grids).

**Enum-fix (`9f77fd2`)** closed the one reliability gap the review found: the caption→name rewrite was
structurally inert for an enum-typed criteria member that **isn't a displayed column** — `enumsByMember` was
built only from column metadata, so `Evaluation.Rating` (referenced by `Rating='Good'` but not an
`Employee_Evaluations_ListView` column) had no rewrite channel and worked only by the demo's caption==name
coincidence. Fix (additive): the projector now emits `ViewMetadata.AppearanceEnums` — enum metadata for the
class's non-displayed enum members — but only when the view has appearance rules (nullable/default, no cost for
rule-less views); the client merges it into `enumsByMember` (column metadata wins on conflict). Enum appearance
rules now work for **any** caption/name split, not just where they happen to match.

**Demo/proof:** `Evaluation.Rating='Good'` → green in the `Employee_DetailView` Evaluations nested grid,
asserted by computed color `rgb(0,128,0)` in the E2E + screenshot; the `1=1` `StartOn` bold rule too. Tests:
`Api.Tests` **58/58**, `Components.Tests` **76/76** (incl. multi-word-enum, missing-member, and the non-column
enum-rewrite case), E2E **5/5**. Build clean. Implementer (opus) + reviewer + focused enum-fix subagent; the
review's one Important finding is the enum-fix, now landed.

#### BUG-001: `byte[]` members projected a broken `Byte_ListView` nested view (ID: 1219)

**Completed 2026-07-13** (owner hit it running the POC; first noted as a GAP-009 quirk). Some DetailViews showed
_"Failed to load view metadata: metadata request for 'Byte_ListView' failed"_ in a nested section.

**Root cause.** `ViewMetadataProjector.WalkViewItem` treated **every** `IsList` member as a nested
business-object collection, emitting a `nestedList` with `ViewId = "{ListElementTypeInfo.Type.Name}_ListView"`.
A `byte[]` member (`Customer.Logo`, `Product.Image`, `Employee.Picture.Data`) reports `IsList == true` with
element type `System.Byte`, so the id became **`Byte_ListView`** → `GET api/model/views/Byte_ListView` →
`ProjectListView` null → **404** → the client's inline error.

**Fix (reconciled with [[UI-001]]).** A new `IsBusinessObjectCollection` predicate — the list counterpart of
`IsLookupMember` — gates the `nestedList` branch on the element type actually being a domain-component /
persistent type (`ListElementTypeInfo` is an `ITypeInfo`; verified against installed 26.1 source
`DC/IMemberInfo.cs:71`). `ClassifyDataType` now maps `byte[]` → **`image`** (before the `IsList → collection`
line), so a blob projects as a scalar `image` **item**, not a nested collection; `GridBinding.VisibleColumns`
also skips `image` (a base64 blob isn't a grid cell). Any other non-BO `IsList` member is omitted (no real
nested view to fetch). Genuine BO collections (`OrderItems`, `Evaluations`, `Customer.Orders`) are unchanged.
The client already degrades an unknown editor type to a graceful badge, so the `image` item renders safely
until UI-001 adds the `<img>` editor.

**Verified live** (all four demo DetailViews): `Logo`/`Image`/`Picture.Data` now project as `image` items, every
real collection still nests, and **no `Byte_ListView` anywhere**. Test:
`Byte_array_member_is_an_image_item_not_a_broken_nested_view` on `Customer_DetailView` (byte[] `image` item +
no `Byte_ListView` nestedList + real collections still nested). `Api.Tests` **59/59**, `Components.Tests`
**76/76**. Files: `ViewMetadataProjector.cs`, `GridBinding.cs`, `KnownModel.cs`, `DetailViewMetadataTests.cs`.

#### UI-001: Client UI enhancement — DevExpress theme + image rendering + chrome polish (ID: 1220)

**Completed 2026-07-13** (owner: _"the colors and styling is very minimal compared to a default XAF app"_).

**Root cause of "minimal."** The app registered the **Fluent** theme (Design-System-based, CSS-variable driven)
AND separately loaded vanilla `bootstrap.min.css`. Fluent styles the DevExpress components but **not** native
HTML / Bootstrap chrome, so the surrounding app (nav, login, labels, buttons) fell back to plain Bootstrap —
reading as bare. Verified the 26.1 theming model via dxdocs (401523): **Classic** themes (Office White, Blazing
Berry, …) bundle Bootstrap CSS and style both DevExpress components *and* Bootstrap-classed chrome; Fluent does
not.

**What changed:**
- **Theme (`App.razor`)** — switched `RegisterTheme(Themes.Fluent)` → **`Themes.OfficeWhite`** (Classic) and
  **dropped the redundant `bootstrap.min.css` link** (the Classic theme provides Bootstrap; loading both
  conflicts). One theme now styles the whole app cohesively. Swapping to Blazing Berry / Purple / Blazing Dark
  is a one-liner.
- **Image rendering (`ImageEditor.razor` + `EditorMap`)** — closes the other half of [[BUG-001]]: an `image`
  member (`byte[]`) renders as an `<img>` data-URI (MIME sniffed from the base64 magic-byte prefix, per the
  magic-bytes rule) instead of the "unsupported editor" badge. **Verified live: the ACME `Customer.Logo`
  renders as a real picture.** A nested-path blob that OData doesn't expand (`Employee.Picture.Data`) degrades
  to a clean "(no image)".
- **Chrome (`app.css`, `Login.razor`)** — colored top bar, styled sidebar with an active-item highlight, a
  centered login card, image sizing. All keyed off the theme's own `--bs-*` variables so it tracks the theme.

**Verified with Playwright** across both render modes (Server + WebAssembly): login card, styled Employee list
(sidebar active-state + themed grid), themed Employee/Customer detail (editors, tabs, nested grids), and the
live ACME logo image. `Components.Tests` **76/76**, build clean. Files: `App.razor`, `ImageEditor.razor`,
`EditorMap.cs`, `app.css`, `Login.razor`. Surfaced **BUG-002** (nested tab over a non-OData-exposed child type
shows a raw 404 — pre-existing, tracked separately).

#### DOC-001: Overhaul `docs/HOW-TO-IMPLEMENT.md` (ID: 1221)

**Completed 2026-07-13.** The guide (`6bfa3dd`) predated the platform's full capability set and framed
**multi-tenancy** as the default — because the reference module (`OutlookInspiredDemo.Module`) is tenant-aware
and forced it. Reworked per the owner's steer (_"multi-tenancy is important but most use-cases will be single
tenancy"_):
- **Single-tenancy is now the common path** shown throughout (one DB, plain username logins, no
  `AddMultiTenancy`); multi-tenancy is a clearly-marked **Multi-tenant** box wherever it changes the setup, with
  its extra costs (shared-BO read-only writes, tenant-email logons, self-seeding) called out there. The
  gotcha index tags the two tenancy-only rows **[MT]**.
- **Reconciled with shipped code:** added the `byte[]` → `image` handling (the `IsList` trap / [[BUG-001]]),
  the omit-non-exposed-nested-child rule ([[BUG-002]]), and the DevExpress **theme** guidance (Fluent leaves
  chrome bare → use a Classic theme like Office White; [[UI-001]]) — three new gotchas (25–27) plus inline
  coverage in Steps 2 and 5.
- **Added a "Run the reference first" section** — the VS multi-startup (`XafHeadless.slnLaunch`, both http,
  `Admin@company1.com`/blank, LocalDB `XafHeadlessDemo`) so a reader can see the working app before building.
- Framed Step 4 (commands) explicitly as the headless answer to ViewControllers (parameterized actions →
  command endpoints; grid filtering → server-side `$filter`).

Docs-only; no code change. File: `docs/HOW-TO-IMPLEMENT.md`.

#### BUG-002: Omit nested tabs over a non-OData-exposed child type

**Completed 2026-07-13** (found while verifying UI-001; owner chose fix (a) — omit the tab). A nested
collection whose child type isn't independently OData-queryable produced a raw
_"Failed to load data: net_http_message_not_success_statuscode_reason, 404"_ tab — e.g. `Customer.Employees`
is `ObservableCollection<CustomerEmployee>`, and `CustomerEmployee` is a join type never passed to
`options.BusinessObject<T>()`, so the nested grid's `api/odata/CustomerEmployee` fetch 404s (the
"only explicitly-exposed types are OData-reachable" ceiling, now visible in the UI).

**Fix.** `ViewMetadataProjector` now reads `IOptions<WebApiOptions>.BusinessObjects` into an `exposedTypes`
set (the same signal `NavigationProjector` uses — NOT the broader EDM entity-set list) and `WalkViewItem`
omits (`return null`) any business-object `nestedList` whose `ListElementTypeInfo.Type` isn't exposed. An
unreachable tab simply doesn't appear — model-driven, consistent with the GAP-004 nav filter. DI resolves
`IOptions<WebApiOptions>` automatically (as it already does for `NavigationProjector`); no Startup change.

**Verified live** on `Customer_DetailView`: the `Employees` (`CustomerEmployee`) and `Quotes` tabs are gone,
while `Orders` (Order) and `CustomerStores` (CustomerStore) stay. Test:
`Nested_collection_over_a_non_exposed_child_type_is_omitted`. `Api.Tests` **60/60**. Files:
`ViewMetadataProjector.cs`, `KnownModel.cs`, `DetailViewMetadataTests.cs`.

#### GRID-001: Grid column chooser + group-by box + header context menu (client-side)

**Completed 2026-07-13** (owner: the grid lacked the classic XAF affordances — column chooser + group-by box +
the column-header sub-menu; chosen approach: **capped client-side load**, with server-side grouping deferred to
[[GRID-002]]).

**Approach.** `DxGrid` does grouping/sorting/filtering/paging + the column chooser client-side, which requires
the data in memory (a server-mode `GridCustomDataSource` can't group — verified dxdocs 403737). `XafListView`
now binds each list to an in-memory result set capped at `RowCap = 5000` and enables `ShowGroupPanel="true"`
(the group-by box) + `ContextMenus="GridContextMenus.Header"` (the right-click sub-menu: Group By Column /
Column Chooser / sort / Hide Column / Filter Builder). The server still does the security trim, the
master-detail filter, and the default order; only display-shaping moved to the client.

**Two fixes found while building (systematic-debugging):**
- **OData `MaxTop`**: the WebAPI capped `$top` at **100** (`Startup.cs` `EnableQueryFeatures(100)`), so the
  single `$top=5000` fetch 400'd ("The limit of '100' for Top query has been exceeded"). Raised to 5000 to
  match `RowCap` (reads are permission-trimmed and the client caps the load; writes stay blocked by
  `ODataReadOnlyMiddleware`).
- **ExpandoObject binding**: bound as `List<IDictionary<string,object?>>`, `DxGrid` used reflection for
  field access and threw _"property 'Name' not found in ExpandoObject"_ on every row (empty grid). `DxGrid`
  picks its field-access strategy from the collection's **static element type** — binding as
  `List<ExpandoObject>` (the concrete type) makes it use dictionary access (what the old `GridCustomDataSource`
  got from `DataItemType = typeof(ExpandoObject)`).

**Large views**: a view over `RowCap` loads only the first `RowCap` rows and shows a "Showing the first N of M
rows" note; correct server-side grouping for those is **GRID-002**. `ODataGridDataSource` +
`ODataFilterTranslator` are retained (currently unused on the render path) as that server-side foundation.

**Verified live with Playwright**: Employee list renders all 51 rows, the header context menu shows Group By
Column + Column Chooser, "Group By Column" on Department produces 7 collapsible groups, and Order shows
"Showing the first 5,000 of 55,000 rows". `Api.Tests` **60/60**, `Components.Tests` **76/76**. Files:
`XafListView.razor`, `Startup.cs` (MaxTop), `ODataGridDataSource.cs` (retained-note).

#### GRID-003: Auto filter row for all column types

**Completed 2026-07-13** (owner: complete the auto filter row). The per-column filter row rendered
(`ShowFilterRow="true"`) but was **scalar-only** — enum/lookup columns had their filter editor hidden by a
GAP-005 leftover (`FilterRowEditorVisible="@(c.DataType != "enum" && c.Lookup is null)"`), a restriction that
only mattered when the filter was translated to **server-side** OData `$filter` and an enum caption / flattened
lookup display couldn't be mapped back to the real member.

Since [[GRID-001]] the grid filters **client-side** over the in-memory rows, where an enum cell holds its
caption and a lookup cell holds the flattened display string — both of which `DxGrid` filters as text with no
translation. So the `FilterRowEditorVisible` gate is removed and **every** displayed column now gets a filter
editor. (Collection/image columns are still excluded upstream by `GridBinding.VisibleColumns`.)

**Verified live with Playwright**: on `Employee_ListView`, the Department (**enum**) column — previously with no
filter editor — now filters correctly (`Management` → exactly the 4 managers: CEO/CTO/CMO/COO); on
`Order_ListView` the **lookup** columns (Customer/Store) and numeric columns now show filter editors too.
`Components.Tests` **76/76**. One-line change in `XafListView.razor`. (Separate pre-existing quirk, not in
scope: `Order`'s `Store` lookup DisplayMember is `Emblem`, which has no text to show. **Corrected 2026-08-08
by GRID-005:** `Emblem` is a *reference to the `Emblem` entity*, not the `byte[]` this line originally called
it — the cell is blank because the display path resolves to an object. This line is where that
misidentification entered the record and was carried into BUG-006.)

#### GAP-010: Link/Unlink endpoints + `Aggregated` projection (server scope)

**Completed 2026-07-13** (owner chose "server + aggregation projection only now"; the client Link picker waits
for the MIG-002 write-capable lookup editor — GAP-010 stays open in `TODO.md` for that UI). Nested lists needed
the to-many **association** write path: **Link** = associate an *existing* object with the master's collection;
**Unlink** = remove the association **without deleting** the object.

**Investigation.** Verified XAF's mechanics against installed 26.1 source (`SystemModule/LinkUnlinkController.cs`):
`LinkObjectsCore` → `collectionSource.Add(obj)`, `UnlinkObjectsCore` → `collectionSource.Remove(obj)`; the
aggregation signal is `IMemberInfo.IsAggregated`. Finding: **every laid-out demo collection reports
`IsAggregated == true`** (XAF infers aggregation from the relationship, not just the `[Aggregated]` attribute —
even `Customer.Orders`), so there's no laid-out shared collection to demo Link/Unlink in the browser. The
non-aggregated many-to-many `Employee.AssignedEmployeeTasks` (↔ `EmployeeTask.AssignedEmployees`) is the
API-test target.

**Server (`LinkController`).** `POST`/`DELETE api/link/{type}/{key}/{member}/{childKey}`: opens a secured
ObjectSpace, resolves master + child by key, and `Add`/`Remove`s the child on the master's collection member —
the ORM handles the join row (many-to-many) or FK (one-to-many). Rejects a non-collection or **aggregated**
member (400, `IsAggregated`), gates on `CanWrite` for the member (403), and commits through the **same
`IValidator` → 422** contract as `SaveController` (OData `$ref` linking is blocked by `ODataReadOnlyMiddleware`).
Link is idempotent.

**Projection.** `LayoutNode.Aggregated` (nullable, nestedList-only) now carries `IMemberInfo.IsAggregated` so the
client can eventually offer Link/Unlink (shared) vs New/Delete (owned).

**Verified.** Live round-trip proven (baseline 0 → LINK 200 → 1 → UNLINK 200 → 0, task not deleted).
`Api.Tests` **64/64** (+4): link↔unlink round-trip via OData `$expand` (+ task still exists after unlink),
`Aggregated` projects true on `Employee.Evaluations`, link on an aggregated collection → 400, unknown master
type → 404. `Components.Tests` **76/76**. Files: `LinkController.cs` (new), `ViewMetadataProjector.cs`,
`ViewMetadataDtos.cs`, `Contracts/ViewMetadata.cs`, `KnownModel.cs`, `LinkTests.cs` (new).

#### GRID-002: Server-side grouping/paging for large views (OData `$apply`) — done 2026-07-14

**Landed via a backport of the companion headless implementation's GRID-001 work** (developed and gated
there; transplanted here with namespaces swapped, comments adjusted for repo truth).

**Hybrid binding.** `XafListView` runs a `$count=true&$top=0` probe per view: at or below
`RowCap` (5,000) the unchanged capped in-memory bind stays; above it the view binds through
`ODataGridDataSource` (`GridCustomDataSource`) — true server paging (`$top/$skip/$orderby`), the
filter row as a real `$filter` (scalar columns; enum/lookup filter cells render disabled with a
tooltip), and **server-side grouping**: `GetGroupInfoAsync` answers each grouping level with one
`$apply=[filter(...)/]groupby((path),aggregate($count as Count))` fetch; expanding a group pages
its children through the ordinary `GetItemsAsync` path (the grid AND-bakes the group criteria into
`FilterCriteria` — verified against installed 26.1 source). Groupable ceiling (live-probed):
non-date scalars + lookups (nav display path); enums/dates excluded — `DxGridDataColumn.AllowGroup`
gates the UI to the same ceiling. A `GroupSummary` Count item shows the true per-group total (served
from the same bucket). The "Showing first N of M" cap banner is gone — server mode has a real pager.
**Order (55k) now routes server-mode** (wire-verified during the E2E: count probes, 25-row pages
with `$orderby` + `Store($select=Emblem)` expand, `contains(InvoiceNumber,...)` filter over all rows;
`$apply=groupby((ShipmentCourier),...)` probe: buckets 13668+13670+13965+13697 = 55,000 exactly,
each equal to its independent `$filter` count).

**Backport also fixed a live crash found here:** a persisted GAP-008 pref blob can deserialize with
`Columns == null` (System.Text.Json `WhenWritingDefault` trimming) — `SanitizeLayoutForServerMode`
now no-ops on it instead of throwing an `ArgumentNullException` that killed the whole circuit inside
`OnLayoutAutoLoading` (repro: poison pref `{"PageIndex":1}` on `Order_ListView`; regression-checked
live after the fix, plus a unit test).

**Verified.** `dotnet build XafHeadless.slnx` 0 errors; `Components.Tests` **89/89** (was 76; the
transplanted suite includes the hybrid-binding, grouping-translation, and sanitize tests);
`XafHeadless.E2E` **5/5** against freshly published hosts (dual-render-mode smoke, new-views sweep,
nav menu, column-resize persistence — Order's flows now exercise the server-paged binding end to
end). Files: `XafListView.razor`, `GridBinding.cs`, `ODataGridDataSource.cs`, `ODataQueryBuilder.cs`,
`ApiClient.cs`, `GridBindingTests.cs`, `ODataQueryBuilderTests.cs`.
