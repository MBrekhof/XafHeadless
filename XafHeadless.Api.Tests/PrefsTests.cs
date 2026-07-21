using System.Net;
using System.Text;

namespace XafHeadless.Api.Tests;

// GAP-008: per-user layout-prefs endpoint. Proves the round-trip (PUT then GET returns the same blob for
// the SAME user) AND per-user isolation (a DIFFERENT user gets their OWN prefs, never a shared blob) --
// the identity is the framework's security user (ISecurityStrategyBase.UserId), never the request. The
// "Restricted" user (restricted@company1.com) is seeded once by AssemblyFixture (see TestBase).
[TestClass]
public class PrefsTests : TestBase {
    const string ViewId = KnownModel.OrderListViewId; // "Order_ListView"

    static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    [TestMethod]
    public async Task Put_then_Get_roundtrips_and_isolates_between_users() {
        var admin = await GetClientAsync("Admin");
        var restricted = await GetClientAsync("Restricted");

        // Unique per run so the assertions can't false-positive on a leftover blob from an earlier run
        // (the upsert overwrites the single (user, view) row each run).
        var adminBlob = $$"""{"marker":"admin-{{Guid.NewGuid():N}}","w":123}""";
        var restrictedBlob = $$"""{"marker":"restricted-{{Guid.NewGuid():N}}","w":456}""";

        // Admin PUT -> 200, then GET returns the exact same blob.
        var adminPut = await admin.PutAsync($"api/prefs/{ViewId}", Json(adminBlob));
        Assert.AreEqual(HttpStatusCode.OK, adminPut.StatusCode,
            $"admin PUT: {adminPut.StatusCode} {await adminPut.Content.ReadAsStringAsync()}");
        var adminGet = await admin.GetAsync($"api/prefs/{ViewId}");
        Assert.AreEqual(HttpStatusCode.OK, adminGet.StatusCode);
        Assert.AreEqual(adminBlob, await adminGet.Content.ReadAsStringAsync(),
            "admin GET must return exactly what admin PUT");

        // Restricted user must NOT see admin's blob -- either 204 (no prefs yet) or its own, never admin's.
        var restrictedGet1 = await restricted.GetAsync($"api/prefs/{ViewId}");
        var restrictedBody1 = restrictedGet1.StatusCode == HttpStatusCode.NoContent
            ? "" : await restrictedGet1.Content.ReadAsStringAsync();
        Assert.AreNotEqual(adminBlob, restrictedBody1,
            "PER-USER ISOLATION: the restricted user must not read the admin user's prefs");

        // Restricted writes its OWN, reads its OWN back, and admin's is UNTOUCHED (mutual isolation).
        (await restricted.PutAsync($"api/prefs/{ViewId}", Json(restrictedBlob))).EnsureSuccessStatusCode();
        var restrictedGet2 = await restricted.GetAsync($"api/prefs/{ViewId}");
        Assert.AreEqual(restrictedBlob, await restrictedGet2.Content.ReadAsStringAsync(),
            "restricted GET must return exactly what restricted PUT");
        var adminGet2 = await admin.GetAsync($"api/prefs/{ViewId}");
        Assert.AreEqual(adminBlob, await adminGet2.Content.ReadAsStringAsync(),
            "admin's prefs must be unchanged by the restricted user's write");
    }

    [TestMethod]
    public async Task Get_unsaved_view_returns_no_content() {
        var admin = await GetClientAsync("Admin");
        var resp = await admin.GetAsync($"api/prefs/NeverSaved_{Guid.NewGuid():N}_ListView");
        Assert.AreEqual(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [TestMethod]
    public async Task Prefs_endpoints_require_authentication() {
        using var anon = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var get = await anon.GetAsync($"api/prefs/{ViewId}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, get.StatusCode);
        var put = await anon.PutAsync($"api/prefs/{ViewId}", Json("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    // GAP-008-minors #2: blob-size cap. A fresh, per-test viewId keeps this isolated from the other
    // PrefsTests (and from the persisted-layout E2E test, which owns Order_ListView).
    [TestMethod]
    public async Task Put_rejects_an_oversized_body_but_accepts_a_normal_one() {
        var admin = await GetClientAsync("Admin");
        var viewId = $"Cap_{Guid.NewGuid():N}_ListView";

        // Over the 64 KB cap -- must be rejected (400 or 413) with a JSON {error} body, and must NOT
        // have been persisted (the subsequent GET below proves that).
        var oversized = new string('a', 70_000);
        var oversizedPut = await admin.PutAsync($"api/prefs/{viewId}", Json(oversized));
        Assert.IsTrue(
            oversizedPut.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            $"oversized PUT should be rejected with 400/413, got {oversizedPut.StatusCode}");
        var errorBody = await oversizedPut.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", errorBody, $"rejection body should carry a JSON error field: {errorBody}");
        var afterRejectedPut = await admin.GetAsync($"api/prefs/{viewId}");
        Assert.AreEqual(HttpStatusCode.NoContent, afterRejectedPut.StatusCode,
            "the oversized body must not have been persisted");

        // A normal-size body must still succeed and round-trip.
        var normalBlob = $$"""{"marker":"cap-ok-{{Guid.NewGuid():N}}"}""";
        var normalPut = await admin.PutAsync($"api/prefs/{viewId}", Json(normalBlob));
        Assert.AreEqual(HttpStatusCode.OK, normalPut.StatusCode,
            $"normal-size PUT should succeed: {normalPut.StatusCode} {await normalPut.Content.ReadAsStringAsync()}");
        var normalGet = await admin.GetAsync($"api/prefs/{viewId}");
        Assert.AreEqual(normalBlob, await normalGet.Content.ReadAsStringAsync());
    }
}
