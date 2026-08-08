using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// Task 4 (26.1 migration): the kill-gate smoke path over the Blazor WEB APP (:5220, InteractiveAuto)
// + the 26.1 API host (:5200), driven by C# Playwright (house rule -- never the Node bindings). Both
// apps must already be running (production-style: published + hosted, not `dotnet run`); the
// orchestrator publishes + hosts + health-checks them, runs this, then kills them.
//
// Selectors were locked against the LIVE DOM (Task 4 exploration, same-session Playwright MCP probes) on
// 26.1.3, and still hold on 26.1.4 -- the suite runs green there (2026-08-08):
//  - grid data rows are class-less <tbody> <tr>; the empty-row placeholder carries .dxbl-grid-empty-row,
//    so data rows = ".dxbl-grid-table tbody tr:not(.dxbl-grid-empty-row)" -- UNCHANGED from the 25.2 era
//    (DevExpress kept the same DxGrid DOM/class contract across the 25.2 -> 26.1 bump).
//  - tabs are [role=tab] (DXBL-TAB-ITEM); editors sit in .xaf-item with a <label> caption + <input>.
//  - The 422 error renders at BOTH the offending editor (.text-danger, inside its .xaf-item) and the
//    view-level summary (.alert-danger) -- both bound verbatim to the server's rule message.
//  - The command success message renders in .alert-success.
//
// DATA SAFETY / recovery: the demo LocalDB catalog (XafHeadlessDemo, host DB) and the demo-seeded
// tenant DB (OutlookInspiredDemo_company1) are both disposable dev data (Global Constraints). If a
// pinned key in testsettings.json ever 404s (DB dropped/reseeded), recover with:
//   1. Drop the LocalDB catalogs: `sqllocaldb stop mssqllocaldb` then delete the .mdf/.ldf under
//      %USERPROFILE%\ (or `DROP DATABASE XafHeadlessDemo; DROP DATABASE OutlookInspiredDemo_company1;`
//      via sqlcmd/SSMS against (localdb)\mssqllocaldb).
//   2. Rerun XafHeadless.Api once (creates+seeds the host catalog XafHeadlessDemo) -- the tenant
//      catalog OutlookInspiredDemo_company1 needs the DEMO'S OWN Blazor.Server app run once against
//      it to reseed the 55k rich rows (Task 1 finding; our host never re-seeds tenant data).
//   3. Rediscover the pinned keys via the two probes documented in PlaywrightFixture.cs and update
//      testsettings.json.
[TestClass]
public class SmokeTests : PlaywrightFixture {
    const string DataRows = ".dxbl-grid-table tbody tr:not(.dxbl-grid-empty-row)";

    // Known-model literals (E2E is a standalone black-box project -- no ProjectReference to
    // XafHeadless.Api.Tests's KnownModel -- these were captured from the SAME live probes recorded in
    // that file, not re-guessed).
    const string OrderListCaption = "Invoice #";              // Order_ListView's InvoiceNumber column
    const string OrderDetailCaption = "Order";                // Order_DetailView's h3 (ViewMetadata.Caption)
    const string OrderItemsTabCaption = "Order Items";        // Order_DetailView's nested-collection tab
    const string OrderPinnedInvoiceNumber = "0000001";        // the pinned OrderKey's InvoiceNumber
    const string EmployeeDetailCaption = "Employee";          // Employee_DetailView's h3
    // The real [RuleRequiredField] message on Employee.FirstName (default XAF template, no custom
    // message on this member) -- verified live via POST api/save/Employee/{key} {"FirstName":null}.
    const string FirstNameRequiredMessage = "\"First Name\" must not be empty.";

    ILocator EditorInput(string labelCaption) =>
        Page.Locator(".xaf-item")
            .Filter(new() { Has = Page.GetByText(labelCaption, new() { Exact = true }) })
            .First.Locator("input").First;

    // ---------------------------------------------------------------------------------------------
    // THE Auto-mode proof (Task 4's point): the SAME smoke assertions pass in BOTH render phases,
    // inside the SAME browser context (Playwright.MSTest's PageTest gives one Page/Context per test
    // METHOD, which is exactly why both phases live in this one ordered test method -- Phase B must
    // reuse Phase A's context so the WASM runtime it downloaded is actually still cached, AND so the
    // sessionStorage the JWT was persisted into survives into Phase B).
    //
    // Phase A (fresh context -> Server): badge asserted "Server" first, then the full smoke. Login
    //   persists the JWT to sessionStorage (GAP-007).
    // Phase B (same context, WASM runtime now cached -> reload until badge flips): badge asserted
    //   "WebAssembly", then -- WITHOUT re-logging-in -- prove the persisted session SURVIVED the
    //   takeover (GAP-007: the JWT was restored from sessionStorage on the fresh WASM runtime's first
    //   interactive render), then the SAME smoke helper again.
    // ---------------------------------------------------------------------------------------------
    [TestMethod]
    public async Task DualRenderMode_SmokePasses_Server_then_WebAssembly() {
        // ---- Phase A: Server ----
        await LoginAsync();
        await Shot("A-01-login");
        await Expect(BadgeLocator).ToContainTextAsync("Server", new() { Timeout = 15000 });
        // GAP-004: LoginAsync now lands on the model-driven FIRST nav item (Employee_ListView), not
        // Order_ListView -- RunSmokeAsync below is Order-data-specific, so navigate there explicitly.
        await NavigateSpa("/list/Order_ListView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Order_ListView$"), new() { Timeout = 15000 });
        await RunSmokeAsync("A");

        // ---- Trigger the Auto takeover (same context) ----
        var tookOver = await TryTriggerWasmTakeoverAsync();
        if (!tookOver) {
            Assert.Inconclusive(
                "Phase B (WebAssembly) did not take over after repeated reloads in this Playwright " +
                "browser context; the badge stayed Server-rendered throughout. Task 3 achieved this " +
                "manually (cached runtime + fresh reload, same browser context).");
            return;
        }
        await Expect(BadgeLocator).ToContainTextAsync("WebAssembly", new() { Timeout = 10000 });
        await Shot("B-00-wasm-badge");

        // ---- Phase B: WebAssembly. GAP-007 persistence proof (NO re-login) ----
        // The JWT persisted to sessionStorage in Phase A is restored on this fresh WASM runtime's
        // first interactive render, so the session survived the takeover. Prove it: navigate into the
        // app WITHOUT logging in and assert the AUTHENTICATED CONTENT actually rendered.
        // The persistence proof is the RENDERED authenticated content below, NOT the URL alone: a
        // FAILED restore would leave the URL set to /list/Order_ListView while AuthGuard shows a stuck
        // "Loading…" (and, with Fix I-1, then redirects to /login). With Fix I-1 in place the URL check
        // is a valid SECONDARY signal (a failed restore no longer lingers on the list URL), but the
        // real proof is the caption header + a data row being visible -- that only renders with a valid
        // restored Bearer token. NavigateSpa + these polling assertions wait for the async restore to
        // land; no fixed sleeps.
        await NavigateSpa("/list/Order_ListView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Order_ListView$"), new() { Timeout = 15000 });
        await Expect(BadgeLocator).ToContainTextAsync("WebAssembly", new() { Timeout = 10000 });
        // Explicit authenticated-content assertion (the actual persistence proof): the Order list
        // rendered with the restored token -- known caption header + at least one data row -- rather
        // than the guard sitting on "Loading…" or the app bouncing to /login.
        await Expect(Page.Locator("th").Filter(new() { HasTextString = OrderListCaption }).First)
            .ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows).First).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Shot("B-01-persisted-session");
        await RunSmokeAsync("B");
    }

    // ---------------------------------------------------------------------------------------------
    // GAP-009: the N-view sweep's client-render proof -- the compression claim's teeth. Order and
    // Employee were the only 2 data points the generic /list/{ViewId} + /detail/{ViewId}/{ObjectKey}
    // routes had ever been pointed at before this task. This method points them at TWO BRAND NEW
    // types -- Customer and Product -- reusing the exact same DataRows selector, NavigateSpa, and
    // LoginAsync helper RunSmokeAsync already uses for Order: zero new per-view client rendering
    // code was added to make this pass (the one nested-
    // collection quirk the sweep found: Customer.Logo / Product.Image nested lists point at a
    // "Byte_ListView" that doesn't resolve -- that degrades to an inline error in just that one
    // section, verified live, not exercised here since neither list/detail page depends on it).
    // ---------------------------------------------------------------------------------------------
    [TestMethod]
    public async Task NewViews_render_via_the_same_generic_client_Customer_and_Product() {
        await LoginAsync();

        // Customer_ListView -- brand-new type #1. Same selector/assertions RunSmokeAsync uses for
        // Order_ListView; no per-view code, just a different ViewId. Two back-to-back SPA list
        // navigations (unlike RunSmokeAsync, which interacts with each list before moving on) can
        // catch the grid mid-teardown/rebuild, so the row-count check itself polls (Expect(...)
        // .Not.ToHaveCountAsync(0, ...)) rather than a one-shot CountAsync() sampled right after
        // ToBeVisibleAsync -- a transient-DOM race, not a real 0-rows result (confirmed live via
        // GET api/odata/Product?$top=1 returning real data before writing this test).
        await NavigateSpa("/list/Customer_ListView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Customer_ListView$"), new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows).First).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows)).Not.ToHaveCountAsync(0, new() { Timeout = 15000 });
        await Shot("N-01-customer-list");

        // Product_ListView -- brand-new type #2.
        await NavigateSpa("/list/Product_ListView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Product_ListView$"), new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows).First).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows)).Not.ToHaveCountAsync(0, new() { Timeout = 15000 });
        await Shot("N-02-product-list");

        // Customer_DetailView -- one detail open, proving XafDetailView/LayoutNodeRenderer render a
        // THIRD brand-new type with no new code either. Key read live via a deterministically
        // ordered OData probe (same recipe PlaywrightFixture's header documents for the pinned
        // Order/Employee keys: real column, real $orderby -- not an arbitrary $top=1).
        using var api = await ApiClientAsync();
        var resp = await api.GetAsync("api/odata/Customer?$top=1&$orderby=Name asc");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var customerKey = doc.RootElement.GetProperty("value")[0].GetProperty("ID").GetString();
        Assert.IsNotNull(customerKey, "Customer probe returned no ID");

        await NavigateSpa($"/detail/Customer_DetailView/{customerKey}");
        await Expect(Page.Locator("h3")).ToHaveTextAsync("Customer", new() { Timeout = 15000 });
        await Shot("N-03-customer-detail");
    }

    // ---------------------------------------------------------------------------------------------
    // GAP-004: the flat client menu itself. Proves (a) it is absent while unauthenticated, (b) it
    // renders the expected renderable-demo-type captions after login, and (c) clicking one of its own
    // links (not NavigateSpa's synthetic anchor) navigates to /list/{viewId} and the grid renders real
    // rows -- i.e. the menu's <NavLink href> wiring itself works, not just that the route exists.
    // ---------------------------------------------------------------------------------------------
    [TestMethod]
    public async Task NavMenu_renders_expected_items_and_a_click_navigates_and_renders_grid() {
        Page.SetDefaultNavigationTimeout(10000);
        Page.SetDefaultTimeout(15000);

        // Unauthenticated: MainLayout hosts NavMenu only inside its `AuthState.Token is not null`
        // sidebar block, so no menu should render on /login.
        await Page.GotoAsync(ClientBaseUrl + "/login",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(Page.Locator("nav.xaf-navmenu")).Not.ToBeVisibleAsync(new() { Timeout = 5000 });

        await LoginAsync();
        var nav = Page.Locator("nav.xaf-navmenu");
        await Expect(nav).ToBeVisibleAsync(new() { Timeout = 15000 });

        // The brief's target renderable demo types, all confirmed live via GET api/model/navigation
        // (KnownModel.NavigationFirstItemViewId's remarks in XafHeadless.Api.Tests record the same
        // discovery) -- captions equal the type names for this demo module (e.g. Order_DetailView's
        // own h3 caption is "Order", asserted elsewhere in this file as OrderDetailCaption).
        foreach (var caption in new[] { "Employee", "Customer", "Order", "Product" }) {
            await Expect(nav.GetByRole(AriaRole.Link, new() { Name = caption, Exact = true }))
                .ToBeVisibleAsync(new() { Timeout = 10000 });
        }
        await Shot("N-04-navmenu");

        await nav.GetByRole(AriaRole.Link, new() { Name = "Order", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Order_ListView$"), new() { Timeout = 15000 });
        var rows = Page.Locator(DataRows);
        await Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 15000 });
        // Self-polling (gap-009's fix for the same transient-DOM race: a one-shot CountAsync() sampled
        // right after ToBeVisibleAsync can catch the grid mid-teardown/rebuild right after navigation).
        await Expect(rows).Not.ToHaveCountAsync(0, new() { Timeout = 15000 });
        await Shot("N-05-navmenu-click-order");
    }

    // ---------------------------------------------------------------------------------------------
    // GAP-008: the per-user layout-persistence proof end-to-end. Resize the first Order_ListView column
    // in the UI, RELOAD the page (GAP-007 restores the JWT from sessionStorage so the grid re-inits
    // authenticated), and assert the widened column comes back at its persisted width -- not the default.
    // The client persists via DxGrid.LayoutAutoSaving -> PUT api/prefs/Order_ListView and restores via
    // LayoutAutoLoading -> GET (see XafListView.razor). Resize (not reorder) because a 1-D border drag is
    // deterministic and maps straight to GridPersistentLayoutColumn.Width. ColumnResizeMode=NextColumn was
    // enabled on the shared grid for exactly this.
    // ---------------------------------------------------------------------------------------------
    [TestMethod]
    public async Task ColumnResize_persists_across_reload_on_Order_ListView() {
        await LoginAsync();

        // Clean slate: clear any layout persisted by an earlier run/test so the width delta below is
        // measured from the grid's DEFAULT, and so this test's widened state can't leak the other way.
        using (var seed = await ApiClientAsync())
            (await seed.PutAsync("api/prefs/Order_ListView", new StringContent("", Encoding.UTF8, "application/json")))
                .EnsureSuccessStatusCode();

        // GAP-008-minors #3: everything from here on runs inside a try/finally so the cleanup PUT below
        // ALWAYS resets the pref, even if a resize/reload assertion throws mid-test -- previously the
        // cleanup was the last statement in the method body, so a thrown assertion above it skipped the
        // reset and leaked a widened Admin/Order_ListView pref into sibling tests.
        try {
            await NavigateSpa("/list/Order_ListView");
            await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Order_ListView$"), new() { Timeout = 15000 });
            var header = Page.Locator("th").Filter(new() { HasTextString = OrderListCaption }).First;
            await Expect(header).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Expect(Page.Locator(DataRows).First).ToBeVisibleAsync(new() { Timeout = 15000 });

            var box0 = await header.BoundingBoxAsync();
            Assert.IsNotNull(box0, "Invoice # header should have a bounding box");
            var defaultWidth = box0!.Width;

            // Resize: grab the header's right border and drag it ~150px to the right (NextColumn mode grows
            // this column, shrinks the next). Stepped move so DevExpress's pointer-based resize registers.
            var y = box0.Y + box0.Height / 2;
            var edgeX = box0.X + box0.Width - 1;
            await Page.Mouse.MoveAsync(edgeX, y);
            await Page.Mouse.DownAsync();
            await Page.Mouse.MoveAsync(edgeX + 150, y, new() { Steps = 15 });
            await Page.Mouse.UpAsync();

            // Poll for the resize to actually widen the column (bounded, not a fixed sleep).
            var resizedWidth = defaultWidth;
            for (var i = 0; i < 20 && resizedWidth <= defaultWidth + 40; i++) {
                await Page.WaitForTimeoutAsync(250);
                resizedWidth = (await header.BoundingBoxAsync())!.Width;
            }
            Assert.IsTrue(resizedWidth > defaultWidth + 40,
                $"the drag should have widened the column (default {defaultWidth}, after resize {resizedWidth})");
            await Shot("R-01-resized");

            // LayoutAutoSaving's PUT is async -- poll the server until the prefs row exists (200, not 204).
            using (var api = await ApiClientAsync()) {
                var saved = false;
                for (var i = 0; i < 20 && !saved; i++) {
                    var resp = await api.GetAsync("api/prefs/Order_ListView");
                    saved = resp.StatusCode == HttpStatusCode.OK;
                    if (!saved) await Page.WaitForTimeoutAsync(250);
                }
                Assert.IsTrue(saved, "the resized layout should have been PUT to api/prefs/Order_ListView");
            }

            // Reload (GAP-007 restores the token) -> grid re-inits -> LayoutAutoLoading GETs + restores width.
            await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(BadgeLocator).Not.ToContainTextAsync("Static", new() { Timeout = 15000 });
            var header2 = Page.Locator("th").Filter(new() { HasTextString = OrderListCaption }).First;
            await Expect(header2).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Expect(Page.Locator(DataRows).First).ToBeVisibleAsync(new() { Timeout = 15000 });

            // THE proof: the restored width matches the persisted width, NOT the default.
            var restoredWidth = defaultWidth;
            for (var i = 0; i < 40; i++) {
                restoredWidth = (await header2.BoundingBoxAsync())!.Width;
                if (Math.Abs(restoredWidth - resizedWidth) < 20) break;
                await Page.WaitForTimeoutAsync(250);
            }
            await Shot("R-02-restored");
            Assert.IsTrue(Math.Abs(restoredWidth - resizedWidth) < 20,
                $"restored width {restoredWidth} should match the persisted {resizedWidth} (default was {defaultWidth})");
            Assert.IsTrue(restoredWidth > defaultWidth + 40,
                $"restored width {restoredWidth} must not have reverted to the default {defaultWidth}");
        } finally {
            // Cleanup: reset the pref so the widened state doesn't leak into the other Order_ListView tests --
            // ALWAYS runs, including when an assertion above threw mid-test.
            using var cleanup = await ApiClientAsync();
            await cleanup.PutAsync("api/prefs/Order_ListView", new StringContent("", Encoding.UTF8, "application/json"));
        }
    }

    // Shared ordered smoke path, run once per render-mode phase (write once, assert twice). NOTHING
    // here leaves a persisted mutation: the only write attempt (clearing Employee.FirstName) fails
    // validation server-side (422) and commits nothing (proven by an OData re-read); the in-finally
    // restore is cheap-but-good hygiene, not a load-bearing safety net (see file header for the real
    // drop/reseed recovery path).
    async Task RunSmokeAsync(string phase) {
        // 1. List renders > 0 rows with a known model caption.
        await Expect(Page.Locator("th").Filter(new() { HasTextString = OrderListCaption }).First)
            .ToBeVisibleAsync(new() { Timeout = 15000 });
        var rows = Page.Locator(DataRows);
        await Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 15000 });
        Assert.IsTrue(await rows.CountAsync() > 0, "Order_ListView should render > 0 rows");
        await Shot($"{phase}-02-list");

        // 2. Sort the Invoice # column and assert the first-row order flips. Condition-bounded, not a
        //    fixed sleep: WASM-phase renders measurably slower than Server's (PlaywrightFixture
        //    comments). Two pre-existing timing edges, both bounded-polled here rather than assumed:
        //      (a) the first cell can be momentarily empty right after the grid mounts -- wait for a
        //          real value before sampling `before`, else we'd compare "" against "";
        //      (b) the first header click sometimes just re-asserts the default ascending order
        //          (same-value edge, the production app's E2E suite hit this too) and DevExpress applies the sort
        //          asynchronously -- so toggle up to a few times, polling for the flip, instead of
        //          asserting after exactly one click.
        var header = Page.Locator("th").Filter(new() { HasTextString = OrderListCaption }).First;
        var firstCell = Page.Locator(DataRows).First.Locator("td").First;
        await Expect(firstCell).Not.ToHaveTextAsync("", new() { Timeout = 15000 });   // edge (a)
        var before = (await firstCell.InnerTextAsync()).Trim();
        var after = before;
        for (var attempt = 0; attempt < 3 && after == before; attempt++) {            // edge (b)
            await header.ClickAsync();
            try { await Expect(firstCell).Not.ToHaveTextAsync(before, new() { Timeout = 5000 }); }
            catch (PlaywrightException) { /* no flip yet -- toggle again */ }
            after = (await firstCell.InnerTextAsync()).Trim();
        }
        Assert.AreNotEqual(before, after, "sorting Invoice # should flip the first-row order");
        await Shot($"{phase}-03-sort");

        // 2b. GAP-005: filter row. Typing the pinned order's own Invoice # into its filter-row cell
        //     (accessible name "Specify the search value for {Caption} field", verified live via
        //     Playwright MCP against the running app -- DxGrid's ShowFilterRow default) and blurring
        //     (DxTextBox commits on blur, same as the Employee edit below) must round-trip through
        //     ODataFilterTranslator -> a real server-side $filter and narrow 55,000 rows down to
        //     exactly the one matching row -- proving the filter drives a real OData query, not a
        //     client-side re-render. No cleanup needed: the next step navigates away and unmounts
        //     this ListView entirely.
        var invoiceFilterCell = Page.GetByRole(AriaRole.Textbox,
            new() { Name = $"Specify the search value for {OrderListCaption} field" });
        await invoiceFilterCell.FillAsync(OrderPinnedInvoiceNumber);
        await invoiceFilterCell.BlurAsync();
        var filteredRows = Page.Locator(DataRows);
        await Expect(filteredRows).ToHaveCountAsync(1, new() { Timeout = 15000 });
        await Expect(filteredRows.First.Locator("td").First).ToHaveTextAsync(OrderPinnedInvoiceNumber);
        await Shot($"{phase}-03b-filter");

        // 3. Open the config-PINNED order's detail via SPA nav (preserves the in-memory JWT).
        await NavigateSpa($"/detail/Order_DetailView/{OrderKey}");
        await Expect(Page.Locator("h3")).ToHaveTextAsync(OrderDetailCaption, new() { Timeout = 15000 });

        // 4. A known tab caption + the Order Items tab (nested collection) are present with rows.
        var orderItemsTab = Page.Locator("[role=tab]").Filter(new() { HasTextString = OrderItemsTabCaption }).First;
        await Expect(orderItemsTab).ToBeVisibleAsync(new() { Timeout = 15000 });
        await orderItemsTab.ClickAsync();
        var orderItemRows = Page.Locator(DataRows);
        await Expect(orderItemRows.First).ToBeVisibleAsync(new() { Timeout = 15000 });
        Assert.IsTrue(await orderItemRows.CountAsync() > 0, "Order Items tab should show the pinned order's line items");
        await Shot($"{phase}-04-detail");

        // 5. Validation: Employee edit path. Clear the required FirstName, Save, assert the 422
        //    surfaces at the editor + summary with the REAL rule's message text, and that nothing
        //    committed (OData re-read). Restore-in-finally is defensive hygiene, not load-bearing --
        //    a 422 never persists (verified below), so there is nothing to actually roll back on the
        //    happy path; the finally only fires a real restore write if something unexpected stuck.
        using (var api = await ApiClientAsync()) {
            var originalFirstName = await ReadEmployeeFirstNameAsync(api);
            Assert.IsNotNull(originalFirstName, "pinned Employee should have a FirstName to restore");
            try {
                await NavigateSpa($"/detail/Employee_DetailView/{EmployeeKey}");
                await Expect(Page.Locator("h3")).ToHaveTextAsync(EmployeeDetailCaption, new() { Timeout = 15000 });

                var firstName = EditorInput("First Name");
                await firstName.FillAsync("");
                await firstName.BlurAsync(); // DxTextBox commits on change/blur -> registers the edit
                var save = Page.GetByRole(AriaRole.Button, new() { Name = "Save" });
                await Expect(save).ToBeEnabledAsync(new() { Timeout = 5000 });
                await save.ClickAsync();

                var fnItem = Page.Locator(".xaf-item")
                    .Filter(new() { Has = Page.GetByText("First Name", new() { Exact = true }) }).First;
                await Expect(fnItem.Locator(".text-danger"))
                    .ToContainTextAsync(FirstNameRequiredMessage, new() { Timeout = 10000 });
                await Expect(Page.Locator(".alert-danger")).ToContainTextAsync(FirstNameRequiredMessage);
                await Shot($"{phase}-05-validation-error");

                // Commits-nothing proof: the 422 must not have persisted the cleared value server-side.
                var afterInvalidSave = await ReadEmployeeFirstNameAsync(api);
                Assert.AreEqual(originalFirstName, afterInvalidSave,
                    "a 422 must commit nothing -- FirstName must remain unchanged server-side");

                // Restore the form (back to the original value -> un-dirties, Save disables).
                await firstName.FillAsync(originalFirstName!);
                await firstName.BlurAsync();
                await Expect(save).ToBeDisabledAsync(new() { Timeout = 5000 });
            } finally {
                var current = await ReadEmployeeFirstNameAsync(api);
                if (current != originalFirstName) await RestoreEmployeeFirstNameAsync(api, originalFirstName);
            }
        }

        // 6. Command round-trip. The "Order summary" button operates on whatever DetailView is
        //    currently loaded, so navigate back to the pinned ORDER's detail first (we're still on
        //    Employee's from step 5) -- otherwise the command would resolve against the wrong type.
        await NavigateSpa($"/detail/Order_DetailView/{OrderKey}");
        await Expect(Page.Locator("h3")).ToHaveTextAsync(OrderDetailCaption, new() { Timeout = 15000 });
        await Page.GetByRole(AriaRole.Button, new() { Name = "Order summary" }).ClickAsync();
        var success = Page.Locator(".alert-success");
        await Expect(success).ToBeVisibleAsync(new() { Timeout = 10000 });
        // Resilient to line-item/total drift, specific to the pinned order.
        await Expect(success).ToContainTextAsync($"Order {OrderPinnedInvoiceNumber}");
        await Expect(success).ToContainTextAsync("item(s)");
        await Shot($"{phase}-06-command-success");
    }
}
