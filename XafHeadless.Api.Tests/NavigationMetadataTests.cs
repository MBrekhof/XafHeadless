using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// GAP-004: GET api/model/navigation -- the flat, security-trimmed client menu. Black-box-over-HTTP,
// same pattern as ListViewMetadataTests.
[TestClass]
public class NavigationMetadataTests : TestBase {
    [TestMethod]
    public async Task Navigation_includes_the_renderable_demo_list_views_with_captions() {
        var client = await GetClientAsync("Admin");
        var items = await client.GetFromJsonAsync<JsonElement[]>("api/model/navigation");
        Assert.IsNotNull(items);
        Assert.IsNotEmpty(items, "navigation menu should not be empty for Admin");

        foreach (var viewId in new[] {
            KnownModel.OrderListViewId, KnownModel.EmployeeListViewId,
            KnownModel.CustomerListViewId, KnownModel.ProductListViewId
        }) {
            var match = items.FirstOrDefault(i => i.GetProperty("ViewId").GetString() == viewId);
            Assert.AreEqual(JsonValueKind.Object, match.ValueKind, $"{viewId} missing from navigation");
            Assert.IsFalse(string.IsNullOrEmpty(match.GetProperty("Caption").GetString()),
                $"{viewId} has an empty caption");
        }

        // Model-driven order preserved: Employee_ListView is first in the live nav tree (KnownModel).
        Assert.AreEqual(KnownModel.NavigationFirstItemViewId, items[0].GetProperty("ViewId").GetString());
    }

    [TestMethod]
    public async Task Navigation_excludes_dashboards_admin_and_reports_items() {
        var client = await GetClientAsync("Admin");
        var items = await client.GetFromJsonAsync<JsonElement[]>("api/model/navigation");
        Assert.IsNotNull(items);

        Assert.IsFalse(items.Any(i => i.GetProperty("ViewId").GetString() == KnownModel.ApplicationUserListViewId),
            "ApplicationUser_ListView must be excluded -- not independently OData-exposed (WebApiOptions.BusinessObjects)");
        Assert.IsFalse(items.Any(i => i.GetProperty("Caption").GetString() == "Welcome"),
            "the Welcome DashboardView must be excluded -- not a ListView");
        Assert.IsFalse(items.Any(i => i.GetProperty("ViewId").GetString()!.Contains("Dashboard", StringComparison.OrdinalIgnoreCase)),
            "no DashboardView should ever appear -- rule 1 (ListView only)");
        Assert.IsFalse(items.Any(i =>
                i.GetProperty("ViewId").GetString()!.Contains("Report", StringComparison.OrdinalIgnoreCase) ||
                i.GetProperty("Caption").GetString()!.Contains("Report", StringComparison.OrdinalIgnoreCase)),
            "no Reports item should appear -- ReportDataV2 is not OData-exposed");
    }

    // Cheap security-path exercise (brief: optional, only if it doesn't balloon the task) -- reuses the
    // existing restricted@company1.com fixture (TestFixturesController), which grants type-level Read
    // on Order. Not a true exclusion test (no seeded fixture denies a whole type outright for this
    // role), but it does prove the endpoint runs the real per-user CanRead check rather than a fixed
    // Admin-only path: a non-admin caller still sees the type it's actually allowed to read.
    [TestMethod]
    public async Task Restricted_role_still_sees_a_permitted_item_in_navigation() {
        var restricted = await GetClientAsync("Restricted");
        var items = await restricted.GetFromJsonAsync<JsonElement[]>("api/model/navigation");
        Assert.IsNotNull(items);
        Assert.IsTrue(items.Any(i => i.GetProperty("ViewId").GetString() == KnownModel.OrderListViewId),
            "restricted role grants type Read on Order -- Order_ListView should still be navigable");
    }

    // GAP-008-minors #4: UserLayoutPref (host prefs infra) and LookupProbe (dev-only projection
    // fixture) are host-owned SharedBusinessObjects (Startup.WithSharedBusinessObjects), never passed
    // to options.BusinessObject<T>() -- so NavigationProjector's rule 2 (exposedTypes.Contains(type),
    // sourced from WebApiOptions.BusinessObjects) already excludes them. This is a regression guard: no
    // navigation item's ViewId may ever reference either type, i.e. neither must leak into the client menu.
    [TestMethod]
    public async Task Navigation_excludes_host_infra_and_dev_only_entities() {
        var client = await GetClientAsync("Admin");
        var items = await client.GetFromJsonAsync<JsonElement[]>("api/model/navigation");
        Assert.IsNotNull(items);

        foreach (var infraType in new[] { "UserLayoutPref", "LookupProbe" }) {
            Assert.IsFalse(
                items.Any(i => i.GetProperty("ViewId").GetString()!.Contains(infraType, StringComparison.Ordinal)),
                $"{infraType} is host infra/dev-only (not OData-exposed) -- it must never appear in the client nav menu");
        }
    }
}
