using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

[TestClass]
public class ListViewMetadataTests : TestBase {
    [TestMethod]
    public async Task ListView_metadata_has_columns_with_captions_and_types() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.OrderListViewId}");
        Assert.AreEqual("ListView", meta.GetProperty("Type").GetString());
        Assert.AreEqual(KnownModel.OrderKeyMember, meta.GetProperty("KeyMember").GetString()); // PH2-001
        var columns = meta.GetProperty("Columns").EnumerateArray().ToList();
        Assert.IsGreaterThanOrEqualTo(3, columns.Count);
        Assert.IsTrue(columns.All(c => !string.IsNullOrEmpty(c.GetProperty("Caption").GetString())));
        Assert.IsTrue(columns.Any(c => c.GetProperty("Member").GetString() == KnownModel.OrderColumn1));
    }

    [TestMethod]
    public async Task Restricted_role_sees_fewer_or_equal_members_and_no_forbidden_ones() {
        var admin = await GetClientAsync("Admin");
        var restricted = await GetClientAsync("Restricted");
        var adminCols = await GetColumnMembers(admin);
        var restrictedCols = await GetColumnMembers(restricted);
        Assert.IsLessThanOrEqualTo(adminCols.Count, restrictedCols.Count);
        Assert.IsTrue(restrictedCols.All(adminCols.Contains),
            "restricted saw a member admin did not — trimming is broken");
        Assert.Contains(KnownModel.RestrictedDeniedMember,
adminCols, "admin should see the member the restricted role denies");
        Assert.DoesNotContain(KnownModel.RestrictedDeniedMember,
restrictedCols, "restricted role denies read on this member — projector must omit it");
    }

    static async Task<List<string>> GetColumnMembers(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.OrderListViewId}"))
        .GetProperty("Columns").EnumerateArray()
        .Select(x => x.GetProperty("Member").GetString()!).ToList();

    [TestMethod]
    public async Task Unknown_view_returns_404() {
        var client = await GetClientAsync("Admin");
        var response = await client.GetAsync("api/model/views/DoesNotExist_ListView");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
