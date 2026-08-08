using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// BUG-008: a lookup column renders the value at the end of its DISPLAY PATH, and that path may take more
// than one hop. Order_ListView's Store is a lookup on CustomerStore, whose default property is Emblem --
// an ENTITY, not a scalar (`[XafDefaultProperty(nameof(Emblem))]`, HasOne/WithMany). Stopping at one hop
// asked for $expand=Store($select=Emblem), got a nav object back, and rendered a permanently blank cell.
// The projector now walks to a primitive (Emblem's own default property is CityName, a string) and sends
// "Emblem.CityName", so the column shows text -- and, because the path now lands on something $orderby can
// evaluate, GRID-005's ceiling stops refusing it and the column sorts again.
//
// This file replaces LookupSortCeilingE2ETests, which asserted the OPPOSITE for the same column. That test
// was correct when written and its premise is now gone: after BUG-008 every lookup projected by this model
// resolves to a string (verified across all 7 navigable views), so there is no longer a live example of an
// unresolvable display path to drive the ceiling from a browser. GRID-005's predicate keeps its three unit
// tests in GridBindingTests; what is no longer covered end-to-end is the AllowSort=false BINDING itself.
// Recreating that needs a dev-only fixture type whose default property cannot resolve (a cycle or a blob),
// in the shape of the existing host-owned LookupProbe -- noted on the card rather than left implied.
[TestClass]
public class LookupDisplayPathE2ETests : PlaywrightFixture {
    const string DataRows = ".dxbl-grid-table tbody tr:not(.dxbl-grid-empty-row)";
    const int PageSize = 25;
    const string ViewId = "Order_ListView";

    // GAP-008 persists layout per user, so sort state survives the run: start clean, restore in finally,
    // or this test leaves Order_ListView sorted for DateFilterE2ETests, whose baseline assumes the default
    // order.
    static async Task ClearPersistedLayoutAsync() {
        using var http = await ApiClientAsync();
        (await http.PutAsync($"api/prefs/{ViewId}", new StringContent("{}", Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task OrderServerMode_TwoHopLookupColumn_RendersTextAndSorts() {
        await ClearPersistedLayoutAsync();
        try {
            await LoginAsync();
            await NavigateSpa($"/list/{ViewId}");
            await Expect(Page).ToHaveURLAsync(new Regex($@"/list/{ViewId}$"), new() { Timeout = 15000 });
            var rows = Page.Locator(DataRows);
            await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
            await Expect(rows.First).ToContainTextAsync(new Regex(@"\S"), new() { Timeout = 15000 });

            // Find Store by caption, then read the SAME column index out of the body -- captions are
            // uppercased by the theme, so match case-insensitively.
            var headers = Page.Locator(".dxbl-grid-table thead tr").First.Locator("th");
            var headerCount = await headers.CountAsync();
            var storeIndex = -1;
            for (var i = 0; i < headerCount; i++) {
                var caption = (await headers.Nth(i).InnerTextAsync()).Trim();
                if (string.Equals(caption, "Store", StringComparison.OrdinalIgnoreCase)) { storeIndex = i; break; }
            }
            Assert.IsTrue(storeIndex >= 0, "could not locate the Store column header -- the locator, not the app, is wrong");

            // THE defect: this cell was empty for every row, on every load.
            var storeCell = rows.First.Locator("td").Nth(storeIndex);
            var cellText = (await storeCell.InnerTextAsync()).Trim();
            Assert.IsFalse(string.IsNullOrEmpty(cellText),
                "the Store cell must render the value at the end of its display path, not an entity");
            await Shot("bug008-01-store-renders-text");

            // And the resolved path is orderable, so the column sorts. Two clicks: the view already comes
            // back ordered by InvoiceNumber, and the first click on Store is ascending -- compare the two
            // click results to each other rather than to the baseline.
            var storeHeader = headers.Nth(storeIndex);
            await storeHeader.ClickAsync();
            await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
            await Expect(rows.First).ToContainTextAsync(new Regex(@"\S"), new() { Timeout = 15000 });
            var ascending = await rows.First.InnerTextAsync();

            await storeHeader.ClickAsync();
            await Expect(rows).ToHaveCountAsync(PageSize, new() { Timeout = 15000 });
            await Expect(rows.First).ToContainTextAsync(new Regex(@"\S"), new() { Timeout = 15000 });
            var descending = await rows.First.InnerTextAsync();

            Assert.AreNotEqual(ascending, descending,
                "sorting by a resolved lookup path must reorder the grid -- before BUG-008 this sort was refused outright");
            await Shot("bug008-02-store-sorts");
        }
        finally {
            await ClearPersistedLayoutAsync();
        }
    }
}
