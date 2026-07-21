using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// GAP-003: the create flow. Keyless POST api/save/{type} on SaveController -- os.CreateObject(clrType),
// apply the body through the SAME ApplyChanges helper the update path (Save) uses, same
// Committing/IValidator/CommitChanges contract. Success = 201 + { key } (server-generated,
// BaseObject.ID, never sent by the client); a rule violation = the same 422 { MemberErrors, Messages }
// contract SaveValidationTests already proves for update.
//
// Test targets per brief (do not swap): Order for the success/cleanup path (no RuleRequiredField, not
// ForbidDelete -- can be created AND deleted); Employee for the 422 path (real RuleRequiredField rules;
// nothing commits on a 422, so the ForbidDelete on Employee never comes into play here).
// TEST-001: Create_Order creates a real Order row (then deletes it) -- while it exists, a
// concurrent $top=1-no-$orderby "first row" pick elsewhere could land on it. [DoNotParallelize]
// removes this class from the parallel pool entirely (see SaveReferenceAndEnumTests for the same
// reasoning), so it never overlaps with the other classes that mutate shared Order rows.
[TestClass]
[DoNotParallelize]
public class SaveCreateTests : TestBase {
    [TestMethod]
    public async Task Create_Order_returns_201_with_server_generated_key_and_persists() {
        var client = await GetClientAsync("Admin");
        var orderDate = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var poNumber = $"PO-{Guid.NewGuid():N}";

        var resp = await client.PostAsJsonAsync("api/save/Order", new Dictionary<string, object?> {
            [KnownModel.OrderColumn2] = orderDate,      // "OrderDate"
            [KnownModel.OrderScalarMember] = poNumber,  // "PONumber"
        });

        Assert.AreEqual(HttpStatusCode.Created, resp.StatusCode,
            $"expected 201, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var key = body.GetProperty("key").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(key), "201 body must carry a non-empty server-generated key");
        Assert.IsTrue(Guid.TryParse(key, out _), $"key '{key}' is not a Guid -- the client never sent one, so it must be server-generated");

        try {
            var reread = await client.GetFromJsonAsync<JsonElement>(
                $"api/odata/Order({key})?$select={KnownModel.OrderColumn2},{KnownModel.OrderScalarMember}");
            Assert.AreEqual(poNumber, reread.GetProperty(KnownModel.OrderScalarMember).GetString(),
                "PONumber sent at create time did not persist");
            Assert.AreEqual(orderDate, reread.GetProperty(KnownModel.OrderColumn2).GetDateTime(),
                "OrderDate sent at create time did not persist");
        } finally {
            var del = await client.DeleteAsync($"api/test-fixtures/Order/{key}");
            Assert.IsTrue(del.IsSuccessStatusCode,
                $"cleanup delete of created Order {key} failed: {del.StatusCode} {await del.Content.ReadAsStringAsync()}");
        }
    }

    [TestMethod]
    public async Task Create_Employee_missing_required_fields_returns_422() {
        var client = await GetClientAsync("Admin");
        var marker = $"Test-{Guid.NewGuid():N}"; // unique FirstName so the defensive no-persist check below can't false-positive on unrelated rows

        var resp = await client.PostAsJsonAsync("api/save/Employee",
            new Dictionary<string, object?> { ["FirstName"] = marker }); // LastName/Title/Email/Address/City deliberately omitted

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, resp.StatusCode,
            $"expected 422, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var responseBody = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(responseBody.GetProperty("MemberErrors").TryGetProperty(KnownModel.EmployeeRequiredMemberLastName, out var msg),
            "422 body does not identify the omitted required LastName member");
        Assert.IsFalse(string.IsNullOrEmpty(msg.GetString()));
        Assert.IsGreaterThan(0, responseBody.GetProperty("Messages").GetArrayLength());

        // Defensive: validation throws in Committing, before CommitChanges -- nothing should persist.
        var check = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Employee?$filter=FirstName eq '{marker}'&$select={KnownModel.EmployeeKeyMember}");
        Assert.AreEqual(0, check.GetProperty("value").GetArrayLength(), "invalid create must not have persisted");
    }
}
