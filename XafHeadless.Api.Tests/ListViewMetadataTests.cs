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

    // GRID-005: LookupMetadata projected ObjectType/KeyMember/DisplayMember and NO type for the display
    // member, so the client could not tell that sorting Order_ListView's Store column means
    // $orderby=Store/Emblem over a NAVIGATION PROPERTY -- a guaranteed 400 ("The $orderby expression
    // must evaluate to a single value of primitive type"). BUG-005 could only strip the shaping AFTER
    // the click. Classifying the display member is what lets the client refuse it up front.
    [TestMethod]
    public async Task Lookup_metadata_projects_its_display_member_data_type() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.OrderListViewId}");
        var lookups = meta.GetProperty("Columns").EnumerateArray()
            .Where(c => c.TryGetProperty("Lookup", out var l) && l.ValueKind == JsonValueKind.Object).ToList();
        Assert.IsNotEmpty(lookups, "Order_ListView must project at least one lookup column for this to prove anything");

        var store = lookups.Single(c => c.GetProperty("Member").GetString() == KnownModel.OrderNonPrimitiveLookupColumn);
        Assert.AreEqual("lookup", store.GetProperty("Lookup").GetProperty("DisplayDataType").GetString(),
            $"{KnownModel.OrderNonPrimitiveLookupColumn} displays a reference, not a primitive -- the client needs the type to refuse the sort");

        foreach (var c in lookups)
            Assert.IsFalse(string.IsNullOrEmpty(c.GetProperty("Lookup").GetProperty("DisplayDataType").GetString()),
                $"every projected lookup must carry its display member's type ({c.GetProperty("Member").GetString()} did not)");
    }

    [TestMethod]
    public async Task Unknown_view_returns_404() {
        var client = await GetClientAsync("Admin");
        var response = await client.GetAsync("api/model/views/DoesNotExist_ListView");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
