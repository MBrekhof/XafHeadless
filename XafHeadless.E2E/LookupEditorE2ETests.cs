using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// PH2-005 / LOOKUP-001: the lookup editor must be able to DISPLAY the object its record already
// references. It used to fetch candidates as the target type's first 50 rows over OData, which meant the
// current value was only shown when it happened to fall inside that window. Employee has 51 rows in this
// demo, so the window was already short -- and CustomerStore has 200.
//
// api/lookup/{type} takes the current key and returns that object unconditionally, alongside a bounded,
// server-searchable page whose text is resolved by the same display-path walk grids use (BUG-008).
[TestClass]
public class LookupEditorE2ETests : PlaywrightFixture {
    // Ask the API for an Order that actually HAS an employee, and navigate straight to it.
    //
    // The first version of this test clicked the first row of Order_ListView. It passed alone and failed in
    // the full suite: sibling tests sort and group that view, so "the first row" is not a fixed order, and
    // the row it landed on had no Employee at all -- an empty combo that looks exactly like the defect. The
    // assertion is about the editor, so the fixture must not also be asserting something about row order.
    static async Task<string> OrderKeyWithAnEmployeeAsync() {
        using var http = await ApiClientAsync();
        var resp = await http.GetAsync("api/odata/Order?$top=20&$select=ID&$expand=Employee($select=ID)");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var row in doc.RootElement.GetProperty("value").EnumerateArray())
            if (row.TryGetProperty("Employee", out var e) && e.ValueKind == JsonValueKind.Object)
                return row.GetProperty("ID").GetString()!;
        Assert.Fail("no Order in the first 20 has an Employee -- cannot prove anything about the editor");
        return "";
    }

    [TestMethod]
    public async Task OrderDetail_LookupEditor_ShowsTheReferencedObjectsText() {
        var key = await OrderKeyWithAnEmployeeAsync();

        await LoginAsync();
        await NavigateSpa($"/detail/Order_DetailView/{key}");
        await Expect(Page).ToHaveURLAsync(
            new Regex(@"/detail/Order_DetailView/[0-9a-fA-F-]{36}$"), new() { Timeout = 20000 });

        // Order_DetailView's only reference editor is Employee (verified in the projected layout). Located
        // by ARIA role rather than by DevExpress CSS class: the class names are internal and version-bound
        // (the UI-002 restyle work found several that had moved), while the role is part of the rendered
        // contract.
        var combo = Page.GetByRole(AriaRole.Combobox).First;
        await Expect(combo).ToBeVisibleAsync(new() { Timeout = 20000 });

        // THE assertion: the combo shows the referenced object's display text. An empty box here is the
        // old defect -- a real value that the editor could not see because it was outside the fetch window.
        await Expect(combo).Not.ToHaveValueAsync("", new() { Timeout = 20000 });
        var shown = await combo.InputValueAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(shown),
            "the lookup editor must display the object its record references, not an empty box");
        await Shot("lookup001-01-editor-shows-current-value");
    }
}
