using System.Net;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// NPO-001: the read feed for non-persistent [DomainComponent] types -- the class of view that previously
// projected metadata fine and then loaded no data at all, because OData cannot serve a type with no DbSet.
//
// Opportunity is the primary subject deliberately: it is exactly 4 rows derived from an enum, so these
// assertions hold on an unseeded database. QuoteAnalysis is one row per Quote and its assertions are
// written to hold at any seed size.
[TestClass]
public class NonPersistentTests : TestBase {
    // Keep in sync with NonPersistentController.MaxRows / XafListView.RowCap.
    const int MaxRows = 5000;

    [TestMethod]
    public async Task Anonymous_is_rejected() {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var response = await client.GetAsync("api/nonpersistent/Opportunity");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The whole point of the card: a non-persistent type now has a wire representation. Before NPO-001
    // there was no endpoint at all and the view loaded nothing.
    [TestMethod]
    public async Task Opportunity_returns_one_row_per_stage_except_summary() {
        var client = await GetClientAsync("Admin");
        var doc = JsonDocument.Parse(await client.GetStringAsync("api/nonpersistent/Opportunity"));

        var rows = doc.RootElement.GetProperty("value");
        Assert.AreEqual(4, rows.GetArrayLength(), "Stage enum minus Summary");
        Assert.AreEqual(4, doc.RootElement.GetProperty("@odata.count").GetInt64());

        var stages = rows.EnumerateArray().Select(r => r.GetProperty("Stage").GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "High", "Medium", "Low", "Unlikely" }, stages);
        // Summary is the band that spans every other band (Stage.Range() -> 0.0..1.0); including it would
        // double-count every quote. The demo's own controller excludes it, so we must too.
        CollectionAssert.DoesNotContain(stages, "Summary");
    }

    // Enums go out as their CLR NAME, matching what OData V4 emits, so a row from this endpoint and a row
    // from api/odata read identically to the client. A regression to numeric here would be silent -- the
    // client's EnumValueCanon tolerates both forms -- so it is asserted explicitly.
    [TestMethod]
    public async Task Enum_members_are_serialized_as_names_not_numbers() {
        var client = await GetClientAsync("Admin");
        var doc = JsonDocument.Parse(await client.GetStringAsync("api/nonpersistent/Opportunity"));
        var stage = doc.RootElement.GetProperty("value")[0].GetProperty("Stage");
        Assert.AreEqual(JsonValueKind.String, stage.ValueKind, "enum must serialize as its name");
    }

    // A type the host was never told how to populate must NOT come back as an empty list: that is
    // indistinguishable from a view that legitimately has no rows, and it is precisely the silent-empty
    // failure (NonPersistentObjectSpace.CreateCollection's fallback BindingList) that NPO-001 removes.
    [TestMethod]
    public async Task Unregistered_and_unknown_types_are_404() {
        var client = await GetClientAsync("Admin");
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await client.GetAsync("api/nonpersistent/NoSuchTypeAnywhere")).StatusCode);
        // RoutePoint is a real [DomainComponent] in the demo module with no populator registered.
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await client.GetAsync("api/nonpersistent/RoutePoint")).StatusCode);
    }

    // A computed type has no table to write to, so the view must offer no write affordance. Both of the
    // usual inputs to the allow-set say "yes" here -- the model describes a generic ListView, and the
    // security system grants create/delete on a type it holds no permissions for -- so without the
    // read-only override the client renders a New button that cannot possibly save. Caught by looking at
    // the rendered page, not by a passing assertion, which is why it is now asserted.
    [TestMethod]
    public async Task Non_persistent_view_offers_no_write_actions() {
        var client = await GetClientAsync("Admin");
        var doc = JsonDocument.Parse(await client.GetStringAsync("api/model/views/Opportunity_ListView"));

        var allow = doc.RootElement.GetProperty("Allow");
        Assert.IsFalse(allow.GetProperty("New").GetBoolean(), "nowhere to save a new computed row");
        Assert.IsFalse(allow.GetProperty("Edit").GetBoolean());
        Assert.IsFalse(allow.GetProperty("Delete").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("NonPersistent").GetBoolean(),
            "the client picks its data route off this flag");
    }

    // The cap bounds the WIRE while @odata.count keeps reporting the truth, so the client can say
    // "showing the first N of M" rather than silently presenting a truncated set as complete.
    [TestMethod]
    public async Task QuoteAnalysis_is_capped_but_reports_the_true_total() {
        var client = await GetClientAsync("Admin");
        var doc = JsonDocument.Parse(await client.GetStringAsync("api/nonpersistent/QuoteAnalysis"));

        var returned = doc.RootElement.GetProperty("value").GetArrayLength();
        var total = doc.RootElement.GetProperty("@odata.count").GetInt64();

        Assert.IsLessThanOrEqualTo(MaxRows, returned, "the cap must bound what crosses the wire");
        Assert.IsGreaterThanOrEqualTo(returned, total, "count is the true total, never less than what was sent");
        if (total > MaxRows) Assert.AreEqual(MaxRows, returned, "over the cap, exactly the cap is returned");
    }
}
