using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace XafHeadless.Api.Tests;

public abstract class TestBase {
    protected static IConfiguration Config { get; } = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("testsettings.json")
        .AddJsonFile("testsettings.Development.json", optional: true)
        .Build();

    protected static string BaseUrl => Config["Api:BaseUrl"]!; // http://localhost:5200

    // "Restricted" needs a role+user fixture the demo doesn't seed (see TestFixturesController).
    // PH2 review fix #2: seeding used to be triggered from HERE, once per GetClientAsync("Restricted")
    // call -- with [assembly: Parallelize(Scope = ExecutionScope.MethodLevel)] (MSTestSettings.cs)
    // running tests concurrently, two Restricted tests could both hit the seeder's check-then-act
    // window at once. Seeding now happens exactly ONCE, before any [TestMethod] runs, in
    // AssemblyFixture.SeedRestrictedRoleAsync below -- this method only logs on.
    // internal (not protected): AssemblyFixture is a sibling class in this same assembly, not a
    // TestBase subclass, and needs to call this too.
    internal static async Task<HttpClient> GetClientAsync(string userKey) {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var response = await client.PostAsJsonAsync("api/Authentication/Authenticate",
            new { userName = Config[$"Test:{userKey}User"], password = Config[$"Test:{userKey}Password"] });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadAsStringAsync()).Trim('"');
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

// PH2 review fix #2: seeds the Restricted-role/user fixture once for the whole test run. MSTest
// guarantees AssemblyInitialize methods run single-threaded before any [TestMethod] (including the
// MethodLevel-parallel ones), so there is no concurrent seeding window left to race.
[TestClass]
public class AssemblyFixture {
    [AssemblyInitialize]
    public static async Task SeedRestrictedRoleAsync(TestContext _) {
        var admin = await TestBase.GetClientAsync("Admin");
        (await admin.PostAsync("api/test-fixtures/restricted-role", null)).EnsureSuccessStatusCode();
    }
}
