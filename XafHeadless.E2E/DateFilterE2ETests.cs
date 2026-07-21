using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// GRID-004 (companion-headless backport): date-column filtering through the REAL DxGrid filter row, in both
// binding modes. Before the fix, date cells materialized as raw ISO strings, DxGrid sniffed the
// column as text, and the filter row emitted string criteria (contains/eq 'text') the server 400s
// against Edm.DateTimeOffset -- the unhandled 400 killed the whole Blazor circuit. With typed
// DateTime materialization + the explicit metadata-driven DxDateEdit filter cell, a date entry
// becomes a [day, next-day) range: translated to UTC-instant $filter literals in server mode,
// evaluated client-side against DateTime cells in-memory.
[TestClass]
public class DateFilterE2ETests : PlaywrightFixture {
    const string DataRows = ".dxbl-grid-table tbody tr:not(.dxbl-grid-empty-row)";
    const int PageSize = 25;   // XafListView's DxGrid PageSize

    // ---- SERVER MODE: Order (55k rows > RowCap). The demo data has NO day under one page
    // (probed live: 35-67 orders/day) AND the default order clusters same-day rows, so page 1 shows
    // exactly PageSize single-day rows both unfiltered (the OLDEST day, 2023) and filtered -- neither
    // row counts nor distinct-day counts can disambiguate. The discriminator is the day VALUE:
    // filtering to the NEWEST day (3 years later) must flip every visible OrderDate cell to a text
    // absent from the baseline page, and clearing must restore the baseline texts. Format-agnostic
    // (no culture assumption); day-boundary CORRECTNESS is covered by the unit suite and was
    // count-proven live in the companion headless implementation -- this test pins the wire round trip and the circuit
    // surviving it (the old behavior was a 400 that killed the whole circuit).
    [TestMethod]
    public async Task OrderServerMode_DateFilterRow_SwitchesPageToTheEnteredDay() {
        Page.Console += (_, msg) => Console.WriteLine($"[console:{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => Console.WriteLine($"[pageerror] {err}");
        var day = await NewestDayAsync("Order", "OrderDate");

        await LoginAsync();
        await NavigateSpa("/list/Order_ListView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Order_ListView$"), new() { Timeout = 15000 });
        var rows = Page.Locator(DataRows);
        await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });

        // Baseline: settle = all cells non-empty (a row mid-render reads as "" -- observed live).
        var baseline = await WaitForCellsAsync(cells => cells.Count == PageSize && cells.All(t => t.Length > 0));
        Assert.IsTrue(baseline.Count == PageSize && baseline.All(t => t.Length > 0),
            "unfiltered page 1 never settled");
        await Shot("date-filter-01-order-unfiltered");

        // ISO yyyy-MM-dd parses under any app culture, so the test needn't know the display culture.
        var filterCell = Page.Locator(".xaf-date-filter[data-field='OrderDate'] input");
        await filterCell.FillAsync(day.ToString("yyyy-MM-dd"));
        await filterCell.BlurAsync();

        // Filtered: one non-empty day text that did NOT appear on the baseline page. The row COUNT
        // stays PageSize through the refilter (25 -> 25), so only the cell contents signal completion.
        await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
        var filtered = await WaitForCellsAsync(cells =>
            cells.Count == PageSize && cells.All(t => t.Length > 0)
            && cells.Distinct().Count() == 1 && !baseline.Contains(cells[0]));
        Assert.AreEqual(1, filtered.Distinct().Count(),
            $"filtered page must show a single OrderDate day, got [{string.Join(" | ", filtered.Distinct())}]");
        Assert.IsFalse(baseline.Contains(filtered[0]),
            $"filtered day text '{filtered[0]}' already appeared on the baseline page -- cannot prove the filter applied");
        // The editor must still SHOW the committed day (the ExtractDayFromCriteria re-render round trip).
        await Expect(filterCell).Not.ToHaveValueAsync("", new() { Timeout = 5000 });
        await Shot("date-filter-02-order-filtered");

        // Cleared: the baseline page (and its day texts) come back.
        await filterCell.FillAsync("");
        await filterCell.BlurAsync();
        await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
        var cleared = await WaitForCellsAsync(cells => cells.SequenceEqual(baseline));
        Assert.IsTrue(cleared.SequenceEqual(baseline),
            $"cleared filter must restore the unfiltered baseline page (got [{string.Join(" | ", cleared.Distinct())}], baseline [{string.Join(" | ", baseline.Distinct())}])");
    }

    // ---- IN-MEMORY: Employee (51 rows <= RowCap, pages at 25). DxGrid evaluates the day-range
    // criteria client-side against the now-DateTime HireDate cells; expected counts bucket the wire
    // values by their own wall day (no zone conversion -- wall vs wall).
    [TestMethod]
    public async Task EmployeeInMemory_DateFilterRow_FiltersToTheEnteredDay() {
        var (day, expected) = await PickSmallDayAsync("Employee", "HireDate", max: PageSize - 1);

        await LoginAsync();
        // LoginAsync lands on the model-driven first nav item (Employee_ListView) -- but assert the
        // route explicitly rather than assuming the landing view.
        await NavigateSpa("/list/Employee_ListView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/Employee_ListView$"), new() { Timeout = 15000 });
        var rows = Page.Locator(DataRows);
        await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });   // page 1 of 51

        var filterCell = Page.Locator(".xaf-date-filter[data-field='HireDate'] input");
        await filterCell.FillAsync(day.ToString("yyyy-MM-dd"));
        await filterCell.BlurAsync();
        await Expect(rows).ToHaveCountAsync(expected, new() { Timeout = 15000 });
        await Shot("date-filter-03-employee-inmemory-filtered");

        await filterCell.FillAsync("");
        await filterCell.BlurAsync();
        await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
    }

    // Reads the visible rows' OrderDate cell texts; the column INDEX is resolved from the header
    // captions so column-order/model changes don't silently shift the assertion to a wrong column.
    async Task<List<string>> OrderDateCellsAsync() {
        var headers = await Page.Locator(".dxbl-grid-table thead th").AllInnerTextsAsync();
        var idx = headers.Select((h, i) => (h, i)).First(x => x.h.Trim() == "Order Date").i;
        var texts = new List<string>();
        var rows = Page.Locator(DataRows);
        var count = await rows.CountAsync();
        for (var r = 0; r < count; r++)
            texts.Add((await rows.Nth(r).Locator("td").Nth(idx).InnerTextAsync()).Trim());
        return texts;
    }

    // Polls the OrderDate cells until `settled` holds. Returns the last observation either way;
    // the caller's asserts name the failure.
    async Task<List<string>> WaitForCellsAsync(Func<List<string>, bool> settled) {
        List<string> cells = [];
        for (var i = 0; i < 60; i++) {
            cells = await OrderDateCellsAsync();
            if (cells.Count > 0 && settled(cells)) return cells;
            await Task.Delay(250);
        }
        return cells;
    }

    static async Task<DateTime> NewestDayAsync(string entity, string member) {
        using var api = await ApiClientAsync();
        var resp = await api.GetAsync(
            $"api/odata/{entity}?$orderby={member} desc&$top=1&$select={member}&$filter={member} ne null");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return DateTimeOffset.Parse(
            doc.RootElement.GetProperty("value")[0].GetProperty(member).GetString()!).DateTime.Date;
    }

    // Samples the newest 500 values of {member}, buckets them into WALL days (what the grid displays
    // and the user filters by), and returns the newest day whose row count is 1..max. Used by the
    // in-memory test (client-side evaluation is wall-vs-wall over the full small set). Computed
    // live so no data value is pinned against the demo DB.
    static async Task<(DateTime Day, int Count)> PickSmallDayAsync(string entity, string member, int max) {
        using var api = await ApiClientAsync();
        var resp = await api.GetAsync(
            $"api/odata/{entity}?$orderby={member} desc&$top=500&$select={member}&$filter={member} ne null");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var days = doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(r => DateTimeOffset.Parse(r.GetProperty(member).GetString()!).DateTime.Date)
            .GroupBy(d => d).OrderByDescending(g => g.Key);
        foreach (var g in days.Where(g => g.Count() <= max))
            return (g.Key, g.Count());
        Assert.Inconclusive($"no {entity}.{member} day with 1..{max} rows in the sample window");
        return default;   // unreachable
    }
}
