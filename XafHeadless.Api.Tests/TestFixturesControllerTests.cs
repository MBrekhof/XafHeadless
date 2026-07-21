using System.Net;

namespace XafHeadless.Api.Tests;

[TestClass]
public class TestFixturesControllerTests : TestBase {
    // PH2 review fix #1c: prove the gate actually gates. Restricted is deliberately non-admin (that's
    // the whole point of the fixture), so POSTing the seeder endpoint as Restricted must be denied.
    // The test host runs under launchSettings.json's "http" profile (ASPNETCORE_ENVIRONMENT=
    // Development), so the environment gate (finding #1a) is a no-op here and the ROLE gate (finding
    // #1b, the explicit IsAdministrative check -- see TestFixturesController) is what's actually
    // exercised: expect 403, not the 404 the environment gate would produce outside Development.
    [TestMethod]
    public async Task Restricted_role_is_denied_403_seeding_the_test_fixture() {
        var restricted = await GetClientAsync("Restricted");
        var response = await restricted.PostAsync("api/test-fixtures/restricted-role", null);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
