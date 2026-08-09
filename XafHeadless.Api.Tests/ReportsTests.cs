using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// RPT-001: the catalogue, the run gate, and the collect gate.
//
// These deliberately avoid needing a running JobServer: a test that only passes when a background worker
// happens to be up is a test that fails for the wrong reason. The full run -> render -> collect round trip
// and the cross-user 403 were verified live against both hosts (see docs/DONE.md); what is pinned here is
// everything reachable from the API alone.
[TestClass]
public class ReportsTests : TestBase {
    const string KnownReport = "OutlookInspiredDemo.Module.Resources.Reports.ProductOrders";

    [TestMethod]
    public async Task Catalogue_lists_reports_with_stable_identifiers() {
        var client = await GetClientAsync("Admin");
        var reports = await client.GetFromJsonAsync<JsonElement[]>("api/reports");
        Assert.IsNotNull(reports);
        Assert.IsNotEmpty(reports, "the demo seeds predefined reports -- an empty catalogue means the read is wrong");

        // PredefinedReportTypeName, not the primary key: this host's tenant DB is a disposable dev
        // catalogue whose ReportDataV2 GUIDs regenerate on every re-seed, so a key would rot.
        Assert.IsTrue(reports.Any(r => r.GetProperty("Id").GetString() == KnownReport),
            $"'{KnownReport}' must be in the catalogue by its stable resource-type name");
        Assert.IsTrue(reports.All(r => !string.IsNullOrWhiteSpace(r.GetProperty("Name").GetString())),
            "every entry needs a display name -- the id alone is not something to show a user");
    }

    // The catalogue is security-trimmed, so running must be gated by the same read: otherwise the trim is
    // advisory and anyone who knows an identifier can render a report they cannot see.
    [TestMethod]
    public async Task Running_an_unknown_report_is_rejected() {
        var client = await GetClientAsync("Admin");
        var response = await client.PostAsync("api/reports/NoSuchReport/run", null);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    // A correlation id that has produced nothing yet is "not ready", not "broken" -- the client polls on
    // this, so a 404 or a 500 here would turn a normal wait into an error.
    [TestMethod]
    public async Task Collecting_a_run_that_has_not_finished_reports_pending() {
        var client = await GetClientAsync("Admin");
        var response = await client.GetAsync($"api/reports/runs/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
    }

    // Rendered reports are produced by a SERVICE user and can contain rows the caller may not see, so the
    // whole surface is authenticated. The per-artifact ownership check is the second gate.
    [TestMethod]
    public async Task The_report_surface_rejects_anonymous_callers() {
        using var anon = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anon.GetAsync("api/reports")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"api/reports/runs/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await anon.PostAsync($"api/reports/{KnownReport}/run", null)).StatusCode);
    }
}
