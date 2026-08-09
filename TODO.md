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

_GRID-005 (project a lookup display member's data type so an impossible sort is refused up front —
`LookupMetadata.DisplayDataType` + `IsServerSortable` + `AllowSort` in server mode) done 2026-08-08 — see
`docs/DONE.md`. It also **corrected BUG-006's recorded root cause**: Store's display member `Emblem` is a
**reference to an entity**, not the `Edm.Binary` blob that record claimed._

_BUG-008 (Store rendered permanently blank — its display path landed on an entity; `ProjectLookup` now
walks the default-property chain to a primitive and sends the dotted path `Emblem.CityName`) fixed
2026-08-09 — see `docs/DONE.md`. It also lifted GRID-005's ceiling for that column, which cost the
ceiling its only live E2E subject — tracked as [[TEST-003]] below._

_BUG-009 (clicking a nested row navigated to a DetailView that does not exist — the `_ListView` →
`_DetailView` swap is wrong for a nested view named `{Master}_{Collection}_ListView`; the projector now
sends the child's real `DetailViewId` from `IModelClass.DefaultDetailView`) fixed 2026-08-09 — see
`docs/DONE.md`. Every nested tab was affected; no test had ever clicked a nested row._

#### GRID-006: Date filtering leans on `date()` because the EDM and CLR types disagree (ID: 1223)

**Ceiling accepted by BUG-003 (2026-08-08, `docs/DONE.md`), with the cost written down.** The EDM types
`DateTime` members as `Edm.DateTimeOffset` while the CLR property is `DateTime?`, so **no** range comparison
binds (see README finding 7). The client therefore emits `date(path) op yyyy-MM-dd`, which the server
accepts and which needs no timezone conversion.

The cost is SARGability: `CAST(OrderDate AS date) = @d` cannot seek an index on `OrderDate`. Sub-second at
the demo's 55k rows; a millions-row table wants the mismatch fixed at the source instead — either project
the members as `DateTimeOffset` in the EDM, or configure the OData query binder so a `DateTimeOffset`
literal compares against a CLR `DateTime`. Both are Api-host changes; measure before choosing, and keep the
`date()` path as the fallback for hosts that cannot change their EDM.

## P2: Medium — platform breadth

_**Direction set 2026-08-09: [[ARCH-001]]** (`docs/DONE.md`). Headless is a **growth path for individual
painful views**, not a replacement for XAF. Cards below whose subject is never a first candidate under that
direction — [[CHART-001]], [[PIVOT-001]], [[DASH-001]], [[MODEL-001]] — are **Backlog** on the board;
deprioritised, not refuted. What matters is the list/detail/report path being fast and correct._

### Feature-completeness push (2026-08-08)

#### FEAT-000: Feature-completeness roadmap — sequencing and the standing decisions (ID: 1238)

**Umbrella card for the 2026-08-08 feature-completeness push** (owner: "as feature complete as possible,
including features not yet included like pivotgrid, chart, reporting"). Not work itself — the map, so the
individual cards do not each re-litigate the same questions.

**The gap set, as verified against the code on 2026-08-08** (projector emits ListView + DetailView only;
seven editors exist; reporting renders server-side but has no UI): CRUD-001 (1231) new-object UI, the
server half already proven by GAP-003; LOOKUP-001 (1232) write-capable lookup, which also closes PH2-005
(541) and unblocks GAP-010 (559); CHART-001 (1228), whose aggregation path is already wire-proven via
`$apply`; RPT-001 (1230) client reporting UI over a renderer that already ships; EDIT-001 (1233) editor
inventory, audit first because it sizes the rest; CRUD-002 (1235) inline nested editing, needs CRUD-001;
EXPORT-001 (1236) list export, cheaper after RPT-001; PIVOT-001 (1227), aggregation decision first;
DASH-001 (1229) as the capstone after chart and/or pivot; FILE-001 (1234), needs a storage decision;
SCHED-001 (1237) parked in Backlog with no demand signal; and BUG-008 (1226), a pre-existing defect that
LOOKUP-001 inherits.

**Suggested order and why:** CRUD-001 → LOOKUP-001 → CHART-001 → RPT-001. That front-loads the items
reusing proven server capability (create endpoint, `$apply` aggregation, report renderer) and defers those
needing a design decision first (pivot aggregation, file storage, export ownership).

**Progress (2026-08-09):** ~~CRUD-001~~, ~~BUG-008~~, ~~LOOKUP-001~~ and ~~PH2-005~~ done — see
`docs/DONE.md`. BUG-008 was taken out of order, ahead of LOOKUP-001, because a picker that could not render
a display string for the very lookups BUG-008 describes would have shipped the same blank-value defect into
a second place; `LookupEditor` was in fact one of the two consumers that fix had to touch.

**Two card premises turned out to be wrong, which is worth noting for the ones still open:** LOOKUP-001
described an editor that was already write-capable, and BUG-006 (via GRID-003) had misidentified an entity
reference as a blob. Both were written from reading the *records* rather than the *code*. The cards below
were written the same way — treat their claims as leads to verify first, not as findings.

**CHART-001 and PIVOT-001 are now decided-against rather than pending (2026-08-09).** Checking the model
before writing code found that **neither is declared in it**: no chart or analysis node exists anywhere in
the reference module, and what looks like one (`QuoteAnalysis_ListView`, `Quote_DetailView_Pivot`) is an
ordinary ListView and a cloned DetailView whose chart/pivot behaviour lives in platform-specific WinForms/
Blazor code against XtraCharts. A metadata-driven projector has nothing to project. Both cards now carry
the same three options, and a chart metadata contract should be invented only on explicit request, because
doing so unasked would break the model-is-the-contract principle every other feature here has honoured.

**That entry originally recommended DASH-001 as the alternative. That recommendation was wrong and is
withdrawn (2026-08-09).** It claimed "two real DashboardViews exist and are composed of ListViews this
platform already renders" — read off the view *names* without opening them. `Welcome` holds a single
decorative `<StaticImage>`, and `Opportunities`' two items target `[DomainComponent]` **non-persistent**
types with no `DbSet`, which OData cannot serve at all. See [[DASH-001]] and [[NPO-001]].

**EDIT-001's audit is done** and found the more valuable gap: the projector reads **only the CLR type** and
ignores declared editor aliases entirely, of which this module has 19 across 8 aliases. That is a silent
wrong-editor bug, not just a missing-editor one.

**EDIT-001 is done (2026-08-09)** — the alias is projected additively, the client prefers it and falls back
to the CLR hint rather than to a badge, and HyperLink + ProgressBar editors ship. `DxHtmlPropertyEditor`,
the most-declared alias, was deliberately left on its string fallback because rendering stored HTML is an
XSS vector — [[EDIT-002]].

**Three cards in a row have now dead-ended on the same thing** — CHART-001, PIVOT-001 and DASH-001 all
describe screens this module builds in platform-specific code or over non-persistent objects, none of which
crosses the wire. That pattern is itself the finding, and it is now carded as [[NPO-001]]: a whole class of
XAF screen (computed/aggregate `[DomainComponent]` types) has no representation here at all. MIG-002's gap
list never mentioned it.

**Next: CRUD-002** — inline nested-collection editing. Deliberately chosen as something demonstrably real:
`Order.OrderItems` is an aggregated collection of a **persistent, OData-exposed** type, already rendered as
a nested tab, and CRUD-001 built the create path it needs. Verifiable end to end, unlike the last three.

**Standing decisions that apply to every card here, so they are stated once:**
- **The server holds the data.** No feature may pull an unbounded row set to the client. Order is 55k rows;
  GRID-002 set the precedent with `$apply` and a `RowCap` hybrid, and anything aggregating (chart, pivot,
  export) follows it or explains why not.
- **Heavy rendering goes off the request path** (MIG-002 boundary B): reports and exports enqueue to the
  JobServer, they do not run inline in the API.
- **Never assume DevExpress API surface** — dxdocs or installed 26.1 source before writing the code, and say
  so explicitly when a claim is unverified.
- **Every gap gets a card before it gets code**, cited in TODO.md so board and file cannot drift (the
  2026-08-08 backfill exists because that stopped happening once already).

_CRUD-001 (new-object creation UI — `/new/{ViewId}` route, `ApiClient.CreateAsync` reading the server-generated key off the 201, New button gated on the projected `Allow.New`) done 2026-08-09 — see `docs/DONE.md`._

_LOOKUP-001 + PH2-005 (a dedicated `api/lookup/{type}` candidate endpoint — current key always
included, server-side search, bounded at the database, text resolved by the shared display-path walk)
done 2026-08-09 — see `docs/DONE.md`. **Both card premises were wrong:** the editor was already
write-capable, and the real defect was the 50-row OData fetch losing the current value (Employee has
51 rows). Remaining ceiling — server-side search *in the combo* — is [[LOOKUP-002]] below._

#### RPT-002: Lookup-valued report parameters, and running a report for the current selection (ID: 1244)

**The two pieces RPT-001 deliberately left, now that reporting works end to end.**

**1. Lookup-valued parameters.** `ProductOrders.Product` is a `Guid` parameter identifying a Product.
Today it projects as `string` and renders as a text box — honest (you can paste a key) but crude. The report
carries the answer: `DynamicListLookUpSettings` names the data source and value/display members, and 8 of
this module's parameters use it. Resolving it would let the parameter project as a `lookup`, at which point
[[LOOKUP-001]]'s picker renders it with no further work — the payoff for making the parameter form reuse
the DetailView editors. Verify the settings object against installed 26.1 source first.

**2. Run a report for the current view/selection.** The plumbing exists: the run endpoint accepts a
`criteria` string and `ReportRenderService` passes it to `SetupReport`. What is missing is a Run action on a
list view that hands the grid's criteria through. The ceiling that makes it more than a button: the grid's
filter is a DevExpress `CriteriaOperator` translated for OData, while a report wants XAF criteria over the
report's own data source — close but not identical, and a filter that silently means something different in
the report than on screen is worse than no button. Check the translation before wiring it.

Neither is blocking; reporting is usable without both.

#### LOOKUP-002: Server-side search in the lookup editor (DxComboBox CustomData) (ID: 1240)

**The ceiling LOOKUP-001 stopped at, stated rather than implied.** `api/lookup/{type}` already supports
server-side search — `Contains()` over the display path, evaluated by the data store, verified live (a
search for "an" over 51 employees returned 13). The **editor** does not use it: `DxComboBox` is bound to
`Data`, so its AutoSearch filters only the page already fetched. Correct for every lookup target in this
model (largest is CustomerStore at 200, inside the endpoint's cap), wrong for a target with thousands of
rows where the right candidate may not be in the page being searched.

**What it takes** (verified against dxdocs 26.1): `DxComboBox.CustomData` is the documented remote-data
binding and speaks the **DevExtreme.AspNet.Data `LoadResult` protocol** — the delegate takes a
`DataSourceLoadOptionsBase` and returns `Task<LoadResult>`. So it adds a package and changes this
endpoint from a plain array to a `LoadResult` shape (or adds a second action that speaks it). Do it when a
real target app has a large lookup target; not worth reshaping a working contract before then.

Documented caveat to verify when doing it: with `OnDemand` load mode plus virtual scrolling, if `TData`
and `TValue` differ the component "may display the selected item's text incorrectly on page load" — which
is exactly the bug LOOKUP-001 fixed, so re-prove the current-value display rather than assuming it.

#### CHART-001: Project and render XAF chart views (ID: 1228)

**Backlogged 2026-08-09 under [[ARCH-001]]** — a chart view is never a first candidate when views are chosen
by pain. Deprioritised, not refuted; the evidence below stands and the card stays open.

**Premise checked against the model 2026-08-09, and it does not hold. Re-scoped before any code.**

The card assumed XAF exposes a chart *view* whose model carries series, argument/value members, series type
and diagram type. **There is no such thing in this reference module:** no chart view node of any kind in
`Model.DesignedDiffs.xafml` (only ListView, DetailView, DashboardView); `QuoteAnalysis_ListView` sounds like
the exception and is a plain `<ListView>` with two `<ColumnInfo>` entries. The module registers
`ChartModule`/`PivotGridModule`, and `Quote.cs` imports `DevExpress.XtraCharts` with a
`[CloneView(CloneViewType.DetailView, "Quote_DetailView_Pivot")]` and a `[NotMapped] PaletteEntry[]` — all
of it **platform-specific UI code**, none of it declared in the application model.

So a metadata-driven projector has nothing to project. This is the first concrete instance of MIG-002's
"complex ViewController behaviors … need bespoke client UI or don't cross the wire".

**Conclusion CONFIRMED and its reason replaced, 2026-08-09.** The conclusion above survives; the reasoning
under it was incomplete, because it was read off the *module* model while charts are declared in the *app*
model. Chart views do exist and they ARE declared — the reason they remain unprojectable is sharper than
"nothing is declared", and it is this:

    <ListView Id="Opportunity_ListView" EditorTypeName="…Blazor.Editors.DxChartListEditor"
              ChartType="DxPieChart" SettingTypeName="…Blazor.Server.Charts.OpportunityPieChart" />

The model declares **that** a view is a pie chart and **which type configures it**. It does not declare the
chart. `SettingTypeName` names a **Razor component**, and that component holds the whole definition —
FeatureCenter's equivalent is `ArgumentField="(Order x) => x.Customer.Country"`,
`ValueField="… x.Freight"`, `SummaryMethod="Enumerable.Sum"` (`FeatureCenter.Module.Blazor/Charts/
OrderSalesByCountryChart.razor`). Series, fields and aggregation are Blazor markup in an assembly a
headless host cannot reference.

**So inventing a chart contract really would be invention** — there is no model text to read, unlike
[[PIVOT-001]], where `PivotFieldArea`/`PivotSummaryType` sit on ordinary `ColumnInfo` nodes. That asymmetry
is the finding: pivot is projectable, chart is not, and they should stop being treated as one problem.
[[MODEL-001]] does not rescue this card — it changes which model layers are read, and no layer contains
the series definition.

**Three options, and the choice is the owner's:**
1. **Declare our own chart contract** — small metadata (series member, argument member, aggregate, criteria)
   an app opts into, rendered with `DxChart` over the wire-proven `$apply` path. Useful, but it is a
   platform feature we invent, NOT an XAF model projection — a departure from "the module's model is the
   contract" that every other feature here has honoured. Now known to be the ONLY route, not one of several.
2. ~~**Do [[DASH-001]] instead.**~~ Withdrawn — that recommendation was made on a wrong reading of the
   dashboards (see FEAT-000). DASH-001 is now unblocked on its own merits by NPO-001, not as a chart
   substitute.
3. **Drop it** until a target app has a model-declared chart — noting that on this evidence a Blazor XAF app
   never will, because the platform's own mechanism puts the definition in a Razor component.

Recommendation: **option 3 by default, option 1 only on explicit request.** Do not spend [[MODEL-001]]'s
outcome on this card; spend it on [[PIVOT-001]].

_RPT-001 (client-side reporting: catalogue, run, collect, download, and a parameter form that reuses
the DetailView editors) **done 2026-08-09** — see `docs/DONE.md`. Two nice-to-haves were left
deliberately and are [[RPT-002]] below: lookup-valued parameters, and running a report for the current
grid selection._

#### EDIT-002: Render DxHtmlPropertyEditor members safely (the XSS decision) (ID: 1241)

**The alias EDIT-001 deliberately did not implement, and why.** `DxHtmlPropertyEditor` is the most-declared
alias in the reference module — 7 uses, including `Order.OrderTerms` and `Order.Comments`, both on the
primary DetailView. EDIT-001 projects the alias and falls back to the plain string editor for it, so those
fields still read and edit as raw HTML text. Nothing is broken; nothing is rendered either.

**Why it stopped there: rendering stored HTML is an XSS vector.** The value comes from the database, and a
naive `@((MarkupString)value)` executes whatever is in it — script tags, event handlers, `javascript:` URLs
— in the authenticated user's session. The repo's own rule is that security measures are not simplified
away.

**Three options, roughly by cost:**
1. **Read-only sanitised render** — add a sanitiser (e.g. `HtmlSanitizer`), render cleaned markup read-only,
   keep the plain-text editor for editing. Smallest safe win; check the dependency's CVE history first.
2. **A real HTML editor** via DevExpress `DxHtmlEditor` — verify licensing and whether it is in the
   referenced packages before assuming; note a WYSIWYG still stores markup something must sanitise on the
   way *out*.
3. **Leave the string fallback** and document that HTML members render as source — what ships today.

Whatever is chosen, sanitise on the **render** path, not only on save: existing rows already hold whatever
they hold, and a save-time-only guard trusts data that predates it.

Also unimplemented, lower value: `PdfViewerPropertyEditor` (4 uses; needs a viewer plus the FILE-001 storage
decision), `MapHomeOfficePropertyEditor` and `EnumImageOnlyEditor` (demo-custom, no general meaning). All
fall back to their CLR editors and are fine there.

_CRUD-002 (inline nested-collection editing — per-row Delete plus New opening the child's own create
form with the master carried through the route) done 2026-08-09, **both halves** — see `docs/DONE.md`.
Gated on `LayoutNode.Aggregated` throughout, honouring GAP-010's rule that a shared collection needs
Link/Unlink rather than New/Delete._

#### EXPORT-001: Export a list view to XLSX/PDF (ID: 1236)

**Premise corrected 2026-08-09 against dxdocs, before any code. The recommendation survives; the reason
for it does not.**

This card claimed `DxGrid`'s export "in server mode only ever sees the current page (25 rows of 55k) — a
wrong answer dressed as a feature". **The documentation does not say that.** Nothing in the Blazor grid
export topics describes truncation to the loaded page, and the TreeList equivalent states the opposite:
*"Filter and export operations force the component to load all data."*

**The real trade-off:** client-side export is *correct* but forces the whole set into the client — 55,000
rows through the circuit for `Order`, which is exactly what FEAT-000's first standing decision forbids. The
case for a server-side export is **cost and the platform's premise**, not correctness. Someone might
reasonably decide 55k rows is fine for an occasional export, and they should decide on the true facts.

**Unverified and load-bearing:** this platform binds through `GridCustomDataSource`, not
`GridDevExtremeDataSource`. The documented limitations are written for the latter; export with a *custom*
data source is not described. **Probe it before building** — export a server-mode view and count the rows.

**Options:**
1. **Server-side via the JobServer**, reusing RPT-001's proven pipeline (enqueue → artifact with
   `RequestedBy` → poll → JS-interop download). Keeps data on the server; costs a job and an XLSX writer.
2. **Grid client-side export** — simple, honest for in-memory views, pulls everything for server-mode ones.
3. **Both**, switching on `GridBinding.UseServerMode`, the hybrid split GRID-002 already established.

Recommendation: **(1)**, on the cost argument rather than the correctness one this card used to make.

#### MODEL-001: Reconsider PH2-003 — should the host read app-level (and platform-module) model diffs? (ID: 1245)

**Reopens the question PH2-003 (539) closed on 2026-07-12, because evidence found on 2026-08-09 changes what
that decision costs.**

PH2-003 decided: *module-level model IS the platform contract; app-level `Model.xafml` customizations are
invisible to the headless host, and that is deliberate. Apps wanting customizations honored should put them
in a module.* It explicitly parked the alternative as a possible Phase-2 feature: *"host references the app
project's `Model.xafml`, merges its diffs before projection"*.

At the time that looked cheap. It is not, and here is the evidence.

**Every chart, pivot and dashboard declaration in every DevExpress XAF demo lives in a layer this host
deliberately does not read.** Verified 2026-08-09 across the installed 26.1 demos:

- `OutlookInspiredDemo.Blazor.Server/Model.xafml` — `Opportunity_ListView` as `DxChartListEditor`,
  `QuoteAnalysis_ListView` as `DxPivotGridListEditor`, plus two more chart views. **App level.**
- `MainDemo.Blazor.Server/Model.xafml` — the `PaycheckChart` DashboardViewItem and three `EditorType`
  overrides. **App level.** `MainDemo.Module/Model.DesignedDiffs.xafml` contains **zero** chart/pivot/
  dashboard nodes — checked, not assumed.
- `FeatureCenter.Module.Blazor/Model.DesignedDiffs.xafml` — three chart ListViews. Module level, but a
  **platform-specific (Blazor) module**, which a headless host cannot load either.

So "put them in a module" is advice no DevExpress demo follows, and for charts/pivots the natural home is
either the app project or a platform-specific module. Both are outside the contract as it stands.

**What this costs concretely.** It is the sole remaining blocker on [[PIVOT-001]], and the reason
[[DASH-001]] could not be verified against the demos. A pivot is *fully* model-declared — `EditorTypeName`
plus `PivotFieldArea`/`PivotSummaryType` on the ordinary `ColumnInfo` nodes the projector already walks — so
nothing about pivot needs inventing. It is unreachable purely because of which model layer this host merges.
(Charts are a different story and stay out regardless: `SettingTypeName` points at a Razor component holding
the series definition — see [[CHART-001]].)

**SPIKE RUN 2026-08-09 — and it invalidates this card's own premise. The blocker is NOT which model layer
we merge.** Measured against the live host (temporary reflection probe on `api/diagnostics/model`, reverted
after; the host was `QuoteAnalysis_ListView`):

1. **No Blazor module is loaded.** `Application.Modules` contains no Blazor module of any kind. Expected,
   but now measured rather than inferred.
2. **Column nodes carry no pivot extension at all** — the probe returned an empty interface list for
   `IModelColumn`. So `PivotFieldArea` / `PivotSummaryType` **do not exist as model properties in this
   host**. They are declared on `IModelColumnPivotGridBlazor` and registered in exactly one place:
   `SystemBlazorModule.ExtendModelInterfaces` (`DevExpress.ExpressApp.Blazor/SystemModule/Module.cs:232`,
   26.1 source).
3. **The platform-agnostic extensions ARE present**, courtesy of `ChartModule`/`PivotGridModule` which the
   demo pulls in via `RequiredModuleTypes` (`Module.cs:38,43`): `IModelChartListView.ChartSettings` and
   `IModelPivotListView.PivotSettings`.
4. **But that agnostic node is an opaque blob.** `IPivotSettings` exposes `Settings:String`,
   `LayoutSettings:String`, `ChartSettings:String` plus boolean totals/chart toggles — and **no** structured
   field or area members. The pivot definition is a serialized XtraPivotGrid settings string.

**What that means, and it is worse than this card assumed.** Merging the app-level `Model.xafml` would
achieve nothing on its own: `PivotFieldArea="Row"` would arrive with no extender to land on and be
discarded. The layer was never the binding constraint — **the model extenders are**.

**It also kills the option this card called cheapest.** "Keep PH2-003, let apps declare pivots in a
platform-agnostic module" is not available: XAF has no platform-agnostic *structured* pivot declaration. The
only structured one is Blazor's. So declaring pivots "our way" would be **inventing a contract** — precisely
what [[CHART-001]] refused to do. Pivot and chart are not as different as the previous entry claimed; pivot
is projectable only if we can reach the Blazor model extension.

**The real fork, and it is a genuine architecture decision:**

- **Load `SystemBlazorModule` into the Api host.** That is the only route to the structured pivot model.
  Note the assembly is *already* in the Api's output transitively (`DevExpress.ExpressApp.Blazor.v26.1.dll`),
  so the package reference is not the barrier — **registering the module is**, and it brings controllers,
  templates, list editors and `DataAccessModeHelper` registrations with it. That is the adapter tax the
  README says this project exists to escape, re-entering through the model door. It may still be the right
  call if it is inert without a `BlazorApplication`, which is unverified and is the next thing to measure.
- **Accept that pivot is out**, on the same evidence and for the same reason as chart: the declaration this
  platform would need is platform-specific. Cheapest, honest, and closes both cards for one stated reason
  instead of two guessed ones.
- **Parse the agnostic `Settings` blob.** Reverse-engineering a DevExpress serialization format. Named only
  to be rejected — it is re-implementing model logic, brittle across versions, and the README's line.

**SECOND SPIKE RUN 2026-08-09 — `SystemBlazorModule` registers INERT in this host. Recorded here because
the branch that proved it (`spike/model-001-blazor-module`, `1d87e18`) was deleted afterwards; this is now
the only record.**

- The host **boots** with `.Add<DevExpress.ExpressApp.Blazor.SystemModule.SystemBlazorModule>()`. No
  `BlazorApplication` required. The module has **no `Setup(XafApplication)` override**; its four overrides
  are `ExtendModelInterfaces` (what we want), `GetDeclaredControllerTypes` (controllers never instantiate
  without Frames, and a headless host creates none), `RegisterEditorDescriptors` (dormant without a UI) and
  `GetDeclaredExportedTypes`.
- Column nodes then carry `IModelColumnPivotGridBlazor.PivotFieldArea`, `.PivotGroupInterval` and
  `.PivotSummaryType`.
- **Api.Tests 85/85 green.** Extending the model for every view changed nothing any existing test observes.
- **No new PackageReference needed** — `DevExpress.ExpressApp.Blazor` is already in the Api's transitive
  closure (it arrives with the WebApi/demo dependency graph).

**What it does NOT prove.** `PivotFieldArea` read `Row` for `QuoteAnalysis`'s first column — that is the
interface's `[DefaultValue(PivotTableArea.Row)]`, **not** the demo's declaration. The declared values live in
`OutlookInspiredDemo.Blazor.Server/Model.xafml`, which this host still does not merge. **Extenders and layer
are a pair**: the module supplies the properties, the layer supplies the values, and neither alone is
sufficient. An earlier revision of this card treated them as alternatives; that was wrong.

**So option 1 is technically viable, and the remaining question is whether it is wanted.** Under the
direction recorded in [[ARCH-001]] (headless as a growth path for individual painful views, not a
replacement) a dashboard/chart/pivot is never a first candidate, so this card is **deprioritised to Backlog
rather than closed** — the evidence is banked and the option stays open if a target app ever needs a pivot
projected.

**Do not build any view against this without revisiting ARCH-001 first.**

#### PIVOT-001: Project and render XAF PivotGrid (analysis) views (ID: 1227)

**Backlogged 2026-08-09 under [[ARCH-001]]** — a pivot is never a first candidate when views are chosen by
pain. Deprioritised, not refuted: [[MODEL-001]]'s spike proved the route is technically viable, so this
reopens cheaply if a target app ever needs one.

**PREMISE CORRECTED 2026-08-09. The earlier finding — "not model-declared, so there is nothing to project"
— was WRONG, and wrong in a way worth understanding: it was read off the demo's *module* model, while the
pivot is declared in its *app* model.**

A Blazor pivot is **fully model-declared**, and by the very nodes this projector already walks.
`OutlookInspiredDemo.Blazor.Server/Model.xafml`:

    <ListView Id="QuoteAnalysis_ListView" EditorTypeName="…Blazor.Editors.DxPivotGridListEditor">
      <Columns>
        <ColumnInfo Id="State"       PivotFieldArea="Row"  SortOrder="Ascending" />
        <ColumnInfo Id="City"        PivotFieldArea="Row"  SortOrder="Ascending" />
        <ColumnInfo Id="Total"       PivotFieldArea="Data" PivotSummaryType="Sum"     Caption="Opportunities" DisplayFormat="{0:C0}" />
        <ColumnInfo Id="Opportunity" PivotFieldArea="Data" PivotSummaryType="Average" Caption="Percentage" />
      </Columns>
    </ListView>

`PivotFieldArea` and `PivotSummaryType` are properties on the ordinary `ColumnInfo` nodes. No companion
type, no code, nothing to invent — the pivot definition is model, end to end.
`IModelListViewPivotGridBlazor.LayoutSettings` also exists but is **not** the definition: its own
description is "Stores the runtime changes in a Pivot Grid layout" (26.1 source,
`DevExpress.ExpressApp.Blazor/SystemModule/Module.cs:516-520`) — user drag-drop state, not design time.

**So this card is unblocked in principle and blocked in practice by one thing only: which model layer this
host merges.** That is [[MODEL-001]], and it must be settled first — it decides whether the pivot view is
authored in a host-owned module or read from the app layer, which are different pieces of work.

**What is now true about the aggregation question** this card was originally sized on: it is real again,
and NPO-001 already met its shape once. `QuoteAnalysis` is one row per Quote (10,000 seeded), and both
Blazor and WinForms pivot editors are **client-data-access only** (`PivotGridListEditor` explicitly rejects
Server/ServerView/InstantFeedback, dxdocs 26.1) — so the aggregation has to happen server-side before rows
reach the editor, exactly the constraint the `TopReturnedObjects` cap addresses for lists.

`PivotGridModule` is registered in the demo's `Module.cs`, and `Quote_DetailView_Pivot` is a cloned
DetailView whose pivot-ness is platform code — both true, neither load-bearing now that the real
declaration has been found.

#### DASH-001: Project and render DashboardViews (ID: 1229)

**Backlogged 2026-08-09 under [[ARCH-001]]** — a dashboard is never a first candidate when views are chosen
by pain. Deprioritised, not refuted; NPO-001 genuinely unblocked it and that stands.

**Premise checked 2026-08-09 — including a claim I made myself two iterations earlier, which was wrong.**

While re-scoping [[CHART-001]] I recommended doing DASH-001 instead, on the grounds that "two real
DashboardViews exist and are composed of ordinary ListViews the platform already renders". That was read
off the view *names* in the XAFML without checking their contents. Both halves are wrong:

- **`Welcome` contains one `<StaticImage>`** — a decorative SVG. No view, no data, nothing to bind.
- **`Opportunities` has two `DashboardViewItem`s**, `Opportunity_ListView` and `QuoteAnalysis_ListView`.
  Both target types are **`[DomainComponent]` non-persistent classes** with no `DbSet` — populated in memory
  by XAF handlers, never stored. They are not OData-exposed and cannot be: that is [[NPO-001]].

So this module cannot demonstrate a working dashboard at all: one is decorative, the other is composed
entirely of data that never crosses the wire.

**What remains true:** a DashboardView *is* model-declared, and projecting one (items referencing other
views, plus layout) is legitimate work for an app whose dashboards sit over persistent types. The projection
is not the hard part — `NavigationMetadataTests` asserts no dashboard reaches the menu, so closing this means
changing a rule and its test, which is ordinary.

**But it cannot be verified end-to-end here**, and shipping a view type that renders two broken tiles against
the only dashboards available is worse than not shipping it — the same reasoning that parked SCHED-001.

**Unblocked 2026-08-09: [[NPO-001]] is done** (`docs/DONE.md`). `Opportunity_ListView` and
`QuoteAnalysis_ListView` now load real data, so the `Opportunities` dashboard's two tiles are no longer
broken-by-construction and this card can finally be verified end-to-end here. What remains is DASH-001's
own work — projecting the DashboardView (items referencing other views, plus layout) and changing the
`NavigationMetadataTests` rule that currently asserts no dashboard reaches the menu.

Note the second tile is the LARGE case: `QuoteAnalysis` is one row per Quote (10,000 in the seeded tenant),
served capped at 5,000 with the true total reported. A dashboard tile is exactly where that cap wants
thinking about — a tile is not a browsing grid, and 5,000 rows behind one is almost certainly the wrong
answer even though it is the right default for a full-page ListView.

#### FILE-001: File attachment upload and download (ID: 1234)

**Named by MIG-002, half-blocked by a deliberate earlier decision.** After BUG-001/UI-001 a `byte[]` member
is display-only: it projects as `image` and renders as an `<img>` data-URI. There is no upload, and no
support for the XAF FileAttachments module — though `Startup.cs:112` already auto-creates that module, so
the server side is closer than it looks.

Scope: an upload editor, a download path, and a storage decision. **MIG-002 is explicit that blobs must NOT
stream inline through OData** — it calls for a file/blob storage service behind upload/download endpoints;
honour that. Two rules this repo already holds apply directly: detect file type by **magic bytes, not
extension** (`ImageEditor` already sniffs MIME that way), and validate at the trust boundary — uploads are
untrusted input, so size limits and content-type checks are not optional simplifications.

Sizing: medium, plus a storage decision (filesystem vs S3/Azure) that should be made explicitly rather than
defaulted.

#### SCHED-001: Scheduler view support (ID: 1237)

**Gap, verified 2026-08-08.** The XAF Scheduler module is auto-created in `Startup.cs:112`, so the module
loads, but nothing projects or renders a scheduler view.

Scope: project the scheduler view's model (appointment source mappings — start, end, subject, resource) and
render with `DxScheduler`, backed by an OData fetch windowed to the visible date range.

**Kept in the board's Backlog lane, not Todo, deliberately:** unlike pivot/chart/reporting it has no demand
signal here — the reference module does not exercise it, and MIG-002 lists scheduler only as a "check
whether the target app uses it" item. Promote it when a real consumer appears rather than building it
speculatively.


_GAP-004 (nav menu, minimal scope) done 2026-07-12 — see `docs/DONE.md`._

_GAP-002 (conditional appearance — per-row colors + enum rules, incl. the non-column enum-fix) done 2026-07-13 — see `docs/DONE.md`._

_UI-001 (client UI enhancement — Office White DevExpress theme, image rendering for byte[] members, styled nav/login/chrome) done 2026-07-13 — see `docs/DONE.md`._

_DOC-001 (overhaul `docs/HOW-TO-IMPLEMENT.md` — single-tenancy as the common path, multi-tenancy boxed as the important variant; folded in theme/image/nested-child gotchas; added a "Run it locally" section) done 2026-07-13 — see `docs/DONE.md`._

_GRID-001 (grid column chooser + group-by box + header context menu, client-side over a capped in-memory load) done 2026-07-13 — see `docs/DONE.md`._

_GRID-002 (server-side grouping/paging for large views via OData `$apply` — hybrid binding, landed via a backport of the companion headless implementation's GRID-001 work) done 2026-07-14 — see `docs/DONE.md`._

_GRID-003 (auto filter row for all column types — lifted the GAP-005 scalar-only restriction now that filtering is client-side) done 2026-07-13 — see `docs/DONE.md`._

#### MIG-002: Headless-migration readiness — gap list + service boundaries for an existing XAF Blazor app (ID: 558)

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

#### GAP-010: Link / Unlink actions for nested list views (association write path) (ID: 559)

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

_PH2-003 (app-level XAFML diffs — **decided 2026-07-12**: module-level model IS the platform contract;
app-level customizations out of scope, no code) closed as a decision — see `docs/DONE.md`._

_PH2-005 (dedicated read-only lookup endpoint) done 2026-08-09 as part of LOOKUP-001 — see
`docs/DONE.md`. It was deferred as YAGNI in 2026-07-12 for want of a consumer; the write-capable
lookup editor was that consumer, so the two were built together._

_P2 status: **GAP-002 / GAP-004 / GAP-005 / GAP-007 / GAP-009** done (see `docs/DONE.md`); **PH2-003** closed
as a decision (moved to `docs/DONE.md`); **PH2-005** deferred (YAGNI, owner-reviewed). **Still open above:**
**GAP-010** — the client Link/Unlink UI, its server half shipped — and **MIG-002**, a planning artifact with
no code. Below is P3 hardening._

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

_Remaining actionable P3 follow-ups: the **GAP-008** minors, and the two additive **PH2-006** bits
(`$metadata` cross-check, runtime-validation-in-`Required`) — both noted in their DONE records — plus
**DIAG-002** and **TEST-002** below. **DATA-001**, **TEST-001** and **GAP-002** are done (above)._

_SVR-001 (JobServer — background jobs + report rendering as a separate service; folds in **SVR-002** unique
index on `JobDefinition.JobTypeName`, **SVR-003** the DevExpress OData host-shared-BO read fix, and **SVR-004**
the Api publish fix) done 2026-07-21, merged to master — see `docs/DONE.md`._

_Owner follow-up (not code): file the **DevExpress support ticket** for the OData constant-parameterization
framework defect — draft ready at [`docs/notes/devexpress-ticket-odata-shared-bo.md`](docs/notes/devexpress-ticket-odata-shared-bo.md); bundle the deferred `$select`→`edmModel` bug into it._

_UI-002 (Modernist theme), DIAG-001 (runtime diagnostics), and BUG-003…BUG-007 (five grid/wire defects, all
found and fixed 2026-08-08) done — see `docs/DONE.md`. Their two recorded upgrade paths are **GRID-005** and
**GRID-006** in P1 above._

#### DIAG-002: Durable log sink / end-to-end correlation — deliberately deferred (ID: 1224)

**Decided, not overlooked** (`docs/superpowers/specs/2026-08-08-runtime-diagnostics-design.md`, "Out of
scope"). DIAG-001 deliberately added **no dependencies**: failures name themselves in the console and on
screen, but nothing survives the process. What was ruled out and why it might come back:

- **Persistent structured sink** (Serilog file/OTLP): today reproducing a failure means having the console
  open. Worth it the first time a failure is reported after the fact rather than observed.
- **Correlation IDs Web → Api → JobServer**: the client and server logs currently correlate by timestamp and
  URL only. Worth it the first time an intermittent multi-host bug needs the three logs stitched.
- **E2E network capture**: the suite reports assertion failures, not the 4xx behind them. Both grid bugs this
  cycle were diagnosed from the *hosts'* logs; a test that attached the failing request to its own output
  would have shortened that. Cheap to add to `PlaywrightFixture` if E2E triage recurs.

No action needed while the current instrumentation keeps answering the questions asked of it.

#### TEST-003: Restore end-to-end coverage of the AllowSort=false binding (lost to BUG-008) (ID: 1239)

**A real coverage loss, recorded rather than left implied.** GRID-005 refuses to sort a lookup whose display
path cannot resolve to a primitive, and `LookupSortCeilingE2ETests` proved that live against Store. BUG-008
then made Store resolve (`Emblem.CityName`) — the correct outcome, but it removed the only live example.
Checked across all seven navigable views on 2026-08-09: **every** lookup this model projects now resolves to
a string, so no browser test can drive the ceiling.

Still covered: the predicate, by three unit tests in `GridBindingTests`. **Not** covered: that `XafListView`
actually binds `DxGridDataColumn.AllowSort` to that predicate — a wiring regression there would now pass
every test.

**To restore it:** add a dev-only fixture type whose default property cannot resolve — a cycle (two types
whose default properties reference each other, which the projector's `visited` guard is written for and
which nothing currently exercises) or a blob default property. Copy the shape of the host-owned
`LookupProbe`, already a dev-only projection fixture excluded from navigation. Then assert the column offers
no sort and that clicking its header does not reorder.

Low priority: the unguarded path is also unreachable in this model today, so the risk is a future regression
rather than a present defect. Do it when someone next touches the sort ceiling, and before any target app
with an unresolvable lookup relies on it.

#### TEST-002: Sweep tooling produced two phantom findings — prefer wire evidence (ID: 1225)

**Method note from the 2026-08-08 sweep, worth keeping before anyone repeats it.** Two rounds of "bugs" came
from the *sweep scripts*, not the app:

1. **Stale grid** — the previous view's grid stays mounted while the next loads, so "wait for a row to exist"
   returns instantly and samples the OLD view's cells. Reported five all-blank columns that did not exist.
   Fix: wait for the header set to CHANGE before sampling.
2. **Wrong control** — clicking a group row does not expand it (`.dxbl-grid-expand-button` does), and a
   context-menu locator matching "Group" also matches "Ungroup". Reported "children never page in", which was
   false.

Both were caught the same way: the **Api request log** showed no corresponding request, so the interaction —
not the app — was at fault. Treat sweep output as a lead and confirm it on the wire. Not code to write; a
convention to keep, which is why it lives here rather than in a test.

---

**Closed by the migration** (not tracked above — see `docs/DONE.md` MIG-001):
- **SEC-001** (OData write guard) → `Middleware/ODataReadOnlyMiddleware.cs`, Task 1.
- **PH2-001** (KeyMember projection) → `ViewMetadata.KeyMember`, Task 2.
- **Required-flag/save mismatch** → closed by migrating to a model with real `[RuleRequiredField]`
  rules (`Employee`), Task 2; see `docs/notes/save-contract.md`.
- **Auto-mode / render-mode-freedom proof** → dual-phase (Server + WebAssembly) E2E, Tasks 3–4.
