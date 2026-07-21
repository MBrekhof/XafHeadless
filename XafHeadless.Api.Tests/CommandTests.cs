using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// Task 2: retargeted from the original POC's summary command over its key entity to OrderSummaryCommand
// -- a read-only summary of a selected Order + its OrderItems (count/total), same generic command
// envelope, same 5 shapes as the original brief. Real server logic (resolve Order via the SECURED
// ObjectSpace, walk its OrderItems collection), nothing committed so no dev-data restore is needed.
[TestClass]
public class CommandTests : TestBase {
    [TestMethod]
    public async Task Command_executes_against_selected_order() {
        var client = await GetClientAsync("Admin");
        var first = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=1&$select={KnownModel.OrderKeyMember}");
        var key = first.GetProperty("value")[0].GetProperty(KnownModel.OrderKeyMember).ToString();
        var resp = await client.PostAsJsonAsync("api/commands/OrderSummary",
            new { ObjectKeys = new[] { key } });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(result.GetProperty("Success").GetBoolean());
        Assert.IsFalse(string.IsNullOrEmpty(result.GetProperty("Message").GetString()));
    }

    [TestMethod]
    public async Task Command_returns_not_found_for_unknown_command_id() {
        var client = await GetClientAsync("Admin");
        var resp = await client.PostAsJsonAsync("api/commands/DoesNotExist", new { ObjectKeys = Array.Empty<string>() });
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [TestMethod]
    public async Task Command_returns_empty_keys_guard_result_for_no_keys() {
        var client = await GetClientAsync("Admin");
        var resp = await client.PostAsJsonAsync("api/commands/OrderSummary", new { ObjectKeys = Array.Empty<string>() });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(result.GetProperty("Success").GetBoolean());
        Assert.AreEqual("No order selected.", result.GetProperty("Message").GetString());
    }

    // Order keys on Guid (unlike the old int-keyed POC entity), so the not-found probe uses Guid.Empty --
    // a syntactically valid key that can never match a real row.
    [TestMethod]
    public async Task Command_returns_not_found_guard_result_for_nonexistent_order() {
        var client = await GetClientAsync("Admin");
        var missingKey = Guid.Empty.ToString();
        var resp = await client.PostAsJsonAsync("api/commands/OrderSummary", new { ObjectKeys = new[] { missingKey } });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(result.GetProperty("Success").GetBoolean());
        Assert.AreEqual($"Order {missingKey} not found.", result.GetProperty("Message").GetString());
    }

    // CommandRequest.ObjectKeys binds null when the property is omitted; the controller normalizes
    // null to Array.Empty<string>() before calling Execute (see CommandsController).
    [TestMethod]
    public async Task Command_normalizes_null_ObjectKeys_to_empty_keys_guard_result() {
        var client = await GetClientAsync("Admin");
        var resp = await client.PostAsJsonAsync("api/commands/OrderSummary", new { });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(result.GetProperty("Success").GetBoolean());
        Assert.AreEqual("No order selected.", result.GetProperty("Message").GetString());
    }
}
