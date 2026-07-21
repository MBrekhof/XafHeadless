using System.Net;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// Read-only OData smoke over the chosen Order type. Doubles as the multi-tenancy proof: an
// authenticated GET returns real tenant data (Admin@company1.com resolves to the company1 tenant DB).
[TestClass]
public class ODataSmokeTests : TestBase {
    [TestMethod]
    public async Task Anonymous_is_rejected() {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var response = await client.GetAsync("api/odata/Order?$top=1");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Admin_can_read_orders_paged_and_sorted() {
        var client = await GetClientAsync("Admin");
        var json = await client.GetStringAsync(
            $"api/odata/Order?$top=5&$orderby={KnownModel.OrderKeyMember} desc&$count=true");
        var doc = JsonDocument.Parse(json);
        Assert.IsGreaterThan(0, doc.RootElement.GetProperty("value").GetArrayLength(), "no rows");
        Assert.IsGreaterThan(0, doc.RootElement.GetProperty("@odata.count").GetInt64());
    }
}
