using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

[TestClass]
public class SaveValidationTests : TestBase {
    // The OData-PATCH probe test that used to live here (mutating a real ApplicationUser row via
    // $top=1 + PATCH UserName=null, restoring in finally) was deleted per kill-gate review: an armed,
    // repeating mutation of real users on shared dev data has negative value. Its finding — OData
    // PATCH returns 204 and silently persists a save that violates a real [RuleRequiredField] rule,
    // because the standalone Web API host never activates PersistenceValidationController (a
    // ViewController, dormant without a live View/Frame) — is root-caused and permanently recorded in
    // docs/notes/save-contract.md, with no need to re-run the mutation to
    // keep proving it.
    //
    // Task 2: retargeted to Employee.FirstName, which carries a REAL [RuleRequiredField] in the demo
    // module (confirmed live: Employee_DetailView now reports Required:true on FirstName — see
    // DetailViewMetadataTests.Employee_DetailView_reports_Required_true_on_RuleRequiredField_member,
    // closing the old metadata/save Required-mismatch finding from the original POC). SaveController
    // wires IValidator itself (see its header comment) so a save that breaks this rule must 422 with
    // the MemberErrors/Messages contract. Nothing commits on this path — verified below by re-reading
    // the row over OData afterwards, not just by trusting the 422 status.
    [TestMethod]
    public async Task SaveController_returns_422_with_member_errors_on_required_field_violation() {
        var client = await GetClientAsync("Admin");
        var first = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Employee?$top=1&$select={KnownModel.EmployeeKeyMember},{KnownModel.EmployeeRequiredMember}");
        var row = first.GetProperty("value")[0];
        var key = row.GetProperty(KnownModel.EmployeeKeyMember).GetString();
        var originalFirstName = row.GetProperty(KnownModel.EmployeeRequiredMember).GetString();

        var resp = await client.PostAsJsonAsync($"api/save/Employee/{key}",
            new Dictionary<string, object?> { [KnownModel.EmployeeRequiredMember] = null });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.GetProperty("MemberErrors").TryGetProperty(KnownModel.EmployeeRequiredMember, out var msg),
            "422 body does not identify the offending member");
        Assert.IsFalse(string.IsNullOrEmpty(msg.GetString()));
        Assert.IsGreaterThan(0, body.GetProperty("Messages").GetArrayLength());

        // Nothing commits on the 422 path: re-read proves the row is untouched.
        var reread = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Employee({key})?$select={KnownModel.EmployeeRequiredMember}");
        Assert.AreEqual(originalFirstName, reread.GetProperty(KnownModel.EmployeeRequiredMember).GetString(),
            "save controller must not have committed the invalid change");
    }

    [TestMethod]
    public async Task SaveController_returns_404_for_unexposed_type() {
        var client = await GetClientAsync("Admin");
        var resp = await client.PostAsJsonAsync("api/save/DoesNotExist/anything",
            new Dictionary<string, object?>());
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
