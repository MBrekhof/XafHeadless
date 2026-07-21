using System.Net.Http.Json;

namespace XafHeadless.Api.Tests;

public record ModelDiagnostics(int ViewsCount, string ListViewId, string DetailViewId,
    bool HasListView, bool HasDetailView, string[] ListViewColumns, string[] DetailViewCollections);

[TestClass]
public class ModelSpikeTests : TestBase {
    // Proves the UI-less multi-tenant host builds the full Views application model for the demo module
    // and can project the chosen Order pair (list + detail with its nested collection).
    [TestMethod]
    public async Task Host_builds_views_model_for_demo_module() {
        var client = await GetClientAsync("Admin");
        var diag = await client.GetFromJsonAsync<ModelDiagnostics>(
            $"api/diagnostics/model?listView={KnownModel.OrderListViewId}&detailView={KnownModel.OrderDetailViewId}");
        Assert.IsNotNull(diag);
        Assert.IsGreaterThan(100, diag.ViewsCount, $"Only {diag.ViewsCount} views — model generation incomplete?");
        Assert.IsTrue(diag.HasListView, $"{KnownModel.OrderListViewId} missing from model");
        Assert.IsTrue(diag.HasDetailView, $"{KnownModel.OrderDetailViewId} missing from model");
        Assert.IsNotEmpty(diag.ListViewColumns, "ListView has no columns");
        Assert.IsTrue(diag.ListViewColumns.Contains(KnownModel.OrderColumn1),
            $"expected column {KnownModel.OrderColumn1}");
        Assert.IsTrue(diag.DetailViewCollections.Contains(KnownModel.OrderCollectionMember),
            $"expected nested collection {KnownModel.OrderCollectionMember}");
        Console.WriteLine("Columns: " + string.Join(", ", diag.ListViewColumns));
        Console.WriteLine("Collections: " + string.Join(", ", diag.DetailViewCollections));
    }

    // The validation-rich Employee pair is also present (recorded for Task 2's real-rule save test).
    [TestMethod]
    public async Task Host_exposes_validation_rich_Employee_pair() {
        var client = await GetClientAsync("Admin");
        var diag = await client.GetFromJsonAsync<ModelDiagnostics>(
            $"api/diagnostics/model?listView={KnownModel.EmployeeListViewId}&detailView={KnownModel.EmployeeDetailViewId}");
        Assert.IsNotNull(diag);
        Assert.IsTrue(diag.HasListView && diag.HasDetailView, "Employee pair missing");
        Assert.IsTrue(diag.DetailViewCollections.Contains(KnownModel.EmployeeCollectionMember),
            $"expected nested collection {KnownModel.EmployeeCollectionMember}");
    }
}
