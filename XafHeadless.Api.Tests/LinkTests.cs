using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// GAP-010: Link/Unlink for a master's to-many association. Link ADDS an existing object to the collection,
// Unlink REMOVES the association WITHOUT deleting the object. [DoNotParallelize] because the round-trip test
// mutates a many-to-many join (TEST-001 pattern), and it restores in a finally.
[TestClass]
[DoNotParallelize]
public class LinkTests : TestBase {
    // Fetch an Employee + its currently-linked EmployeeTask IDs via OData $expand (the same read path the
    // live probe used). $select=ID inside $expand is safe (ID is a real EDM property) -- the "never $select"
    // rule is about the top-level entity naming model-only members.
    static async Task<(string empId, HashSet<string> linked)> GetEmployeeAsync(HttpClient c) {
        var d = await c.GetFromJsonAsync<JsonElement>(
            $"api/odata/Employee?$top=1&$expand={KnownModel.EmployeeSharedCollectionMember}($select=ID)");
        var emp = d.GetProperty("value")[0];
        var empId = emp.GetProperty(KnownModel.EmployeeKeyMember).GetString()!;
        var linked = emp.TryGetProperty(KnownModel.EmployeeSharedCollectionMember, out var arr)
            ? arr.EnumerateArray().Select(t => t.GetProperty("ID").GetString()!).ToHashSet()
            : new HashSet<string>();
        return (empId, linked);
    }

    static async Task<HashSet<string>> GetLinkedAsync(HttpClient c, string empId) {
        var d = await c.GetFromJsonAsync<JsonElement>(
            $"api/odata/Employee?$filter=ID eq {empId}&$expand={KnownModel.EmployeeSharedCollectionMember}($select=ID)");
        var emp = d.GetProperty("value")[0];
        return emp.TryGetProperty(KnownModel.EmployeeSharedCollectionMember, out var arr)
            ? arr.EnumerateArray().Select(t => t.GetProperty("ID").GetString()!).ToHashSet()
            : new HashSet<string>();
    }

    [TestMethod]
    public async Task Link_then_unlink_round_trips_a_many_to_many_association() {
        var client = await GetClientAsync("Admin");
        var (empId, linked) = await GetEmployeeAsync(client);
        // pick an EmployeeTask that ISN'T already linked to this employee, so LINK actually changes the set
        var tasks = await client.GetFromJsonAsync<JsonElement>("api/odata/EmployeeTask?$top=20&$select=ID");
        var taskId = tasks.GetProperty("value").EnumerateArray()
            .Select(t => t.GetProperty("ID").GetString()!).First(id => !linked.Contains(id));
        var route = $"api/link/Employee/{empId}/{KnownModel.EmployeeSharedCollectionMember}/{taskId}";
        var baseCount = linked.Count;
        try {
            var link = await client.PostAsync(route, null);
            Assert.AreEqual(HttpStatusCode.OK, link.StatusCode, "LINK should return 200");
            var afterLink = await GetLinkedAsync(client, empId);
            Assert.HasCount(baseCount + 1, afterLink, "LINK must add exactly one association");
            Assert.Contains(taskId, afterLink, "LINK must associate the requested task");

            var unlink = await client.DeleteAsync(route);
            Assert.AreEqual(HttpStatusCode.OK, unlink.StatusCode, "UNLINK should return 200");
            var afterUnlink = await GetLinkedAsync(client, empId);
            Assert.HasCount(baseCount, afterUnlink, "UNLINK must remove exactly the one association");
            Assert.DoesNotContain(taskId, afterUnlink, "UNLINK must remove the association (but not delete the task)");

            // the task itself still exists -- Unlink is not Delete
            var task = await client.GetFromJsonAsync<JsonElement>($"api/odata/EmployeeTask?$filter=ID eq {taskId}&$select=ID");
            Assert.HasCount(1, task.GetProperty("value").EnumerateArray().ToList(), "UNLINK must NOT delete the EmployeeTask");
        } finally {
            await client.DeleteAsync(route); // restore: ensure the association is gone even if an assert threw
        }
    }

    // GAP-010: the projected LayoutNode.Aggregated flag drives the client's Link/Unlink-vs-New/Delete choice.
    // Employee.Evaluations is an owned (aggregated) collection -> Aggregated must be true.
    [TestMethod]
    public async Task Aggregated_flag_is_projected_on_a_nested_collection() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.EmployeeDetailViewId}");
        var flat = MetadataTestHelpers.Flatten(meta.GetProperty("Layout")).ToList();
        var evaluations = flat.Single(n => n.GetProperty("Kind").GetString() == "nestedList"
            && n.TryGetProperty("Member", out var m) && m.GetString() == KnownModel.EmployeeCollectionMember);
        Assert.IsTrue(evaluations.GetProperty("Aggregated").GetBoolean(),
            "Employee.Evaluations is an owned collection -- Aggregated must project true so the client offers New/Delete, not Link/Unlink");
    }

    // GAP-010: link/unlink is for SHARED collections only; an aggregated (owned) member is rejected (400)
    // before the child is even resolved.
    [TestMethod]
    public async Task Link_on_an_aggregated_collection_is_rejected() {
        var client = await GetClientAsync("Admin");
        var (empId, _) = await GetEmployeeAsync(client);
        var resp = await client.PostAsync(
            $"api/link/Employee/{empId}/{KnownModel.EmployeeAggregatedCollectionMember}/{Guid.Empty}", null);
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            "link/unlink on an aggregated (owned) collection must be rejected -- those use create/delete");
    }

    [TestMethod]
    public async Task Link_on_an_unknown_master_type_is_404() {
        var client = await GetClientAsync("Admin");
        var resp = await client.PostAsync($"api/link/Bogus/{Guid.Empty}/Members/{Guid.Empty}", null);
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode, "an unexposed master type must 404");
    }
}
