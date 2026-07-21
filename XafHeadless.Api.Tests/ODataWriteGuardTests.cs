using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// SEC-001: the OData surface is READ-ONLY (see ODataReadOnlyMiddleware / docs/notes/save-contract.md).
// Authenticated mutating verbs under /api/odata must be rejected with 405 before reaching the
// non-validating XAF CRUD endpoints; GET must still succeed.
[TestClass]
public class ODataWriteGuardTests : TestBase {
    [TestMethod]
    [DataRow("POST")]
    [DataRow("PATCH")]
    [DataRow("DELETE")]
    public async Task Odata_mutations_are_blocked_with_405(string method) {
        var client = await GetClientAsync("Admin");
        var request = new HttpRequestMessage(new HttpMethod(method), $"api/odata/{OrderSet}") {
            Content = JsonContent.Create(new { InvoiceNumber = "HACK" })
        };
        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow, "405 should advertise Allow: GET");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(string.IsNullOrEmpty(body.GetProperty("error").GetString()),
            "405 body should explain the read-only contract");
    }

    [TestMethod]
    public async Task Odata_get_still_succeeds() {
        var client = await GetClientAsync("Admin");
        var response = await client.GetAsync($"api/odata/{OrderSet}?$top=1");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    const string OrderSet = "Order";
}
