using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// BUG-009: clicking a row in a nested tab navigated to a DetailView that does not exist.
//
// LayoutNodeRenderer derived the target by swapping "_ListView" -> "_DetailView" on the NESTED view id.
// That is right for a top-level list (Order_ListView -> Order_DetailView) and wrong for a nested one: a
// nested view is named {Master}_{Collection}_ListView, so Order_OrderItems_ListView became
// Order_OrderItems_DetailView -- a 404 -- while the child's real view is OrderItem_DetailView.
//
// Every nested tab was affected. Nothing caught it because the tab itself renders fine, the click is the
// last step, and the failure surfaces as the DetailView's "failed to load" state rather than an exception.
// No test clicked a nested row. This one does.
[TestClass]
public class NestedRowNavigationE2ETests : PlaywrightFixture {
    const string NestedRows = ".xaf-nested-list .dxbl-grid-table tbody tr:not(.dxbl-grid-empty-row)";

    // An Order that actually HAS items -- an empty nested grid proves nothing about the click.
    static async Task<string> OrderKeyWithItemsAsync() {
        using var http = await ApiClientAsync();
        var resp = await http.GetAsync("api/odata/Order?$top=20&$select=ID&$expand=OrderItems($select=ID;$top=1)");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var row in doc.RootElement.GetProperty("value").EnumerateArray())
            if (row.TryGetProperty("OrderItems", out var items)
                && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
                return row.GetProperty("ID").GetString()!;
        Assert.Fail("no Order in the first 20 has an OrderItem -- cannot prove anything about the click");
        return "";
    }

    [TestMethod]
    public async Task ClickingANestedRow_OpensTheChildsRealDetailView() {
        var key = await OrderKeyWithItemsAsync();

        await LoginAsync();
        await NavigateSpa($"/detail/Order_DetailView/{key}");
        await Expect(Page).ToHaveURLAsync(
            new Regex(@"/detail/Order_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 20000 });

        var rows = Page.Locator(NestedRows);
        await Expect(rows.First).ToContainTextAsync(new Regex(@"\S"), new() { Timeout = 20000 });
        await rows.First.ClickAsync();

        // The child's detail view is named after its TYPE. Before the fix this landed on
        // /detail/Order_OrderItems_DetailView/... and the page could only report a metadata failure.
        await Expect(Page).ToHaveURLAsync(
            new Regex(@"/detail/OrderItem_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 20000 });

        // ...and it must actually RENDER. Asserting only the URL would still pass against a 404 view,
        // which is the failure mode this test exists for.
        await Expect(Page.GetByText(new Regex("Failed to load", RegexOptions.IgnoreCase)))
            .ToHaveCountAsync(0, new() { Timeout = 10000 });
        await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^save$", RegexOptions.IgnoreCase) }))
            .ToBeVisibleAsync(new() { Timeout = 20000 });
        await Shot("bug009-01-nested-row-opens-child-detail");
    }
}
