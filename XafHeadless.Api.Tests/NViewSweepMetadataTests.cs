using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// GAP-009: proves the projector (ModelController -> ViewMetadataProjector) is genuinely
// view-agnostic by pointing it at TWO BRAND NEW list/detail pairs it was never built for --
// Customer and Product -- neither of which existed as a data point before this task (only
// Order/Employee did). One parameterized method per view kind covers all 4 known pairs rather
// than bespoke per-type methods: the assertions ARE the generic contract (>0 columns + a
// KeyMember; a non-empty layout with real editors) already locked down for Order/Employee
// specifically by ListViewMetadataTests/DetailViewMetadataTests. Re-asserting Order/Employee here
// is a cheap regression guard, not duplication of intent -- the point of this file is that the
// SAME two assertions hold for every view id, not just the ones the projector was built against.
[TestClass]
public class NViewSweepMetadataTests : TestBase {
    [TestMethod]
    [DataRow(KnownModel.OrderListViewId, KnownModel.OrderKeyMember)]
    [DataRow(KnownModel.EmployeeListViewId, KnownModel.EmployeeKeyMember)]
    [DataRow(KnownModel.CustomerListViewId, KnownModel.CustomerKeyMember)]
    [DataRow(KnownModel.ProductListViewId, KnownModel.ProductKeyMember)]
    public async Task ListView_projects_columns_and_key_for_any_view(string viewId, string keyMember) {
        var client = await GetClientAsync("Admin");
        var response = await client.GetAsync($"api/model/views/{viewId}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{viewId} did not project");
        var meta = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("ListView", meta.GetProperty("Type").GetString());
        Assert.AreEqual(keyMember, meta.GetProperty("KeyMember").GetString());
        var columns = meta.GetProperty("Columns").EnumerateArray().ToList();
        Assert.IsNotEmpty(columns, $"{viewId} has no columns");
        Assert.IsTrue(columns.All(c => !string.IsNullOrEmpty(c.GetProperty("Caption").GetString())),
            $"{viewId} has a column with an empty caption");
    }

    [TestMethod]
    [DataRow(KnownModel.OrderDetailViewId)]
    [DataRow(KnownModel.EmployeeDetailViewId)]
    [DataRow(KnownModel.CustomerDetailViewId)]
    [DataRow(KnownModel.ProductDetailViewId)]
    public async Task DetailView_projects_a_nonempty_layout_for_any_view(string viewId) {
        var client = await GetClientAsync("Admin");
        var response = await client.GetAsync($"api/model/views/{viewId}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{viewId} did not project");
        var meta = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("DetailView", meta.GetProperty("Type").GetString());
        var layout = meta.GetProperty("Layout");
        Assert.AreNotEqual(JsonValueKind.Null, layout.ValueKind, $"{viewId} has a null layout");
        var flat = MetadataTestHelpers.Flatten(layout).ToList();
        Assert.IsGreaterThanOrEqualTo(3,
flat.Count(n => n.GetProperty("Kind").GetString() == "item"), $"{viewId} projected too few editors");
    }

    // Confirms api/odata/{Type} (the data the generic client's DxGrid/detail actually reads) is
    // live and returns rows for both new types -- the OData half of the "verify before relying on
    // it" requirement, alongside the metadata assertions above.
    [TestMethod]
    [DataRow("Customer")]
    [DataRow("Product")]
    public async Task OData_set_is_exposed_and_returns_rows(string entitySet) {
        var client = await GetClientAsync("Admin");
        var json = await client.GetStringAsync($"api/odata/{entitySet}?$top=1&$count=true");
        var doc = JsonDocument.Parse(json);
        Assert.IsGreaterThan(0, doc.RootElement.GetProperty("value").GetArrayLength(), $"{entitySet}: no rows");
        Assert.IsGreaterThan(0, doc.RootElement.GetProperty("@odata.count").GetInt64());
    }
}
