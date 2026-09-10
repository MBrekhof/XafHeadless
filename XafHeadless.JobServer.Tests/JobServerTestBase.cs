using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace XafHeadless.JobServer.Tests;

// Adapted from a companion headless implementation's job server test base (READ-ONLY reference), but
// structurally different: the JobServer here exposes only an anonymous /health endpoint -- nothing
// authenticated. ALL authenticated calls (Run Now, OData reads, save PATCH) go to the Api (:5200),
// which mints the JWT. The companion implementation's "reuse the Api token on the JobServer"
// cross-host pattern is therefore unnecessary (the shared-JWT-key concern is moot -- nothing hits the
// JobServer's authed surface, because it has none).
public abstract class JobServerTestBase {
    protected static IConfiguration Config { get; } = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("testsettings.json")
        .AddJsonFile("testsettings.Development.json", optional: true)
        .Build();

    protected static string ApiUrl => Config["Api:BaseUrl"]!;               // http://localhost:5200
    protected static string JobServerUrl => Config["JobServer:BaseUrl"]!;   // http://localhost:5300
    protected static string Smtp4devUrl => Config["Smtp4dev:BaseUrl"]!;     // http://localhost:5312
    protected static string HostConnectionString => Config["ConnectionStrings:Host"]!;

    // Same shape as XafHeadless.Api.Tests\TestBase.GetClientAsync.
    protected static async Task<HttpClient> ApiClientAsync(string userKey = "Admin") {
        var client = new HttpClient { BaseAddress = new Uri(ApiUrl) };
        var response = await client.PostAsJsonAsync("api/Authentication/Authenticate",
            new { userName = Config[$"Test:{userKey}User"], password = Config[$"Test:{userKey}Password"] });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadAsStringAsync()).Trim('"');
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // /health is anonymous -- no auth needed.
    protected static HttpClient JobServerHealthClient() => new() { BaseAddress = new Uri(JobServerUrl) };

    // Shared by RunNowTests/ReportRenderTests/EmailDeliveryTests -- all three trigger the same Api
    // command and wait for the same JobExecutionRecord Success signal (kept DRY on purpose).

    // JobRunStatus (NeverRun/Running/Success/Failed) is a plain C# enum on the host-shared BO; whether the
    // OData JSON serializer renders it as its string name or its int ordinal is a framework decision, not
    // something to guess -- handle both. Copied from XafHeadless.E2E\JobServerE2ETests.cs.
    protected static bool IsSuccessStatus(JsonElement status) => status.ValueKind switch {
        JsonValueKind.String => status.GetString() == "Success",
        JsonValueKind.Number => status.GetInt32() == 2, // JobRunStatus.Success ordinal
        _ => false,
    };

    // Divergence from the companion headless implementation: Run Now here is the Api's generic command endpoint, NOT a JobServer
    // api/jobs/run/{type} route (that route does not exist in this design).
    // TEST-004: returns the run's correlation id -- the Api mints it, the worker stamps it on the
    // JobExecutionRecord and the ReportArtifact, and every assertion below keys on it. "The latest record"
    // was satisfiable by a scheduled or manual run from anywhere else on the shared demo catalog.
    protected static async Task<Guid> RunNowAsync(HttpClient api) {
        var resp = await api.PostAsJsonAsync("api/commands/EmailOrdersReport", new { ObjectKeys = Array.Empty<string>() });
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"expected 200, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.GetProperty("Success").GetBoolean(), "EmailOrdersReport command should report Success=true");
        return body.GetProperty("CorrelationId").GetGuid();
    }

    // All JobExecutionRecords carrying this run's correlation id. NO $select (SVR-003); $filter on a Guid
    // member of a host-shared type is the shape SVR-003 fixed and verified live ($filter=ID eq <guid>).
    protected static async Task<JsonElement[]> RecordsForAsync(HttpClient api, Guid correlationId) {
        var page = await api.GetFromJsonAsync<JsonElement>(
            $"api/odata/JobExecutionRecord?$filter=CorrelationId eq {correlationId}");
        return page.GetProperty("value").EnumerateArray().Select(r => r.Clone()).ToArray();
    }

    // Poll (bounded, ~30s budget) for THIS run's JobExecutionRecord to reach Status=Success. Returns null
    // if it did not within the budget -- including when it reached Failed, which the caller's message
    // should distinguish by reading RecordsForAsync itself.
    protected static async Task<JsonElement?> WaitForSuccessAsync(
            HttpClient api, Guid correlationId, int attempts = 30, int delayMs = 1000) {
        for (var attempt = 0; attempt < attempts; attempt++) {
            await Task.Delay(delayMs);
            var hit = (await RecordsForAsync(api, correlationId)).FirstOrDefault(r => IsSuccessStatus(r.GetProperty("Status")));
            if (hit.ValueKind != JsonValueKind.Undefined) return hit;
        }
        return null;
    }
}
