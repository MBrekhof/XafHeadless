using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// GRID-005: a lookup column whose DISPLAY member is not a primitive can never be sorted server-side.
// Order_ListView's Store column displays CustomerStore.Emblem, and Emblem is a REFERENCE to the Emblem
// entity (CustomerStore carries [XafDefaultProperty(nameof(Emblem))]; HasOne/WithMany in the demo's
// DbContext) -- so $orderby=Store/Emblem asks OData to order by a navigation property and earns a
// guaranteed 400, "The $orderby expression must evaluate to a single value of primitive type".
//
// BUG-005 could only strip that shaping AFTER the click, once the view had already failed and the
// persisted layout had already been poisoned. The projected display-member type (LookupMetadata
// .DisplayDataType) now lets the column refuse the sort up front, so the request is never built.
//
// Asserted on ROW ORDER, not on the network and not on header chrome. In Server render mode the OData
// calls are issued by the Blazor circuit SERVER-side, so Playwright's Page.Request never sees them -- a
// network assertion here silently passes for the wrong reason (found live: the positive control saw
// zero requests while the Api log showed plenty). Sort glyph classes are the mis-locatable chrome
// TEST-002 warns about. The locator counts below still fail loud if a caption stops matching exactly
// one header.
[TestClass]
public class LookupSortCeilingE2ETests : PlaywrightFixture {
    const string DataRows = ".dxbl-grid-table tbody tr:not(.dxbl-grid-empty-row)";
    const int PageSize = 25;             // XafListView's DxGrid PageSize
    const string ViewId = "Order_ListView";

    // GAP-008 persists this view's layout per user, so sort state SURVIVES the test run. That makes a
    // toggle-based assertion stateful (an even number of clicks lands back where it started -- observed
    // live) and, worse, would leave Order_ListView sorted for DateFilterE2ETests, whose baseline
    // assumes the default order. Start clean and restore in finally.
    static async Task ClearPersistedLayoutAsync() {
        using var http = await ApiClientAsync();
        (await http.PutAsync($"api/prefs/{ViewId}", new StringContent("{}", Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task OrderServerMode_LookupWithNonPrimitiveDisplayMember_RefusesTheSortUpFront() {
        await ClearPersistedLayoutAsync();
        try {
            await LoginAsync();
            await NavigateSpa($"/list/{ViewId}");
            await Expect(Page).ToHaveURLAsync(new Regex($@"/list/{ViewId}$"), new() { Timeout = 15000 });
            var rows = Page.Locator(DataRows);
            await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });

            // The Modernist theme uppercases captions via text-transform and innerText returns the
            // TRANSFORMED text (the lesson DateFilterE2ETests learned) -- so match case-insensitively.
            var headers = Page.Locator(".dxbl-grid-table thead th");
            var store = headers.Filter(new() { HasTextRegex = new Regex(@"^\s*store\s*$", RegexOptions.IgnoreCase) });
            await Expect(store).ToHaveCountAsync(1, new() { Timeout = 10000 });

            // A row mid-render reads as "" (same trap DateFilterE2ETests documents), so wait for real
            // text before sampling -- otherwise the baseline is empty and every later compare lies.
            await Expect(rows.First).ToContainTextAsync(new Regex(@"\S"), new() { Timeout = 15000 });
            var baseline = await rows.First.InnerTextAsync();

            // Before GRID-005 this click sent $orderby=Store/Emblem, earned a 400, and (BUG-005)
            // persisted the failing sort so every later load replayed it and rendered nothing.
            await store.ClickAsync();
            await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
            Assert.AreEqual(baseline, await rows.First.InnerTextAsync(),
                "clicking a lookup whose display member is not a primitive must not reorder the grid -- the sort has to be refused up front");
            await Shot("grid005-01-store-sort-refused");

            // Positive control: an ordinary scalar column must STILL sort, or this test would also pass
            // against a grid that had simply stopped sorting altogether. Compare the two click results
            // to EACH OTHER (ascending vs descending) rather than to the baseline: the server already
            // returns this view ordered by InvoiceNumber ascending, so the first click reproduces the
            // order that was already on screen and changes nothing visible.
            var invoice = headers.Filter(new() { HasTextRegex = new Regex("invoice", RegexOptions.IgnoreCase) });
            await Expect(invoice).ToHaveCountAsync(1, new() { Timeout = 10000 });

            await invoice.ClickAsync();
            await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
            await Expect(rows.First).ToContainTextAsync(new Regex(@"\S"), new() { Timeout = 15000 });
            var ascending = await rows.First.InnerTextAsync();

            await invoice.ClickAsync();
            await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
            await Expect(rows.First).ToContainTextAsync(new Regex(@"\S"), new() { Timeout = 15000 });
            var descending = await rows.First.InnerTextAsync();

            Assert.AreNotEqual(ascending, descending,
                "a sortable scalar column must still reorder the grid -- otherwise the ceiling is too wide");
            await Shot("grid005-02-scalar-sort-still-works");
        }
        finally {
            await ClearPersistedLayoutAsync();
        }
    }
}
