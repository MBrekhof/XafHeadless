using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace XafHeadless.E2E;

// Shared base for the E2E smoke suite. Derives from Microsoft.Playwright.MSTest's PageTest, which
// owns the browser/context/Page lifecycle per test method (fresh, isolated context each test method
// -- which is exactly why the dual-render-mode proof lives in ONE test method: Phase A and Phase B
// must share the SAME Page/BrowserContext, see SmokeTests.cs).
//
// Task 4 (26.1 migration): retargeted from the original POC's WASM-standalone client (:5210,
// the POC's list view, in-memory JWT dropped by any hard reload) to the Blazor Web App (:5220,
// Order/Employee views, demo fixtures). Two hard constraints carry over:
//  1. Blazor gotchas: NavigationTimeout = 10000, WaitUntil = DOMContentLoaded (see brief).
//  2. AuthState is Scoped per render context -- so a fresh circuit/runtime starts with Token == null.
//     GAP-007 fixed the UX of that: the JWT is now persisted to sessionStorage and restored on the
//     first interactive render, so a hard Page.GotoAsync (fresh navigation / the WASM takeover) keeps
//     the session instead of bouncing to /login. In-page navigation still uses NavigateSpa() (injects
//     an anchor and clicks it -- Blazor's Router intercepts same-origin anchor clicks ->
//     history.pushState, no reload) both because it's how the app navigates and because it exercises
//     the client-side path; a hard Page.GotoAsync is used for the deliberate WASM-takeover trigger
//     (TryTriggerWasmTakeoverAsync), after which the persisted session is expected to SURVIVE
//     (GAP-007) rather than require a re-login.
public abstract class PlaywrightFixture : PageTest {
    protected static readonly IConfiguration Config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("testsettings.json")
        .AddJsonFile("testsettings.Development.json", optional: true)
        .Build();

    protected static string ClientBaseUrl => Config["Client:BaseUrl"] ?? "http://localhost:5220";
    protected static string ApiBaseUrl => Config["Api:BaseUrl"] ?? "http://localhost:5200";
    protected static string AdminUser => Config["Test:AdminUser"] ?? "Admin@company1.com";
    protected static string AdminPassword => Config["Test:AdminPassword"] ?? "";

    // Config-pinned demo records (public demo data -- committable, per the plan's Global Constraints).
    // Discovered ONCE via a live, deterministically-ordered OData probe (never an arbitrary $top=1 --
    // that pattern isn't stable across query-plan changes); documented here so a reseed can rediscover
    // them the same way.
    //   Order:    GET api/odata/Order?$top=1&$orderby=InvoiceNumber asc     -> InvoiceNumber "0000001"
    //             (the same Order Task 3's manual smoke exercised: 3 OrderItems, total GBP 55,500.00).
    //   Employee: GET api/odata/Employee?$top=1&$orderby=LastName asc      -> "James Anderson"
    // If these keys ever 404 (demo DB dropped + reseeded -- see the recovery note in SmokeTests.cs),
    // rerun the same two probes and update testsettings.json; the GUID values differ per seed, the
    // ORDER/SELECTION method (lowest InvoiceNumber / alphabetically-first Employee) stays the recipe.
    protected static string OrderKey => Config["Test:OrderKey"] ?? "771e968e-556e-48b7-955b-fe8cf6176477";
    protected static string EmployeeKey => Config["Test:EmployeeKey"] ?? "0ba7323e-5c81-4675-a922-2dfe3ce545f5";

    // GAP-004: post-login landing is now model-driven (the FIRST item GET api/model/navigation
    // returns), not the old hardcoded /list/Order_ListView. Discovered live (Admin@company1.com):
    // the demo nav tree orders Employee/Evaluation before Sales (Customer/Order/Product), so
    // Employee_ListView is first -- see KnownModel.NavigationFirstItemViewId in XafHeadless.Api.Tests
    // (same discovery, independently recorded here since E2E has no ProjectReference to that project).
    protected const string FirstNavViewId = "Employee_ListView";

    // RenderModeBadge.razor's stable hook (Task 3) -- the Auto-mode probe both phases assert on.
    protected const string BadgeSelector = "[data-testid=render-mode-badge]";

    static readonly string ShotDir =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "screenshots");

    public override BrowserNewContextOptions ContextOptions() => new() {
        ViewportSize = new ViewportSize { Width = 1440, Height = 1024 },
        IgnoreHTTPSErrors = true,
    };

    protected async Task Shot(string name) {
        Directory.CreateDirectory(ShotDir);
        await Page.ScreenshotAsync(new PageScreenshotOptions {
            Path = Path.Combine(ShotDir, name + ".png"), FullPage = true
        });
    }

    protected ILocator BadgeLocator => Page.Locator(BadgeSelector);

    // SPA login. Lands on /login (redirect when unauthenticated), submits the form; on success the
    // app navigates (in-Blazor NavigateTo, no reload) to the FIRST projected nav item's route (GAP-004
    // -- model-driven, FirstNavViewId above) with the JWT held in the current render context's
    // AuthState. Callers that need a SPECIFIC view (e.g. the Order-data-rich smoke path) navigate there
    // explicitly afterward via NavigateSpa -- this helper only proves login + the landing redirect.
    protected async Task LoginAsync() {
        Page.SetDefaultNavigationTimeout(10000);
        Page.SetDefaultTimeout(15000);
        await Page.GotoAsync(ClientBaseUrl + "/",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        // The first paint is static-prerendered HTML; Blazor hasn't attached its event handlers yet.
        // Clicking Login before the render mode actually hydrates would be a silent no-op (button looks
        // clickable but nothing is listening). Wait for the badge to leave "Static" first.
        await Expect(BadgeLocator).Not.ToContainTextAsync("Static", new() { Timeout = 15000 });
        await Page.GetByPlaceholder("User name").FillAsync(AdminUser);
        await Page.GetByPlaceholder("Password").FillAsync(AdminPassword);
        // DxTextBox commits its @bind-Text on change/blur, NOT on Playwright's synthetic 'input' event
        // (same commit-on-blur behaviour the Employee edit relies on below). Blur the password so
        // OnLoginClick reads the typed credentials instead of a still-empty field -- otherwise it POSTs
        // empty creds, the API rejects them, "Login failed" renders and we never leave /login.
        await Page.GetByPlaceholder("Password").BlurAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Login" }).ClickAsync();
        // Blazor's client-side route change (NavigationManager.NavigateTo, no forceLoad) is a
        // same-document navigation -- it never fires a "load" event, which is what Page.WaitForURLAsync
        // waits on by default and why it flaked here. Expect(Page).ToHaveURLAsync polls page.url()
        // instead, which is the documented-correct way to assert SPA navigations in Playwright.
        await Expect(Page).ToHaveURLAsync(new Regex($"/list/{FirstNavViewId}$"), new() { Timeout = 15000 });
    }

    // Client-side navigation that PRESERVES the in-memory JWT (see class remarks).
    protected async Task NavigateSpa(string relativeUrl) {
        await Page.EvaluateAsync(@"(url) => {
            let a = document.getElementById('__e2e_nav');
            if (!a) { a = document.createElement('a'); a.id = '__e2e_nav'; document.body.appendChild(a); }
            a.setAttribute('href', url);
            a.textContent = 'e2e-nav';
        }", relativeUrl);
        await Page.ClickAsync("#__e2e_nav");
    }

    // The Auto-mode proof (Task 4's point): a fresh navigation, in the SAME browser context/profile,
    // after the WASM runtime has already been downloaded once. Blazor Auto downloads the WASM runtime +
    // app bundle in the background during the Server-rendered session; once complete, it records that
    // fact in `localStorage["blazor-resource-hash:{WasmAssemblyName}"]` (confirmed live, Task 4
    // exploration -- devtools inspection of the actual key, not the generic docs' "Cache Storage"
    // description, which this app does NOT use: Cache Storage stayed empty throughout while this
    // localStorage key was present). A subsequent fresh page load reads that key to decide whether to
    // boot straight into WebAssembly instead of Server.
    //
    // IMPORTANT #1 (found during Task 4 exploration): a hard reload CANCELS any in-flight background
    // fetch, so naively reload-polling every N seconds can starve the download instead of triggering
    // it -- every attempt restarts the same not-yet-finished transfer from byte zero, and it never
    // converges. So stage 1 polls the localStorage key IN PLACE (no navigation, nothing to cancel)
    // until the resource hash is recorded.
    //
    // IMPORTANT #2 (also found live): the hash being present is necessary but not sufficient to see
    // "WebAssembly" in the badge on the very next check -- every fresh load (even once cached) still
    // paints the same static-prerendered placeholder first (badge briefly reads "Static"), and the
    // actual WASM runtime boot (a real .NET runtime initializing in the browser) takes a couple of
    // real seconds even from cache -- longer than Server's near-instant interactive attach. So each
    // reload attempt waits for the badge to leave "Static" (same gate LoginAsync uses) before reading
    // its final value. Both stages are bounded polls, not fixed sleeps.
    protected async Task<bool> TryTriggerWasmTakeoverAsync(int maxHashPolls = 40, int hashPollDelayMs = 3000,
        int maxReloadAttempts = 3, int reloadDelayMs = 1500) {
        for (var i = 0; i < maxHashPolls; i++) {
            var hasHash = await Page.EvaluateAsync<bool>(
                "() => Object.keys(localStorage).some(k => k.startsWith('blazor-resource-hash:'))");
            if (hasHash) break;
            if (i < maxHashPolls - 1) await Page.WaitForTimeoutAsync(hashPollDelayMs); // bounded poll
        }

        for (var attempt = 1; attempt <= maxReloadAttempts; attempt++) {
            await Page.GotoAsync(ClientBaseUrl + "/login",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            try {
                await Expect(BadgeLocator).Not.ToContainTextAsync("Static", new() { Timeout = 10000 });
            } catch (PlaywrightException) {
                // Badge stuck on "Static" for this attempt -- count it as a failed attempt (not a hard
                // failure) so the Assert.Inconclusive branch in SmokeTests stays reachable if every
                // attempt sticks, instead of this throwing raw past the caller.
                if (attempt < maxReloadAttempts) await Page.WaitForTimeoutAsync(reloadDelayMs); // bounded poll
                continue;
            }
            if ((await BadgeLocator.InnerTextAsync()).Contains("WebAssembly")) return true;
            if (attempt < maxReloadAttempts) await Page.WaitForTimeoutAsync(reloadDelayMs); // bounded poll
        }
        return false;
    }

    // --- Direct API helpers (used by the Employee-edit restore-in-finally safety net) ---

    protected static async Task<HttpClient> ApiClientAsync() {
        var http = new HttpClient { BaseAddress = new Uri(ApiBaseUrl) };
        var resp = await http.PostAsJsonAsync("api/Authentication/Authenticate",
            new { userName = AdminUser, password = AdminPassword });
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadAsStringAsync()).Trim('"');
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    // Reads the pinned Employee's FirstName straight off OData (Employee.ID is a Guid key -- direct
    // key-predicate syntax, no $filter needed).
    protected static async Task<string?> ReadEmployeeFirstNameAsync(HttpClient http) {
        var resp = await http.GetAsync($"api/odata/Employee({EmployeeKey})?$select=FirstName");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("FirstName", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
    }

    // Robust restore: writes FirstName back through the validating save endpoint (200 expected).
    protected static async Task RestoreEmployeeFirstNameAsync(HttpClient http, string? value) {
        var resp = await http.PostAsJsonAsync($"api/save/Employee/{EmployeeKey}",
            new Dictionary<string, object?> { ["FirstName"] = value });
        resp.EnsureSuccessStatusCode();
    }
}
