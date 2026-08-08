# How to build a headless XAF platform on DevExpress 26.1

A clean-start guide for XAF developers: stand up a UI-less XAF Web API that **projects** the Application
Model and Security System as JSON, and render real ListViews/DetailViews in a thin, multi-render-mode Blazor
client that holds **zero** XAF engine references. Everything here is verified against DevExpress **26.1.3 /
.NET 10 / EF Core** and the installed 26.1 source; the running reference is this repo (`XafHeadless.Api`,
`XafHeadless.Components`, `XafHeadless.Web`/`.Web.Client`).

**The one rule above all: project, don't re-implement.** The moment your metadata controller starts
evaluating permissions or merging model layers itself, stop — you're rebuilding XAF, not projecting it.
XAF's crown jewels (TypesInfo, the Application Model, the Security System, Validation) are UI-framework-free;
you host them and expose them as data. Security is flattened server-side per request; the client never
evaluates permissions.

**On tenancy.** Most apps are **single-tenant** — one database, plain user logins — and that is the simpler,
common path shown first throughout this guide. **Multi-tenancy** (one host, many tenant databases) matters for
SaaS and is *required* if your module is tenant-aware. This repo's reference implementation happens to be
multi-tenant because its demo module (`OutlookInspiredDemo.Module`) resolves `ITenantProvider` during seeding
and crashes single-tenant — so wherever tenancy changes the setup, the multi-tenant variant is called out in a
**Multi-tenant** box. A from-scratch single-tenant module skips those boxes entirely.

---

## Run the reference first

Before building your own, run this one to see the shape of the result:

1. Open `XafHeadless.slnx` in Visual Studio 2022.
2. Pick the **"Run POC (Api + Web)"** startup profile (in `XafHeadless.slnLaunch`) and press **F5**. It starts
   both hosts — `XafHeadless.Api` on `http://localhost:5200` and `XafHeadless.Web` on `http://localhost:5220`
   — and opens the browser. **Both must be `http`**: the client is hard-wired to call `http://localhost:5200`,
   so an `https` client page would be blocked as mixed content.
3. Log in with **`Admin@company1.com`** and a **blank password**. Data lives in LocalDB `XafHeadlessDemo`
   (host) + `OutlookInspiredDemo_company1` (tenant), both disposable and self-created on first run.

That is a fully model-driven UI — every list, detail, editor, filter, image, and appearance rule is projected
from the model, with no per-view client code. The rest of this guide is how it's built.

## Step 1 — The standalone Web API host

A new ASP.NET Core project referencing your platform-agnostic module. Use the standalone Web API pattern
(`services.AddXafWebApi(webApiBuilder => { … }, Configuration)`), then:

- **Modules:** `webApiBuilder.Modules.Add<YourModule>()` + anything it requires. If your module uses
  ReportsV2, call `Modules.AddReports(...)` or DI fails on `IReportDataSourceHelper`.
- **WebApi-builder constraint (26.1):** the fluent module extensions (`AddConditionalAppearance`,
  `AddOffice`, `AddScheduler`, `AddViewVariants`, `AddFileAttachments`) are `IApplicationBuilder<T>`-constrained
  and are **not** available on `IWebApiApplicationBuilder` — only `AddReports`/`AddValidation` are. Don't try
  to call them; the modules still **load** via `RequiredModuleTypes`, so their model contributions (e.g.
  conditional-appearance rules) are present in the model regardless.
- **Secured data access:** `ObjectSpaceProviders.AddSecuredEFCore(o => o.PreFetchReferenceProperties())
  .WithDbContext<YourDbContext>(...).AddNonPersistent()` — secured, so every ObjectSpace the host hands out
  enforces permissions. If your DbContext relies on `INotifyPropertyChanged` proxies (tracking strategy
  `ChangingAndChangedNotificationsWithOriginalValues`), set `UseChangeTrackingProxies()` +
  `UseObjectSpaceLinkProxies()` + `UseLazyLoadingProxies()` explicitly on the options — no `AddXaf*` path does
  it for the WebApi builder.
- **Security + auth:** `Security.UseIntegratedMode(...)` + `.AddPasswordAuthentication(...)`, then standard
  ASP.NET Core JWT bearer auth and a default policy with `.RequireXafAuthentication()`. Single-tenant logons
  are plain usernames (e.g. `Admin`).
- **Endpoints:** **do NOT call `app.UseXaf()`** (Blazor-specific). Use `MapXafEndpoints()` + `MapControllers()`.
- **Let the host own its catalog:** enable `CheckCompatibilityType.DatabaseSchema` + a
  `DatabaseVersionMismatch → e.Updater.Update()` handler so the host creates/updates its own database and runs
  your `ModuleUpdater`/data seeding on first run.

That is the whole single-tenant host: one database, `Update()` seeds it, logins are usernames.

> **Multi-tenant variant.** Needed when your module is tenant-aware (this repo's demo is) or you're building
> SaaS. Mirror the app's tenancy config per the dxdocs "Web API Service" tab:
> `AddMultiTenancy(...).WithHostDbContext<ServiceDbContext>(...).WithMultiTenancyModelDifferenceStore(...)
> .WithSharedBusinessObjects(typeof(TaxRate), …).WithTenantResolver<TenantByEmailResolver>()` — the resolver
> must be the **last** call (its builder returns `void`). Consequences: **logons become tenant emails**
> (`Admin@company1.com`); per-request JWT tenant resolution works headless; add `.WithTenantDatabaseUpdater()`
> to self-seed a fresh tenant DB via the module's own `DataGenerator` (fires lazily per-logon, once per tenant
> per process, no-ops on an already-seeded tenant). Two things get harder under multi-tenancy: host-owned
> ("shared") business objects are **read-only from a tenant-resolved request** (see Step 5 prefs, gotcha 19),
> and a tenant-aware module *forces* this path — a single-tenant startup crashes at `ITenantProvider`
> (gotcha 16).

**Prove the spike before anything else.** A diagnostic endpoint that resolves the shared application
(`ISharedApplicationProvider` → `GetContainer().Application.Model` — constructor-injecting `IModelApplication`
does **not** resolve) and dumps `Views.Count` + your target view IDs. A UI-less host **does** generate the
full Views model from module-level metadata. **Known contract boundary:** app-level `Model.xafml` (the diffs
in a Blazor.Server/Win project) is NOT loaded — you get the **module-level** model only. Decide early that
module-level is your contract (recommended: put customizations in a module), or load app diffs explicitly.

## Step 2 — The metadata projection

One controller: `GET /api/model/views/{viewId}`. DTOs: `ViewMetadata` (Id, Type, ObjectType, Caption,
`KeyMember`, `Allow {Edit,New,Delete}`, Columns, Layout, Appearance), `ColumnMetadata` (Member, Caption,
DataType, SortIndex/SortOrder, Lookup, Enum), recursive `LayoutNode` (Kind = `group|tabs|tab|item|nestedList`,
plus per-item facts: Editor, AllowWrite, Required, MaxLength, Lookup, Enum).

**Contract rules — the whole design's honesty:**
1. Members the user can't read are **omitted**, never flagged — the client can't leak what it never receives.
2. `Allow`/`AllowWrite` = model settings ∩ security, computed **server-side per request**.
3. Only declarative validation facts (Required, MaxLength) project; rule *evaluation* stays server-side.
4. Object-level (criteria) permissions don't flatten at metadata time — inaccessible rows never arrive
   through the secured OData pipeline.

**The security API (26.1):** `IsGrantedExtensions` on `IRequestSecurityStrategy` — `CanRead(type, objectSpace,
memberName)`, `CanWrite/CanCreate/CanDelete(...)` — using the **`IObjectSpace` overloads** (the ObjectSpace-less
ones are `[Obsolete]`). Resolve `ISecurityProvider` in the request scope, open one secured ObjectSpace per
projection request, dispose it. Every call routes into `SecurityStrategy.IsGranted` — you never touch roles or
permissions yourself.

**Layout walk (verified against 26.1 source):** the DetailView root is `IModelViewLayout` — enumerate its
children (a naive switch on the root returns null); `IModelTabbedGroup : IModelList<IModelLayoutGroup>`;
`IModelPropertyEditor.Caption` is ambiguous (CS0229) — cast to `IModelViewItem` (the per-view override wins).
Enum captions via `DevExpress.ExpressApp.Utils.EnumDescriptor`.

**Nested collections vs. blobs — the `IsList` trap.** A member projects as a `nestedList` (embedded child
grid, `View?.Id` for the nested view, `AssociatedMemberInfo.Name` as the master key) **only when its element
type is a business object.** `IsList` alone is a trap: a `byte[]` also reports `IsList == true` with element
type `System.Byte`, so a naive walk emits a nested `Byte_ListView` that doesn't exist and 404s the whole tab.
Gate the nested branch on `ListElementTypeInfo.IsDomainComponent || .IsPersistent` (the list counterpart of the
lookup predicate below), and:
- classify `byte[]` as an **`image`** item (rendered as a picture client-side, Step 5) — not a collection;
- **omit** a `nestedList` whose child type isn't OData-exposed (`WebApiOptions.BusinessObjects`, Step 3) — an
  unreachable child grid would 404 on its data fetch, so don't project the tab at all (same exposure predicate
  the nav menu uses).

**Serializer:** set `JsonSerializerOptions.MaxDepth = 128` (auto-generated layouts bisect into 2-column groups,
producing real 14–20-level trees; without it the endpoint 500s like infinite recursion), `PropertyNamingPolicy
= null` (PascalCase), and mirror the DTO names **byte-identically** in the client.

**Lookup classification:** a member is a lookup when `!IsList && (MemberTypeInfo.IsDomainComponent ||
MemberTypeInfo.IsPersistent)` — use one predicate for both classification and lookup projection so they can't
disagree. (For the EF Core provider these flags are co-set, so this is equivalent to "reference to a persistent
type," but a single predicate is drift-proof.)

## Step 3 — Data over OData (reads), and the write contract

Reads are free: `AddXafWebApi` gives `/api/odata/{Entity}` with `$top/$skip/$orderby/$filter/$count`,
permission-filtered. Gotchas:

- **Never send `$select`.** The model can name members absent from the OData EDM (calculated/unmapped) — a
  `$select` on one 400s. Fetch full entities; handle missing JSON properties defensively.
- **The EDM entity-set list is broader than what's queryable.** It includes types reachable transitively as
  association/lookup targets. To know what's actually served, read `IOptions<WebApiOptions>.BusinessObjects`
  (the exact list your `options.BusinessObject<T>()` calls populate) — not the EDM entity sets. This is the
  same list Step 2 uses to decide whether a nested tab is reachable.

**The write trap that justifies this whole guide: XAF validation does NOT run on OData writes in a UI-less
host.** `PersistenceValidationController` is a ViewController — dormant without a View. An OData PATCH that
violates a `RuleRequiredField` returns 204 and **silently persists**. So:

- **Block raw OData writes** with ~5 lines of middleware rejecting non-GET on `api/odata` (405). Ship this — a
  client must never write through OData.
- **Build a validating save endpoint** `POST /api/save/{type}[/{key}]` (keyless = create), body = changed
  members only. Open a secured ObjectSpace (or `CreateObject` for create), apply the members, hook
  `ObjectSpace.Committing` to run `Validator.RuleSet.ValidateAllTargets(...)` and throw `ValidationException`
  before commit → **422** `{ MemberErrors: { member: message }, Messages: [...] }`. On success, **201** + the
  server-generated key for create, **200** for update. Additional contract points:
  - **Reference/lookup members:** the body sends the FK **key** (a scalar). Detect a reference member
    (`IsAssociation || (IsPersistent && MemberTypeInfo.IsPersistent)`) and resolve the key to the object via
    `os.GetObjectByKey(mi.MemberType, key)` — deserializing a scalar into the reference type throws.
  - **Enum members** round-trip as-is if the enum carries `[JsonStringEnumConverter]`.
  - Unknown/non-writable member → 400; malformed/unresolvable key → 400; per-member `CanWrite` denied → 403
    (via `IsGrantedExtensions.CanWrite`, before commit, for a clean status instead of a commit-time exception).

## Step 4 — Commands (your actions, minus the UI)

A trivial registry: `interface IHeadlessCommand { string Id; CommandResult Execute(IObjectSpace os, string[]
objectKeys); }`, DI-registered as `IEnumerable<IHeadlessCommand>`, one controller (`POST /api/commands/{id}` →
`{ Success, Message, RefreshKeys }`). Objects resolve through the **secured** ObjectSpace. Honest expectation:
controller actions touching only ObjectSpace logic port mechanically; ones doing raw SQL or popup choreography
do not. Audit before assuming a port rate. Note: XAF JWTs carry **no role claims**, so `[Authorize(Roles=…)]`
never matches — gate admin-only endpoints with an in-action `ISecurityStrategyBase.User` check.

This is the headless answer to XAF ViewControllers: a controller action that *takes parameters and starts a
background job* maps cleanly to a command endpoint; an action that filters a grid maps to Step 5's server-side
filter. Actions coupled to a live View/popup are the ones that don't cross the wire.

## Step 5 — The thin client (Blazor Web App, InteractiveAuto)

Structure it as a **Blazor Web App** with an RCL of the rendering logic and two hosts:

- **`*.Components` (RCL):** `XafListView`, `XafDetailView`, the editor map, and all translation services.
  References only `DevExpress.Blazor` (the engine-free UI package) — **never** `DevExpress.ExpressApp` or your
  demo/business module (the **wire rule**).
- **`*.Web` (server host) + `*.Web.Client` (WASM host):** both call the API purely over HTTP and register the
  identical client stack through one shared extension (`ClientServiceRegistration.AddXafHeadlessClient()`) so
  they can't drift. Render mode `InteractiveAuto` — Server first, WebAssembly after the runtime caches.
- **DI lifetimes:** register client state (`AuthState`, `ApiClient`, `HttpClient`) **Scoped**, not Singleton — a
  Singleton leaks one user's token across circuits when hosted server-side.
- **Auth across render modes:** the JWT is per-render-context, so a hard reload or the Server↔WASM takeover
  starts fresh and would bounce to login. Persist the token to **plain `sessionStorage`** via `IJSRuntime`
  (NOT `ProtectedBrowserStorage` — it's Server-circuit-only and breaks the WASM phase), restore it on the
  **first interactive render** (never during prerender — JS interop throws), and defer the "no token → /login"
  redirect until restore has been attempted (or an authenticated reload flashes login).

**Theme it — or it looks unfinished.** DevExpress Blazor ships two theme families and the choice is not
cosmetic. **Fluent** (the default) is Design-System-based: it styles the DevExpress components via CSS
variables but does **not** touch native HTML / Bootstrap chrome — so your nav, login, labels, and buttons fall
back to bare Bootstrap and the app reads as "unstyled." **Classic** themes (Office White, Blazing Berry,
Purple, Blazing Dark) bundle Bootstrap CSS and style **both** the DevExpress components and your
Bootstrap-classed chrome cohesively. Register one in `App.razor`:
`@DxResourceManager.RegisterTheme(Themes.OfficeWhite)` — and **do not also link a separate `bootstrap.min.css`**
(the Classic theme already provides Bootstrap; loading both double-loads and conflicts). Verified via dxdocs
401523. Key your own accents off the theme's `--bs-*` variables so custom CSS tracks the theme.

The two components:

- **`XafListView`** — fetch metadata, build `DxGrid` columns dynamically, bind through `GridCustomDataSource`
  (the documented WASM-compatible path; server-mode sources don't work in WASM): translate its load options
  (StartIndex/Count/SortInfo/**FilterCriteria**) into OData query strings; use the same `$filter` for count and
  page; forward the `CancellationToken`. Lookup columns display via `$expand=Member($select=Display)`; enum
  values map to captions from the projected metadata; collection **and image** columns are skipped (a base64
  blob is not a grid cell). Master-detail reuse = one extra parameter pair composing an OData filter.
- **`XafDetailView`** — fetch metadata + object, recursive renderer over the LayoutNode tree (group→fieldset,
  tabs→DxTabs, item→editor via a DI `editorHint → component` dictionary, nestedList→embedded `XafListView`).
  Editors honor `AllowWrite=false`; unmapped editor hints render read-only with a visible "unsupported editor"
  badge (graceful degradation that doubles as an honest inventory). Dirty tracking compares against original
  values; Save posts only changed members; 422 maps back per member. Include a per-member `RenderFragment`
  slot from day one — the escape hatch everything bespoke uses.
  - **Image members:** the `image` editor (Step 2 classifies `byte[]` as `image`) renders the value — OData
    serializes `Edm.Binary` as a base64 string — as an `<img>` data-URI, sniffing the MIME from the base64
    magic-byte prefix (per the magic-bytes-not-extensions rule). A blob reached through a non-expanded nav path
    (e.g. `Employee.Picture.Data`) has no bytes in the row and degrades to a clean "(no image)".

Keep all translation logic (query building, row materialization, layout collapsing, dirty diffing, the
`CriteriaOperator→$filter` translator) in plain DevExpress-**ExpressApp**-free classes — unit-testable without
a Blazor host.

**Additional capabilities, all the same "project + render" shape:**
- **Navigation menu:** `GET api/model/navigation` walks `IModelRootNavigationItems.AllItems`, filters to
  ListView items whose target type is exposed (`WebApiOptions.BusinessObjects`) and the user `CanRead`, and the
  client renders a flat menu.
- **Filter UI (server-side):** enable the DxGrid filter row and translate its `CriteriaOperator` to OData
  `$filter` — the filtering runs on the server, not in the browser. Scalar members only; enum/lookup filtering
  needs the canonicalization in gotcha 22.
- **Per-user layout prefs:** a host-owned entity + `GET/PUT api/prefs/{viewId}` keyed to
  `ISecurityStrategyBase.UserId`, storing DxGrid column state. (Multi-tenant: this host-owned entity is a
  *shared* BO — read-only from a tenant-resolved request — so write it from a fresh host-context scope via
  `INonSecuredObjectSpaceFactory`, gotcha 19. Single-tenant: just write it normally.)
- **Conditional appearance:** project the model's `[Appearance]` rules; evaluate each rule's `Criteria`
  client-side per row with `DevExpress.Data.Filtering.ExpressionEvaluator` (it reads `ExpandoObject` rows
  natively) and apply colors/styles via DxGrid `CustomizeElement`. Enum criteria need the caption↔name rewrite
  (gotcha 22); supply enum metadata for criteria members that aren't displayed columns.

## Step 6 — Prove it honestly

In order of importance:
1. **Two-role metadata test:** the same view for an admin vs a role denying one member must differ — the denied
   member **absent**, not flagged. The core proof that security flattening works.
2. **Validation round-trip:** clear a required member → save → 422 names it → an OData read confirms nothing
   committed.
3. **Dual render-mode E2E** (C# Playwright, against published builds): the same smoke path passes in **both**
   the Server and WebAssembly render phases, in one browser context — login → list → sort → filter → detail →
   save/422 → nested tab → command. Pin the test record; restore any mutation in a `finally`.
4. **N-view sweep:** point the generic client at several more view pairs and assert they render with **zero
   per-view client code** — the compression claim, executable.

## Gotcha index (things that cost real debugging time — all 26.1)

Rows marked **[MT]** apply only to the multi-tenant variant.

| # | Gotcha | Fix |
|---|--------|-----|
| 1 | UI-less host: no XAF validation on OData writes | Validating save endpoint (`IValidator` at `Committing`); block OData writes (405 middleware) |
| 2 | `IModelApplication` not injectable | `ISharedApplicationProvider` → `GetContainer().Application.Model` |
| 3 | App-level `Model.xafml` not loaded headless | Module-level is the contract (recommended), or load app diffs explicitly |
| 4 | `$select` 400s on model-only members | Never `$select`; defensive null handling |
| 5 | JSON 500s on big DetailViews | `MaxDepth = 128` (real 14–20-level trees) |
| 6 | Obsolete security overloads | `IsGrantedExtensions` with `IObjectSpace` parameters |
| 7 | Layout root matches no node type | Enumerate `IModelViewLayout` children |
| 8 | `IModelPropertyEditor.Caption` ambiguous (CS0229) | Cast to `IModelViewItem` (per-view override wins) |
| 9 | Module needs ReportsV2 | `Modules.AddReports(...)` in the host |
| 10 | Lookup targets 404 in OData | Expose each lookup target type, or a read-only lookup endpoint |
| 11 | `[ApiController]` + non-nullable array in a request record | Implicit-required returns 400 before your code runs; make it nullable + normalize |
| 12 | Reference/lookup save value is an FK scalar | Resolve the key to the object (`os.GetObjectByKey(mi.MemberType, key)`) — deserializing a scalar into the reference type throws |
| 13 | XAF JWTs carry **no role claims** — `[Authorize(Roles=…)]` never matches | In-action `ISecurityStrategyBase.User` role check (verified against 26.1 `StandardAuthenticationIdentityCreator`) |
| 14 | Module fluent extensions (`AddConditionalAppearance`/`AddOffice`/…) unavailable on `IWebApiApplicationBuilder` | Modules still load via `RequiredModuleTypes`; only `AddReports`/`AddValidation` exist for WebApi builders |
| 15 | WebApi builder doesn't apply EF change-tracking proxies | Set `UseChangeTrackingProxies()` + `UseObjectSpaceLinkProxies()` + `UseLazyLoadingProxies()` on the DbContext options |
| 16 | **[MT]** Multi-tenant module **requires** tenancy headless (crashes single-tenant) | Mirror the app's `AddMultiTenancy` config; logons become tenant emails; per-request JWT tenant resolution |
| 17 | Auto-mode E2E: a hard reload mid-download **cancels** the WASM download | Poll `localStorage["blazor-resource-hash:*"]` in place until cached, then reload; the badge transits `"Static"` before settling |
| 18 | Blazor Web App DI: a `Singleton` client service leaks state across circuits | Register `AuthState`/`ApiClient`/`HttpClient` **Scoped** |
| 19 | **[MT]** Shared/host business objects are **read-only from a tenant-resolved request** | Write them from a fresh DI scope (`TenantId == null` → host context) via `INonSecuredObjectSpaceFactory` |
| 20 | Adding a host-owned entity to an **existing** host catalog | `CheckCompatibilityType.DatabaseSchema` is `EnsureCreated`-like (full schema on a fresh catalog, no incremental table add) — a real deployment needs EF Core migrations |
| 21 | The OData EDM entity-set list is broader than what's queryable | Gate exposure checks on `IOptions<WebApiOptions>.BusinessObjects`, not the EDM entity sets |
| 22 | Grid enum/lookup cells hold the **display** value (caption / flattened string), not the raw value | Filter/appearance criteria referencing them need canonicalization (rewrite the enum literal to its member name), or restrict to scalar members |
| 23 | JWT lost on hard reload / render-mode takeover | Persist to plain `sessionStorage` (not `ProtectedBrowserStorage`); restore on first interactive render; don't bounce to login before restore |
| 24 | `DisableDeferredDeletion` + a plain unique index for a host `BaseObject` | For an EF `BaseObject` registered via a no-lambda `modelBuilder.Entity(type)` (no reachable `OnModelCreating`), `[DisableDeferredDeletion]` + `[Index(IsUnique=true)]` is the DevExpress-shipped pattern (mirrors `UserToken`) — no `GCRecord` filter |
| 25 | A `byte[]` member reports `IsList == true` → projected as a nested `Byte_ListView` that 404s | Gate the `nestedList` branch on `ListElementTypeInfo` being a business object; classify `byte[]` as an `image` item and render it as an `<img>` data-URI |
| 26 | A `nestedList` over a child type that isn't OData-exposed 404s on its data fetch | **Omit** the tab: don't project a `nestedList` whose child type isn't in `WebApiOptions.BusinessObjects` (same predicate as the nav filter) |
| 27 | DevExpress **Fluent** theme leaves native/Bootstrap chrome unstyled → the app looks bare | Use a **Classic** theme (`Themes.OfficeWhite`/`BlazingBerry`) — it bundles Bootstrap and styles chrome + components; drop any separate `bootstrap.min.css` |
| 28 | A `DateTime` member cannot be **range-compared** at all: the EDM says `Edm.DateTimeOffset`, the CLR property is `DateTime?` → 400 *"binary operator GreaterThanOrEqual is not defined for the types …"*, and a no-offset literal fails to **parse** | Emit `date(path) op yyyy-MM-dd` (200, no timezone conversion needed). Cost: `date()` is not SARGable — for very large tables fix the EDM/CLR mismatch instead |
| 29 | A ListView column can name a **dotted model path** (`Customer.Name`) that arrives **unclassified** (`Lookup == null`) → the dot reaches the wire (`$orderby`/`$filter` 400), `$expand` never fetches the nav property, and the cell reads a key no payload has → column silently **blank** | Derive every wire form from one path-segment helper: `_` for the grid FieldName, `/` for OData paths, nested `$expand`, and a segment walk for the value. A flat column is one segment, so its behaviour is unchanged |
| 30 | An unhandled exception from a **DevExpress grid data callback** terminates the whole Blazor circuit — one bad request kills the app | Set `GridCustomDataSource.ExceptionHandler` (mark `Handled`, surface the message) **and** wrap the layout body in an `ErrorBoundary` that `Recover()`s on `LocationChanged` |
| 31 | `EnsureSuccessStatusCode()` hides the evidence: no URL, no query string, and not the server's error body — where OData puts the actual reason | Throw your own exception carrying method + absolute URL + status + a bounded body excerpt; log 4xx/5xx on the API side too (an OData 400 is a normal *response*, so nothing else records it) |
| 32 | Persisted grid layout makes any rejected shaping **permanent**: `LayoutAutoSaving` saves the sort/grouping that just failed, so every later load replays it and the view renders nothing | Strip sort/group state from the persisted layout when the failure is attributable to the layout (a 400, or your own ceiling exception); keep column order/width. Ceilings that reach this: `$orderby` over a lookup whose display member is `Edm.Binary`, and a high-cardinality grouping |
| 33 | `GridPersistentLayout.PageSize` is `int?` with `JsonIgnore(WhenWritingDefault)` → a null is dropped from the persisted blob, and applying that layout resets the grid to DevExpress's default of **10** rows, overriding your markup | Refill the markup value when the blob carries none (a persisted user choice still wins) |
| 34 | `DxGrid.HighlightRowOnHover` defaults to **false**, so a row-hover style cannot be done in CSS alone — the grid never marks a hovered row | Set the parameter; take the colour from the theme's own hover variable |
| 35 | Restyling a **Classic** theme: it exposes no public CSS variable API (only Fluent has `--dxds-*`) and defines radii/colours as `--dxbl-*` **on the component's own selector**, so a `:root` override is inert | Redefine those variables at matching specificity (doubled classes), verified against the installed theme CSS. `--dxbl-*` is internal and may change between versions — the supported route for a permanent restyle is a custom Classic theme from the SCSS sources, or `Themes.BootstrapExternal` |
