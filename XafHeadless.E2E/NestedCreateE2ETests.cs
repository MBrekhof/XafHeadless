using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// CRUD-002 (new half): creating a child FROM its master's nested grid, associated with that master.
//
// The association is the whole point. /new/{ViewId} says which view to open, not what the new object
// belongs to, so the master rides along as a query string that XafDetailView seeds into `changes` -- it is
// precisely a pending change this create will write. Without it the form would produce an orphan the user
// must re-parent by hand, which for a composite child is meaningless: its identity IS "belongs to this
// master".
//
// A full form rather than an instant blank row, deliberately: a child type with required members would
// simply 422 on a blank create, so "add a row then fill it in" only works for types that happen to have no
// validation rules.
//
// Like the delete test, this builds its own Order rather than borrowing a demo row, and removes everything
// it created.
[TestClass]
public class NestedCreateE2ETests : PlaywrightFixture {
    static async Task<string> CreateAsync(HttpClient http, string type, string json) {
        var resp = await http.PostAsync($"api/save/{type}", new StringContent(json, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("key").GetString()!;
    }

    static async Task<int> ChildCountAsync(HttpClient http, string orderKey) {
        var resp = await http.GetAsync($"api/odata/OrderItem?$filter=Order/ID eq {orderKey}&$count=true&$top=0");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("@odata.count").GetInt32();
    }

    [TestMethod]
    public async Task CreatingFromANestedGrid_AssociatesTheChildWithItsMaster() {
        using var http = await ApiClientAsync();
        var orderKey = await CreateAsync(http, "Order", "{}");

        try {
            Assert.AreEqual(0, await ChildCountAsync(http, orderKey), "a freshly created Order starts empty");

            await LoginAsync();
            await NavigateSpa($"/detail/Order_DetailView/{orderKey}");
            await Expect(Page).ToHaveURLAsync(
                new Regex(@"/detail/Order_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 20000 });

            // New appears on the nested grid only because the collection is AGGREGATED and the server
            // projected Allow.New. Scope to the nested list so the master's own toolbar cannot satisfy it.
            var newCommand = Page.Locator(".xaf-nested-list").GetByRole(AriaRole.Button,
                new() { NameRegex = new Regex("^new$", RegexOptions.IgnoreCase) });
            await Expect(newCommand).ToHaveCountAsync(1, new() { Timeout = 20000 });
            await newCommand.First.ClickAsync();

            // The child's OWN create form, carrying the master. BUG-009's projected DetailViewId is what
            // makes this the right view rather than a derived-and-nonexistent one.
            await Expect(Page).ToHaveURLAsync(
                new Regex(@"/new/OrderItem_DetailView\?masterMember=Order&masterKey=" + Regex.Escape(orderKey)),
                new() { Timeout = 20000 });

            var save = Page.GetByRole(AriaRole.Button,
                new() { NameRegex = new Regex("^save$", RegexOptions.IgnoreCase) }).First;
            await Expect(save).ToBeEnabledAsync(new() { Timeout = 20000 });
            await save.ClickAsync();

            // A successful create lands on the new child's own detail view (CRUD-001's behaviour), and it
            // must RENDER -- the URL alone would also be satisfied by a view that fails to load, which is
            // exactly how BUG-009 hid. Waiting for it also keeps the screenshot off a mid-load frame.
            await Expect(Page).ToHaveURLAsync(
                new Regex(@"/detail/OrderItem_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 20000 });
            await Expect(Page.GetByRole(AriaRole.Button,
                    new() { NameRegex = new Regex("^save$", RegexOptions.IgnoreCase) }).First)
                .ToBeVisibleAsync(new() { Timeout = 20000 });
            await Expect(Page.GetByText(new Regex("Failed to load", RegexOptions.IgnoreCase)))
                .ToHaveCountAsync(0, new() { Timeout = 5000 });
            await Shot("crud002-02-child-created-from-master");

            // THE assertion: it belongs to the master. A create that merely succeeded would pass every
            // check above and still be wrong -- an orphan child is the failure this feature exists to
            // prevent.
            Assert.AreEqual(1, await ChildCountAsync(http, orderKey),
                "the child must be associated with the Order its New button was clicked inside");
        }
        finally {
            // Children first: the order cannot go while it still owns rows.
            var resp = await http.GetAsync($"api/odata/OrderItem?$filter=Order/ID eq {orderKey}&$select=ID");
            if (resp.IsSuccessStatusCode) {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var row in doc.RootElement.GetProperty("value").EnumerateArray())
                    await http.DeleteAsync($"api/save/OrderItem/{row.GetProperty("ID").GetString()}");
            }
            await http.DeleteAsync($"api/save/Order/{orderKey}");
        }
    }
}
