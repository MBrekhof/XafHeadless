# DevExpress support ticket — draft (SVR-003)

Copy-paste ready. File under **DevExpress.ExpressApp.WebApi** (XAF Web API / headless). Two defects
below share one root area (Web API OData over `.WithSharedBusinessObjects()` types on a multi-tenant
host), so file them as **one ticket**. Everything here is reproduced live and traced into installed
26.1 source — offer the trace up front; support will localize faster.

---

**Title:** OData `$filter`/`$top`/`$skip` return wrong results (and `$select` throws) for
`.WithSharedBusinessObjects()` types on a multi-tenant Web API host

**Product / version:** reproduced on DevExpress.ExpressApp.WebApi **26.1.3** (also
`.ExpressApp.MultiTenancy.EFCore`, `.ExpressApp.EFCore`). Microsoft.AspNetCore.OData 9.3.2, EF Core 10,
SQL Server / LocalDB, .NET 10.

> **Before filing, re-check on the current patch.** This repo moved to **26.1.4** on 2026-08-08 with the
> `EnableConstantParameterization=false` workaround still in place, so whether 26.1.4 still exhibits the
> defect is **untested** — the workaround would mask it either way. Support will ask, so temporarily remove
> the `IApplicationModelConvention` and re-run the reproduction below before submitting.

**Environment:** Non-visual XAF Web API host configured multi-tenant via
`builder.AddMultiTenancy(...).WithHostDbContext(...).WithSharedBusinessObjects(sharedTypes).WithTenantResolver<TenantByEmailResolver>()`.
A host-owned business object (e.g. `JobDefinition : BaseObject`) is registered in the
`WithSharedBusinessObjects` set and exposed to the Web API via `options.BusinessObject<JobDefinition>()`.
Per-tenant business objects (e.g. `Order`) are exposed the same way.

---

## Defect 1 (primary) — OData literal predicates evaluate as `default(T)` for shared-BO types

**Symptom.** For a shared-BO entity set, `$filter`/`$top`/`$skip` behave as though every literal
operand were `default(T)`:

| Query on `api/odata/JobDefinition` (one seeded row: `IsEnabled=false`, `JobTypeName='EmailOrdersReport'`) | Returned | Expected |
|---|---|---|
| no query options | the row | the row ✅ |
| `$count=true` | count **1** | 1 ✅ (count is right even when the paged value is wrong) |
| `$top=1` | **empty** | the row ❌ |
| `$skip=1` | **the row** | empty ❌ |
| `$filter=JobTypeName eq 'EmailOrdersReport'` (string) | **empty** | the row ❌ |
| `$filter=ID eq <the row's guid>` (Guid key) | **empty** | the row ❌ |
| `$filter=IsEnabled eq true` **and** `eq false` | **the row for both** | only `eq false` ❌ |
| `$filter=LastRunStatus eq 'Success'` (enum) | the row | the row ✅ (enum literal is inlined) |
| `$orderby=Name`, single-entity `JobDefinition(<key>)` | the row | ✅ (no literal container involved) |

The same failures reproduce on a second shared type (`JobExecutionRecord`). **A normal per-tenant type
(`Order`) is completely unaffected** — `$top`, string `$filter`, Guid-key `$filter` all return correct
data. So the discriminator is *the business object being a shared BO*, not the query shape.

**Root cause (traced in-process).** The XAF data layer is not at fault — reproducing
`DataService`'s exact path (`IObjectSpaceFactory.CreateObjectSpace(typeof(JobDefinition))` →
`os.GetObjectsQuery<JobDefinition>()`) yields a pristine EF Core `DbSet`, and every direct LINQ op
(`.Where(x => x.JobTypeName == "EmailOrdersReport")`, `.Take(1)`, …) returns the row correctly.

The defect appears in `[EnableQuery].ApplyTo`. Dumping the translated expression for
`$filter=JobTypeName eq 'EmailOrdersReport'` gives:

```
Where($it => (Convert($it.JobTypeName, String)
    == value(Microsoft.AspNetCore.OData.Query.Container.LinqParameterContainer
             +TypedLinqParameterContainer`1[System.String]).TypedProperty))   // → 0 rows
```

ASP.NET Core OData's **constant parameterization** (`ODataQuerySettings.EnableConstantParameterization`,
default `true`) wraps each literal in a `TypedLinqParameterContainer<T>` and the query reads
`container.TypedProperty`. **When this expression executes against the shared-BO DbContext,
`container.TypedProperty` resolves to `default(T)`** — confirmed by every off-by-default result:
`string → null`, `Guid → Guid.Empty`, `int → Take(0)`, `bool → false`. Enum literals are inlined as a
constant (`== 2`), not containerized, so they alone work.

**Why only shared-BO types.** `.WithTenantResolver<>()` registers a build step
(`DevExpress.ExpressApp.MultiTenancy.EFCore/MultiTenancyApplicationBuilder.cs:137-142`) that sets
`IDBContextSwitcher.UseStandaloneDBContext = true` (also `.../ApplicationExtensions.cs:58`). With that
flag, `DbContextFactoryProvider.GetDbContextFactory()`
(`DevExpress.ExpressApp.EFCore/IDbContextFactoryProvider.cs:58-67`) returns a **standalone**
`EFCoreDbContextFactory<TDbContext>` built outside the DI `IDbContextFactory<T>` pipeline. That is the
context the shared-BO object space runs on, and on it the OData `LinqParameterContainer` evaluates to
`default(T)`. (Side evidence: the standalone context emits no EF `DbCommand` logs, unlike the tenant
context — it is wired outside the app's logging/service pipeline.)

**Confirmed fix at the OData layer.** Re-running the same `ApplyTo` with
`ODataQuerySettings.EnableConstantParameterization = false` inlines every literal and makes **all** rows
above correct. We are shipping that as an app-side workaround (a global
`IApplicationModelConvention` that sets `EnableConstantParameterization=false` on the
`EnableQueryAttribute` of the generated data-controller actions), but that only sidesteps the defect —
**the framework should evaluate the parameter container correctly on the standalone shared-BO
context so constant parameterization can stay on.**

**Questions for support:**
1. Is `LinqParameterContainer.TypedProperty` resolving to `default(T)` on the
   `UseStandaloneDBContext` shared-BO context a known issue?
2. Is disabling `EnableConstantParameterization` for the shared-BO controllers the supported approach,
   or is there a first-class way to expose `.WithSharedBusinessObjects()` types over the Web API OData
   surface with correct `$filter`/`$top`?

---

## Defect 2 (same host, likely related) — `$select` on a shared-BO type throws `edmModel` null

`$select` on the same shared types (`api/odata/JobDefinition?$select=Name`) throws during OData
serialization:

```
System.ArgumentNullException: Value cannot be null. (Parameter 'edmModel')
```

Per-tenant types (`Order`) accept `$select` normally. Related observation (not necessarily the cause):
`$metadata` emits **two** `<EntityContainer>` blocks with every entity set/type duplicated — but this
duplication is present for per-tenant types too, and they work, so it is likely a symptom rather than
the cause. We currently avoid `$select` on these types; a framework fix would remove that constraint.

---

## Minimal repro we can supply

A stripped multi-tenant XAF Web API project: one `WithHostDbContext`, one shared BO in
`WithSharedBusinessObjects`, one per-tenant BO, both `options.BusinessObject<T>()`-exposed; seed one
shared row; issue the `$top=1` / string-`$filter` / `$select` requests above against the shared set and
against the per-tenant set for contrast. Happy to send it on request.
