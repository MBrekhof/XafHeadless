# Deviations Log

Deviations from the written plans, with the verification that justified each. Newest first.

## SVR-003 — OData constant-parameterization fix for host-shared BOs (Dispatch H's "NOT fixed" finding, now FIXED) + deferred items

**Resolves Dispatch H's Finding 2 below** (host-shared `$filter`/`$top` reading wrong data). Root cause was
proven by an in-process investigation (see `docs/notes/devexpress-ticket-odata-shared-bo.md`): ASP.NET Core OData's `[EnableQuery]` parameterizes every
`$filter`/`$top` literal into a `LinqParameterContainer.TypedProperty`, which resolves to `default(T)` when
the query runs on the MultiTenancy **standalone shared-BO DbContext** (`.WithTenantResolver` →
`IDBContextSwitcher.UseStandaloneDBContext=true`). So string filters matched `null`, Guid filters matched
`Guid.Empty`, `$top` became `Take(0)`, etc. Per-tenant types (`Order`) run on the DI-registered tenant
context and were unaffected.

**Fix taken (owner decision — global, not per-type):** a global MVC `IApplicationModelConvention`
(`XafHeadless.Api\Infrastructure\HostSharedODataQueryConvention.cs`, registered via
`AddControllers(o => o.Conventions.Add(...))`) walks every controller/action and sets
`EnableConstantParameterization=false` on each `EnableQueryAttribute` it finds. Verified against installed
26.1 source (`DevExpress.ExpressApp.WebApi/Mvc/DataController.cs:129-134` carries the plain `[EnableQuery]`
on the generated `Get()`/`Get(key)`) and against `Microsoft.AspNetCore.OData` 9.3.2 by reflection
(`EnableQueryAttribute : ActionFilterAttribute`, `EnableConstantParameterization` is a **public settable**
`bool`, so the attribute instance in `ActionModel.Filters` is mutated in place — **no subclass fallback was
needed**). Live before/after against the seeded row (Admin): `$top=1` 0→**1**, `$filter=ID eq <guid>`
0→**1**, `$filter=JobTypeName eq 'EmailOrdersReport'` empty→**1**, `$filter=IsEnabled eq true` row→**empty**
(predicate now honored, not ignored), `$filter=IsEnabled eq false` **1**, `$skip=1` row→**empty**; `Order`
`$top`/`$orderby` unchanged (no regression). Inlining literals is a no-op for correctness on per-tenant types
(EF Core re-parameterizes during its own SQL generation — DB param-safety unchanged, only OData-layer
plan-cache reuse is lost — negligible for this demo).

**(a) DevExpress support ticket — SHOULD BE FILED.** This convention is an app-side **workaround**, not a
framework fix. The underlying defect — OData's `LinqParameterContainer.TypedProperty` resolving to
`default(T)` when the translated expression executes against the `UseStandaloneDBContext` shared-BO context
— lives in EF Core / DevExpress MultiTenancy plumbing this project does not own. File a ticket so the
framework evaluates the parameter container correctly on that context (which would let constant
parameterization stay on).

**(b) Deferred (part of the SAME ticket): `$select` → `ArgumentNullException(edmModel)` on host-shared
types.** `$select` on `JobDefinition`/`JobExecutionRecord` throws
`System.ArgumentNullException: Value cannot be null. (Parameter 'edmModel')` during OData serialization — a
SEPARATE bug from the `$filter`/`$top` parameterization one. **Non-blocking:** the client never sends
`$select` (the grid uses `$top`, the DetailView uses `$filter=ID eq`, both now fixed; single-entity
`({key})` reads never used `$select`). The only place `$select` appeared was `SaveDeleteTests.cs`'s
JobDefinition read-back, which SVR-003 dropped (the `({key})` path already returns the full entity). Left
unfixed by owner decision — bundle it into the (a) ticket.

**(c) `$metadata` double-`<EntityContainer>` — observation only, NOT the cause.** The EDM `$metadata`
emits two `<EntityContainer>` blocks (every set/type duplicated). This affects `Order` too, and `Order`
works, so it is not the cause of the `$filter`/`$top` gap (likely related to the `$select` bug). No fix in
this dispatch — noted for the same ticket's context.

**Also in this dispatch (not deferred):**
- **SaveController → 409 on duplicate (Dispatch H's A1/A2 500s).** `CommitWithValidation` now also catches
  `DbUpdateException` and returns **409 Conflict** with the same structured `{ MemberErrors, Messages }`
  JSON the 422 path uses, narrowed via a `when` filter to a genuine unique-index/PK violation
  (`Microsoft.Data.SqlClient.SqlException.Number` 2601/2627 — the numbers SVR-002 proved live); any other
  `DbUpdateException` falls through and propagates so real failures still surface. One guard covers
  Create + Save + all callers. The 2 JobDefinition tests now send a unique `JobTypeName`; a new
  `Create_duplicate_JobTypeName_returns_409_conflict` asserts the contract. `XafHeadless.Api.Tests` 71/71.
- **Dev host catalog reprovisioned via the Api (fixes SVR-002's partial-schema side effect).** SVR-002 had
  rebuilt `XafHeadlessDemo` by booting only the JobServer (narrower shared set), leaving `UserLayoutPref`/
  `LookupProbe` absent → 3 Prefs tests failing with `Invalid object name 'UserLayoutPref'`. Dropped
  `XafHeadlessDemo` and reprovisioned by booting the **Api first** (its shared set builds
  `UserLayoutPref`/`LookupProbe`/`TaxRate`/`JobDefinition` with the fresh `IX_JobDefinition_JobTypeName`
  unique index/`JobExecutionRecord`), **then the JobServer** (adds `ReportArtifact`/`EmailArchive` and
  seeds the one `JobDefinition` row). Verified: all 7 shared tables present, exactly 1 JobDefinition row,
  duplicate insert → `Msg 2601` on `IX_JobDefinition_JobTypeName`. Same disposable-dev-DB precedent as
  SVR-002/GAP-008; the tenant catalog `OutlookInspiredDemo_company1` was untouched.

## SVR-001 — Dispatch H (Task 6.1) findings: nav-generation ordering gap (fixed) + a deeper OData query gap (NOT fixed, escalated)

**Finding 1 — fixed.** `JobDefinition`/`JobExecutionRecord` never appeared in `api/model/navigation`
even though both carry `[DefaultClassOptions]`. Live probe (`api/diagnostics` extended temporarily,
since reverted): the model's `IModelClassNavigation.IsNavigationItem` was `true` and `DefaultListView`
was set for both types, `CanRead(type, os)` was `true` for Admin, yet the generated
`NavigationItems.AllItems` tree (XAF's `SystemModule.NavigationItemNodeGenerator`, which walks
`Application.BOModel.GetUnsorted()`) had only 11 items and neither type was among them. Root cause:
`.WithSharedBusinessObjects()` merges these types into `BOModel` at a point that evidently runs after
the SystemModule's Default-nav-group generator already built and cached the `NavigationItems` node for
the shared Application singleton — a genuine XAF/MultiTenancy model-generation ordering gap, not
something `NavigationProjector`'s existing rules could route around. **Fix** (`NavigationProjector.
ProjectNavigation()`): after the normal walk over the generated tree, a second pass over
`model.BOModel.GetUnsorted()` picks up any `IsNavigationItem==true` class the first pass never
produced an item for, using its `DefaultListView`, and applies the SAME rule-2/rule-3 (OData-exposed +
CanRead) checks. Verified live: `api/model/navigation` now includes both `Job Definition` and
`Job Execution Record`; `dotnet test XafHeadless.Api.Tests` stays at the same 65 passed/5 failed split
before and after (the 5 failures are pre-existing — see below).

**Finding 2 — NOT fixed, flagged for a follow-up dispatch.** Even with nav fixed, `JobDefinition`'s
ListView renders **zero rows** in the client (and a DetailView load by key fails with "No JobDefinition
found with key ..."), despite a real seeded row (confirmed via direct `sqlcmd` against the host
`XafHeadlessDemo` catalog) and `CanRead=true`. Live repro against `api/odata/JobDefinition` as Admin:
- No query options at all → returns the row.
- `$top=N` (any N, with or without `$orderby`) → `value: []` (but `$count=true` alongside still reports
  the correct count of 1 — the count and the paged value diverge).
- `$filter=ID eq <guid>` (the client's exact `DetailBinding.KeyFilter` shape, no `$top`) → `value: []`.
- `$filter=JobTypeName eq 'EmailOrdersReport'` (a **string** member filter, no `$top`) → `value: []`.
- `$filter=IsEnabled eq false` (a **bool** member filter, no `$top`) → returns the row correctly.

So the trigger isn't "any query option" — bool predicates work, string/GUID predicates (and `$top`)
don't. This affects both host-shared types added by this dispatch (`JobDefinition`,
`JobExecutionRecord`) and did not reproduce on a normal per-tenant type (`Order` `$top=1` returns data
fine). This sits inside `AddXafWebApi`'s built-in OData controller + the MultiTenancy
`SharedBusinessObjects`/`DBContextSwitcher` "standalone DbContext" plumbing (`DevExpress.ExpressApp.
MultiTenancy.EFCore`) — not application code this project owns, and not something `NavigationProjector`-
style app-layer patching can route around. **Practical impact:** the client's `GetPageAsync` always
sends `$top` and `XafDetailView`'s `Load()` always sends `$filter` (the key predicate), so the
"zero-new-code CRUD" compression claim does **not** fully hold for `JobDefinition`/`JobExecutionRecord`
— the grid can't display rows and a DetailView can't be opened by key through the normal client flow,
for any host-shared type registered this way. The underlying command mechanics are unaffected: a direct
`POST api/commands/EmailOrdersReport` still returns `"Job enqueued."` and the JobServer still drives the
seeded row's `JobExecutionRecord` to `Status=Success` with a real smtp4dev delivery (verified live,
Dispatch H) — only the **read-back through OData with `$filter`/`$top`** is broken for these two types.
Needs its own investigation (likely a support-ticket-level DevExpress question, or a custom
`ODataController` override scoped to these two types) rather than a fix bundled into a client dispatch.

## SVR-001 — Dispatch G (Task 5.1/5.2): `NextExecution` is epoch-ms; seed carries a recipient for the cron path

**`ScheduleSyncService.ReadNextRunUtc`** parses Hangfire's `recurring-job:{id}` hash `NextExecution`
field as a Unix epoch-**milliseconds** long FIRST, falling back to `DateTime.TryParse` only if that
fails — verified live: the field arrived as `"1784768400000"` (epoch-ms) on this host's SqlServer
storage, not an ISO string. A naive ISO-only parse would silently return null forever and
`JobDefinition.NextRunUtc` would never fill even though the recurring job is genuinely scheduled.
Same handling as the companion headless implementation's proven job scheduler source.

**Seed fix:** `HostDatabaseInitializer.SeedDemoJobDefinition` now sets `ParametersJson` on the seeded
row (`{"EmailRecipients":"demo-recipient@xafheadless.local"}`). Without it, enabling the row with a
cron makes `SyncScheduleByName` call `JobDispatchService.Deserialize(null)`, which Dispatch F's M-4
guard throws on ("EmailRecipients is required") — `SyncOnce` would abort every tick and `NextRunUtc`
would never fill. Only a fresh clone gets this via the seed; the existing dev-DB row needed the same
`ParametersJson` set via the Task 5.2 proof's enable PATCH (the seed's `GetObjectsCount>0` guard skips
reseeding an already-provisioned row).

## SVR-002 — unique index on `JobDefinition.JobTypeName` only takes effect on a freshly-created host catalog

**Not a plan deviation — a platform caveat, same shape as GAP-008's finding.** The platform here is
EnsureCreated-style (no EF Core migrations); XAF's per-table schema update against an **existing**
EF catalog (`IDBUpdater.Update()` on a schema that's already current) does not retrofit a newly-added
`[Index]` attribute onto an existing table — it only builds the full model, indexes included, when the
table is created from scratch. So a fresh clone gets `IX_JobDefinition_JobTypeName` for free the first
time its host catalog is provisioned, but an **existing** `XafHeadlessDemo` (as in this dev environment,
which had accumulated 8 duplicate `EmailOrdersReport` rows) needs either a drop+recreate of the host
catalog or a manual migration to gain the index.

**Taken:** dropped + recreated the disposable `XafHeadlessDemo` host catalog
(`ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ...`), then booted
`XafHeadless.JobServer` so `HostDatabaseInitializer` ran `IDBUpdater.Update()` against the empty
server — EF rebuilt the schema from the model (index included) and the demo host-branch seeder
re-registered tenant `company1` idempotently, and `HostDatabaseInitializer.SeedDemoJobDefinition`
reseeded exactly one `JobDefinition` row (`EmailOrdersReport`), resolving the 8 historical dupes as a
side effect of the recreate rather than a separate delete step. This wiped the catalog's other
disposable content (Hangfire's own auto-recreated schema/history, prior `JobExecutionRecord`/
`ReportArtifact` rows, `UserLayoutPref`/`TaxRate` rows) — all disposable per GAP-008's precedent. The
tenant DB `OutlookInspiredDemo_company1` is a separate catalog, untouched by the drop — verified
still present with its full 55,040-row `Orders` table after the recreate.

## SVR-001 — Dispatch F (Task 4.1/4.2): I-1 at-most-once email — residual ceiling, not exactly-once

**Carry-forward from Dispatch D's review (I-1).** `EmailService.SendEmailWithAttachmentsAsync` archives
best-effort after a successful send (logged, never rethrown), and `JobExecutor.RunAsync` wraps
`RecordCompletionAsync`/`RecordFailureAsync` in a best-effort try/catch so the recorder can never turn
an already-completed handler run into a retry trigger. `[AutomaticRetry(Attempts = 3)]` stays on —
retries remain valuable (and safe) for genuine pre-send failures (render, artifact write, SMTP connect).

**What this does NOT achieve — true exactly-once is out of demo scope:**
- A retry after a pre-send failure may leave a duplicate `ReportArtifact` row. Cosmetic — it's an
  internal audit blob, not user-facing, and acceptable for the demo.
- A process crash in the narrow window between a successful SMTP send and `RecordCompletionAsync`
  actually running can still cause **one** duplicate email on the next retry (the send already
  happened; nothing recorded that fact before the crash). Real exactly-once needs an idempotency key
  or a transactional outbox tying the SMTP send and the completion record together atomically —
  explicitly out of scope here. Documented honestly rather than claimed as fixed.

## SVR-001 — Dispatch F (Task 4.1/4.2): auth posture for `EmailOrdersReport` — accepted, not revisited

**Owner decision (2026-07-21), not re-litigated by this dispatch.** `EmailOrdersReportApiCommand`
stays `[Authorize]`-only like every other command endpoint — no admin gate added. Any authenticated
user may trigger the send. Blast radius is low: the recipient
(`demo-recipient@xafheadless.local`) is a hardcoded demo value baked into the command, not
user-suppliable from the request, so an authenticated-but-unprivileged caller can spam one fixed
inbox at most, never redirect mail elsewhere.

## SVR-001 — Dispatch F (Task 4.2): `xafheadless-smtp` — this repo's own smtp4dev, ports 2526/5312

This repo's own sink runs as `xafheadless-smtp` on 2526→25 (SMTP) and 5312→80 (web UI), started via
`docker run -d --name xafheadless-smtp -p 2526:25 -p 5312:80 rnwood/smtp4dev` (use a distinct port pair
if you run other smtp4dev containers). `appsettings.json`'s `EmailSettings:SmtpPort` is `2526`
accordingly. Left running after Task 4.2's live proof for a later dispatch's automated test to reuse.

## SVR-001 — Dispatch D (Task 3.2/3.3): report DATA source needs an authenticated object space; `NoOpJobScopeInitializer` is not sufficient for the render

**Plan/brief assumption (falsified live):** the brief's `NoOpJobScopeInitializer` header states "every worker-scope
write goes through `INonSecuredObjectSpaceFactory` ... nothing downstream needs an authenticated object space." True
for the report LAYOUT load and all shared-BO writes, but **false for the report's DATA source.** Empirically, the first
live run failed with `DevExpress.XtraReports.DataRetrievalException -> UserFriendlySecurityException: "The user name
must not be empty."` The stack shows ReportsV2 populates report data through a **secured** object space:
`DataSourceBase.CreateObjectSpace -> ScopedReportObjectSpaceProvider.CreateObjectSpace ->
SecuredObjectSpaceFactory.CreateObjectSpace -> EnsureLogon -> AuthenticationMixedV2.Authenticate` — which throws
without an authenticated user. (The layout load stays non-secured; only the data-fill needs auth.)

**Fix taken:** `ReportRenderService` logs on the demo's seeded tenant admin (`Admin@company1.com`, empty password,
`IsActive=1` — verified by querying `OutlookInspiredDemo_company1.PermissionPolicyUser`) **inside the tenant child
scope**, after setting `TenantId`, before the render. Same mechanism as the companion implementation's job
scope initializer
(`SecurityStrategy.Authentication.SetLogonParameters(new AuthenticationStandardLogonParameters(user, ""))` then
`SecurityStrategy.Logon(nonSecuredObjectSpace)`), but applied in the child render scope rather than the outer job
scope — because the report's secured object space is created there and must authenticate against the TENANT database
(where `Admin@company1.com` lives; `ApplicationUser` is tenant-scoped, not a shared BO). `NoOpJobScopeInitializer`
is retained for the OUTER job scope (the recorder uses only the non-secured factory, needs no logon), so the
`JobExecutor` dependency is still satisfied without a redundant host-scope logon.

**Note for later dispatches:** any background render of a demo report needs this tenant-scope logon; a NoOp scope
initializer alone will not render report DATA (only layouts / non-secured reads).

## SVR-001 — Dispatch D (Task 3.2): worker-scope tenant resolution + isolation — RESOLVED and confirmed live

Confirms and completes Dispatch A's forward note. Verified against installed 26.1 source, then proven live.

- **Tenant selection** (as the forward note predicted): `ReportDataV2` is a TENANT-scoped type (not in the shared-BO
  list), so it lives in `OutlookInspiredDemo_company1`. Set `ITenantProvider.TenantId =
  ITenantNameHelper.GetTenantIdByName("company1.com")` to flip `INonSecuredObjectSpaceFactory` routing to the tenant
  provider (`IsTenantSet` -> tenant branch; `MultiTenancy\ApplicationExtensions.cs:89-91`,
  `WebApiXAFApplicationBuilderWrapper.cs:70`). Tenant name `company1.com` / catalog `OutlookInspiredDemo_company1`
  confirmed from the host `Tenant` row.
- **The isolation risk was REAL.** `ITenantProvider` is registered **`AddScoped`**
  (`MultiTenancyCoreStartupExtensions.cs:50`), so setting `TenantId` mutates the *current DI scope's* provider. Had we
  set it in the shared Hangfire job scope, the recorder's LATER shared-BO write (`JobExecutionRecord`/`JobDefinition`)
  would see `IsTenantSet == true` and route into the read-only-shared-data branch
  (`MultiTenantObjectSpaceFactory.cs:100-104`, `SetupMultiTenantSecurityOptions(isReadonlyAccessToSharedData:true)`),
  silently failing to persist to the host catalog.
- **Resolution:** `ReportRenderService` does the tenant selection + logon + render inside a FRESH child DI scope
  wrapped in `IValueManagerStorageContext.RunWithStorageAsync` — exactly DevExpress's own non-request tenant pattern
  (`TenantDatabaseUpdater.cs:63-74`, `MultiTenantServiceScopeFactory.cs:56`). The outer job scope's
  `TenantProvider.TenantId` stays null, so the recorder's shared writes always hit the genuine writable host branch.
- **Live proof (2026-07-20):** one dispatch -> `JobExecutionRecord` row `Status=Success`, `DurationMs=2280`,
  `ErrorMessage=null`; the seeded `JobDefinition` ("Daily Orders Report") `LastRunStatus=Success` +
  `LastRunUtc=CompletedUtc` — i.e. the recorder's SHARED-BO write landed in the host catalog *after* the render set
  `TenantId` in its child scope (had isolation failed, LastRunStatus would still read `NeverRun`). The `ReportArtifact`
  row is 56876 bytes, `%PDF-` header + `%%EOF` trailer.

## SVR-001 — Dispatch D (Task 3.2): render keyed by stable `PredefinedReportTypeName`, not the GUID PK

The brief says to hardcode `OrdersReport`'s key ("its `DisplayName` or a stable identifier"). This host's tenant DB
is a disposable dev catalog whose `ReportDataV2` primary keys are sequential GUIDs regenerated on every re-seed, so a
hardcoded GUID would rot. `ReportRenderService` instead looks up by
`ReportDataV2.PredefinedReportTypeName == "OutlookInspiredDemo.Module.Resources.Reports.ProductOrders"` (a public,
SQL-queryable string on the EF storage type — `DevExpress.Persistent.BaseImpl.EFCore\ReportDataV2.cs:62`) — the
report's stable resource-type name, unique across the seeded rows. (The "Orders" report's transient GUID at
verification time was `58D168A2-707C-499D-C2B3-08DE924D8DC9`, recorded only for traceability.)

## SVR-001 — Task 2.3 finding: PermissionPolicyRole's Delete operation does not default-deny like Write does

Restricted (`TestFixturesController`, no explicit Delete permission on `Order`, only Read:Allow +
Write:Allow) successfully deleted a real Order row via the new `DELETE api/save/{type}/{key}` route --
discovered live, at the cost of one demo row (55,000 → 54,999, unrecoverable, deemed inconsequential
per `docs/notes/test-fixtures.md`'s "disposable DB" framing). Write, by contrast, is documented
(`SaveReferenceAndEnumTests.cs`) to deny everything absent an explicit type-level `Write:Allow`. This
is the demo module's own role configuration, not something this project changes -- `Order`'s
Restricted-role fixture now carries an explicit `Delete:Deny` (`TestFixturesController.cs`) so the
DELETE route's `CanDelete` gate has a real, safe (throwaway-row) test case.

## SVR-001-A — JobServer Task 1.2: full multi-tenancy fallback, not the two-plain-chains hypothesis

**Plan:** `docs/plans/2026-07-20-jobserver-plan.md`, Task 1.2. Working hypothesis: two independent
`ObjectSpaceProviders.AddSecuredEFCore().WithDbContext<T>()` chains under one `AddXafWebApi` builder,
with NO `AddMultiTenancy` (one plain chain for a dedicated `JobServerHostDbContext`, one for the
tenant `OutlookInspiredEFCoreDbContext`).

**Taken:** the plan's documented fallback — the Api's full multi-tenancy wiring, copied
(`AddMultiTenancy().WithHostDbContext().WithMultiTenancyModelDifferenceStore().WithSharedBusinessObjects().WithTenantDatabaseUpdater().WithTenantResolver<TenantByEmailResolver>()`).

**Why (verified against installed 26.1 source, not memory):** the demo's `OutlookInspiredModule`
cannot be hosted without multi-tenancy at all. `OutlookInspiredDemo.Module\DatabaseUpdate\Updater.cs:353`
(`UpdateDatabaseAfterUpdateSchema`) does
`ObjectSpace.ServiceProvider.GetRequiredService<ITenantProvider>().TenantName` — `GetRequiredService`
throws at startup CheckCompatibility unless `AddMultiTenancy` registered `ITenantProvider`. So the
blocker is not "two chains don't compose" (dxdocs article 404322, "Use Multiple Data Models Connected
to Different Databases in EF Core", confirms two `AddSecuredEFCore().WithDbContext<T>()` chains DO
compose) — it is that loading the module at all requires the multi-tenancy services. The Api's own
`Startup.cs` header (lines 26-34) documents hitting exactly this crash during its single-tenant spike.

**Consequences of the fallback shape:**
- **No `JobServerHostDbContext`.** Under full multi-tenancy the four SVR-001 BOs reach the host
  catalog via `.WithSharedBusinessObjects` (the proven `UserLayoutPref` path), so a dedicated host
  `DbContext` is unnecessary (it would be a second competing host chain). The plan's Task 1.2 Step 2
  file is intentionally not created. Downstream: a worker-scope `INonSecuredObjectSpaceFactory`
  request for `JobExecutionRecord` resolves via the shared-BO host routing rather than a dedicated
  context (relevant to Phase 3.2's recorder).
- **`TenantByEmailResolver` kept verbatim** (a DevExpress built-in in
  `DevExpress.ExpressApp.MultiTenancy`, not an Api type — so no project reference needed), rather than
  the plan's "hardcoded company1 resolver". It is never invoked on the boot path (tenant resolution is
  lazy per-logon; a worker has no logon). Worker-path tenant resolution for the eventual report render
  is a genuine Phase 3 problem, out of this slice's scope.
- **Boot safety verified:** this host adds 4 tables to the shared `XafHeadlessDemo` catalog, which
  triggers a schema-version mismatch → `e.Updater.Update()` → the demo host-branch seeder. That seeder
  is idempotent (`Updater.cs`: `CreateTaxRates` guarded by `.Any()`, `CreateTenant` guarded by
  `FirstOrDefault<Tenant>`, `EnsureUser`/`EnsureRole` are find-or-create), so re-running it against the
  Api-seeded catalog is a no-op — the Api's 46 tests are unaffected.

## SVR-001-A — New component: `HostDatabaseInitializer` (host-catalog provisioning at startup)

**Not in the plan.** The plan's Task 1.3 assumed the JobServer boot would auto-create the four
shared-BO tables ("XAF's schema auto-update against XafHeadlessDemo"). It does not — discovered during
the boot-test gate and verified against installed 26.1 source.

**Why the boot alone doesn't provision:** `XafHeadless.Api` gets its host schema for free because it
serves XAF/OData requests that create a host ObjectSpace and trigger `CheckCompatibility` (which, under
`CheckCompatibilityType.DatabaseSchema`, creates missing tables — dxdocs 113239). The JobServer serves
only `/health`, so that trigger never fires. Empirically confirmed: after a clean boot, `Hangfire`
schema existed (11 tables, installed by Hangfire itself) but the four BO tables did not. Two rejected
fixes, each verified not to work: (a) creating a non-secured host ObjectSpace + querying — throws
`Invalid object name 'JobDefinition'` (no schema update runs); (b) building a setup application and
calling `CheckCompatibility()` with `TenantId = null` — targets the in-memory *shared application*
(`UseInMemoryDatabaseForSharedApplication`), not the host SQL catalog.

**Fix taken:** a startup `IHostedService` (`HostDatabaseInitializer`) that resolves `IDBUpdater`
(`DevExpress.ExpressApp.Utils`) and calls `Update(forceUpdate: false, silent: true)`. This is the exact
mechanism the demo's own `OutlookInspiredDemo.Blazor.Server\Program.cs` runs for its `--updateDatabase`
command — it builds the application, iterates its ObjectSpaceProviders, and runs the schema+data update
on each (`IDBUpdater.cs` → `DBUpdater<T>.UpdateCore`). Running it automatically at startup means a fresh
clone needs no manual `--updateDatabase` step (consistent with MT-001's self-seeding philosophy).
Registered before `AddHangfireServer()` so tables exist before any worker runs. Boot-test evidence:
the four tables + correct column types (`ParametersJson`/`ErrorMessage` = `nvarchar(max)`, `Content` =
`varbinary(max)`) are created; on a current schema the updater returns "not needed".

**Note for later dispatches:** the host-provisioning gap is general — any future JobServer boot that
adds host BOs relies on this initializer, and the Api side (Dispatch B, Task 2.1 Step 3) provisions the
same tables via its own request-driven trigger.

## SVR-001-A — Minor deviations (packages / scope)

- **MailKit deferred** from Task 1.1's package baseline to Phase 4 (Dispatch F), which writes the email
  code and will add the reference with the version it needs. Adding an unused package with a guessed
  version now is pure restore risk for zero boot benefit.
- **`Microsoft.AspNetCore.OData` omitted** — this host exposes no OData surface (matches the companion
  headless implementation, which also omits it). It can be added if a later dispatch adds a data surface.
- **Task 2.1 Step 3 (register the 4 BOs on the Api's chain + establish the Api↔JobServer project
  reference) deferred** to the dispatch that needs it (Dispatch B = OData exposure, entangled with
  Dispatch E = Task 3.4's `JobExecutor` reference). It is not needed for this slice's boot test and
  would modify the Api (46 green tests) for a later dispatch's benefit. Cycle-free direction, for the
  record: BOs live in `XafHeadless.JobServer\BusinessObjects\`, and the Api references the JobServer
  (Api→JobServer) — the only direction that satisfies both Step 3 and Task 3.4 without a reference cycle.

## SVR-001 — Forward note for Dispatch D (Task 3.2): worker-scope tenant resolution

**Not solved here (flagged by team lead).** `ReportRenderService` must load the seeded `OrdersReport`
(`ReportDataV2`) from tenant `company1`'s catalog (`OutlookInspiredDemo_company1`), but it runs inside a
Hangfire worker — no HTTP request, so `TenantByEmailResolver` never fires and no tenant is selected.
Object spaces created there default to the HOST context (`IsTenantSet` is false). Below is where to
start, from reading the installed 26.1 multi-tenancy source — **verify before relying on it.**

- **Tenant selection is explicit, not only HTTP-driven.** `ITenantProvider.TenantId` (Guid?) is
  settable. DevExpress sets it directly before doing tenant work in
  `...MultiTenancy.AspNetCore\Services\TenantDatabaseUpdater.cs:68-69`.
- **Name → GUID:** resolve `ITenantNameHelper` and call `GetTenantIdByName("company1.com")` — exactly
  what the resolver does under the hood (`...MultiTenancy\Services\TenantResolver.cs:76-77`,
  `TenantByUserNameResolver`). Tenant Name is `company1.com`; catalog is `OutlookInspiredDemo_company1`
  (host `Tenant` row, from the demo's `CreateTenant("company1.com", "OutlookInspiredDemo_company1")`,
  module `Updater.cs:118-124`).
- **Routing** picks host vs tenant provider by
  `MultiTenancy.Internal.ApplicationExtensions.IsTenantSet(serviceProvider)`
  (`...MultiTenancy.WebApi.EFCore\WebApiXAFApplicationBuilderWrapper.cs:70`). Setting `TenantId` makes it
  true, so a subsequent `INonSecuredObjectSpaceFactory.CreateNonSecuredObjectSpace<ReportDataV2>()`
  should then route to the tenant provider (`OutlookInspiredEFCoreDbContext` → `OutlookInspiredDemo_company1`).
- Probably wrap the scope in `IValueManagerStorageContext.RunWithStorage(...)`
  (`DevExpress.ExpressApp.AmbientContext`), as `TenantDatabaseUpdater` does for its non-request work.

**Sketch (verify at Task 3.2, do not copy blind):** in the job's DI scope →
`RunWithStorage(() => { sp.GetRequiredService<ITenantProvider>().TenantId =`
`tenantNameHelper.GetTenantIdByName("company1.com"); using var os =`
`nonSecuredFactory.CreateNonSecuredObjectSpace<ReportDataV2>(); ... })`. `company1`'s catalog is already
seeded (the demo did it), so this is tenant **selection**, not provisioning — `WithTenantDatabaseUpdater`
(logon-time) is not in play. Open question to confirm: whether `CreateNonSecuredObjectSpace<T>()` honors
the just-set `TenantId` within the same scope, or whether the tenant provider must be resolved the way
`WebApiXAFApplicationBuilderWrapper` builds `IExtraObjectSpaceProviders`.
