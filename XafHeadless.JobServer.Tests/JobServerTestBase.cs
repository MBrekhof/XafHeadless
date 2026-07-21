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

    protected static async Task<DateTime?> LatestExecutionRecordStartedUtcAsync(HttpClient api) {
        // NO $select -- SVR-003 (throws ArgumentNullException(edmModel) on host-shared types).
        var page = await api.GetFromJsonAsync<JsonElement>(
            "api/odata/JobExecutionRecord?$orderby=StartedUtc desc&$top=1");
        var values = page.GetProperty("value");
        return values.GetArrayLength() > 0 ? values[0].GetProperty("StartedUtc").GetDateTime() : null;
    }

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
    protected static async Task<JsonElement> RunNowAsync(HttpClient api) {
        var resp = await api.PostAsJsonAsync("api/commands/EmailOrdersReport", new { ObjectKeys = Array.Empty<string>() });
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"expected 200, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.GetProperty("Success").GetBoolean(), "EmailOrdersReport command should report Success=true");
        return body;
    }

    // Poll (bounded, ~30s budget) for a NEW JobExecutionRecord (StartedUtc > baseline, or any if baseline
    // is null) to reach Status=Success. Returns null if none appeared within the budget.
    protected static async Task<JsonElement?> WaitForNewSuccessAsync(
            HttpClient api, DateTime? baseline, int attempts = 30, int delayMs = 1000) {
        for (var attempt = 0; attempt < attempts; attempt++) {
            await Task.Delay(delayMs);
            var page = await api.GetFromJsonAsync<JsonElement>(
                "api/odata/JobExecutionRecord?$orderby=StartedUtc desc&$top=1");
            var values = page.GetProperty("value");
            if (values.GetArrayLength() > 0) {
                var record = values[0];
                var startedUtc = record.GetProperty("StartedUtc").GetDateTime();
                var isNew = baseline is null || startedUtc > baseline;
                if (isNew && IsSuccessStatus(record.GetProperty("Status"))) return record.Clone();
            }
        }
        return null;
    }
}
