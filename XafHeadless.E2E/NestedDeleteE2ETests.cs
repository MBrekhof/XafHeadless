using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// CRUD-002 (delete half): an AGGREGATED child is owned by its master, so its nested grid offers Delete.
// Before this there was no way to remove a child from the client at all -- the nested tab was
// read/navigate only.
//
// The command is gated twice: the caller must opt in (LayoutNodeRenderer does so only when
// LayoutNode.Aggregated is true, because a shared collection needs Link/Unlink instead -- deleting there
// would destroy an object other records reference), and the server must have projected Allow.Delete,
// which is model AND security.
//
// This test never touches demo data: it picks an order with ZERO items, creates exactly one child of its
// own, and deletes that. The finally block removes the child if the UI did not.
[TestClass]
public class NestedDeleteE2ETests : PlaywrightFixture {
    static async Task<string> CreateAsync(HttpClient http, string type, string json) {
        var resp = await http.PostAsync($"api/save/{type}", new StringContent(json, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("key").GetString()!;
    }

    [TestMethod]
    public async Task DeletingAnAggregatedChild_RemovesItFromTheNestedGrid() {
        using var http = await ApiClientAsync();

        // Build the whole fixture rather than borrowing demo rows. The first attempt looked for an Order
        // with no items and found none in 40 -- and deleting a child of a real order to make room would
        // have destroyed demo data to test a delete. An order of our own costs one extra call and leaves
        // the database exactly as it was found.
        var orderKey = await CreateAsync(http, "Order", "{}");
        var childKey = await CreateAsync(http, "OrderItem", $$"""{"Order":"{{orderKey}}"}""");

        try {
            await LoginAsync();
            await NavigateSpa($"/detail/Order_DetailView/{orderKey}");
            await Expect(Page).ToHaveURLAsync(
                new Regex(@"/detail/Order_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 20000 });

            // Count the DELETE COMMANDS, not <tr> elements. An empty DevExpress grid still renders a
            // placeholder row ("No data to display") whose class is not .dxbl-grid-empty-row, so counting
            // rows reports 1 for an empty grid -- which is exactly how an earlier version of this test
            // failed against working code. One command per data row is the semantic question anyway:
            // "is there still a child here to delete?"
            var deleteCommands = Page.Locator(".xaf-nested-list").GetByRole(AriaRole.Button,
                new() { NameRegex = new Regex("^delete$", RegexOptions.IgnoreCase) });

            // The command exists only because this collection is aggregated AND the server allowed delete.
            await Expect(deleteCommands).ToHaveCountAsync(1, new() { Timeout = 20000 });
            await deleteCommands.First.ClickAsync();

            // MUST still be on the master. The first version of this test asserted only "0 nested rows"
            // and passed while being completely wrong: the delete click also selected the row, so the app
            // navigated to the child's DetailView and rendered "No OrderItem found with key ...". There
            // are no nested rows on that page either, so the count assertion was satisfied by having left
            // the page. Pin the URL first, then count.
            await Expect(Page).ToHaveURLAsync(
                new Regex(@"/detail/Order_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 10000 });

            // The grid reloads through its normal load path, so an emptied collection has nothing to delete.
            await Expect(deleteCommands).ToHaveCountAsync(0, new() { Timeout = 20000 });
            await Shot("crud002-01-child-deleted");

            // And it is gone on the SERVER, not just off the screen -- a grid that merely stopped showing
            // the row would pass the assertion above and be wrong.
            var check = await http.GetAsync(
                $"api/odata/OrderItem?$filter=Order/ID eq {orderKey}&$count=true&$top=0");
            check.EnsureSuccessStatusCode();
            using var checkDoc = JsonDocument.Parse(await check.Content.ReadAsStringAsync());
            Assert.AreEqual(0, checkDoc.RootElement.GetProperty("@odata.count").GetInt32(),
                "the child must be deleted on the server, not just hidden from the grid");
        }
        finally {
            // The child delete is a no-op when the UI already did it -- it matters only if the test failed
            // part-way. The order is ours either way and must not be left behind.
            await http.DeleteAsync($"api/save/OrderItem/{childKey}");
            await http.DeleteAsync($"api/save/Order/{orderKey}");
        }
    }
}
