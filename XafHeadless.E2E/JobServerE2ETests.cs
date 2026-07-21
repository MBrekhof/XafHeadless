using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// SVR-001 Task 6.2: automates exactly the flow SVR-003's manual browser smoke proved by hand
// log in -> open the seeded JobDefinition's DetailView ->
// click Run Now -> a NEW JobExecutionRecord reaches Status=Success -- all read through the real
// client. This exercises both the command-trigger wiring (Dispatch H's "Run Now" button) and the
// host-shared-BO OData read path (SVR-003's fix), the two things that had to land before this test
// could exist at all.
//
// EXTRA HOST REQUIREMENT beyond SmokeTests.cs's Api(:5200)+Web(:5220) pair:
//  - JobServer (:5300) must also be running -- it's the Hangfire WORKER that actually executes the
//    enqueued EmailOrdersReport job. Without it the JobExecutionRecord never leaves Running (or never
//    appears at all).
//  - The `xafheadless-smtp` smtp4dev container (2526/5312) must be up -- Run Now sends a REAL email
//    (Dispatch F); EmailService fails the job if the SMTP port is unreachable. `docker start
//    xafheadless-smtp` if it isn't already (use a distinct port if you run other smtp4dev containers).
// Same DATA SAFETY / recovery note as SmokeTests.cs's header applies (disposable dev data); this file
// pins nothing new -- the JobDefinition key and the Success record are both discovered live every run
// (no testsettings.json key), so a dropped/reseeded DB just changes which GUIDs these reads return.
//
// SVR-003 constraint -- NEVER send $select on JobDefinition/JobExecutionRecord OData reads (still
// throws ArgumentNullException(edmModel) on these host-shared types, deferred DX bug). $top/$orderby/
// ({key}) all work. Do NOT copy PlaywrightFixture.ReadEmployeeFirstNameAsync's $select shape here --
// that's on Employee, a per-tenant type, unaffected by this bug.
[TestClass]
public class JobServerE2ETests : PlaywrightFixture {
    const string DataRows = ".dxbl-grid-table tbody tr:not(.dxbl-grid-empty-row)";
    // h3 caption, confirmed live against the running client (SVR-003 fix report, Gate 4).
    const string JobDefinitionDetailCaption = "Job Definition";

    [TestMethod]
    public async Task RunNow_enqueues_and_a_JobExecutionRecord_reaches_Success() {
        await LoginAsync();

        using var api = await ApiClientAsync();

        // The seeded JobDefinition's key (no $select -- SVR-003) + a baseline of the newest EXISTING
        // JobExecutionRecord, if any. The baseline is what lets the poll below tell THIS run's record
        // apart from one a prior smoke/dispatch already left behind (both share JobTypeName, so
        // JobTypeName alone can't distinguish them -- StartedUtc newer-than-baseline can).
        var jobDefResp = await api.GetAsync("api/odata/JobDefinition");
        jobDefResp.EnsureSuccessStatusCode();
        string jobDefKey;
        using (var jobDefDoc = JsonDocument.Parse(await jobDefResp.Content.ReadAsStringAsync())) {
            var values = jobDefDoc.RootElement.GetProperty("value");
            Assert.IsTrue(values.GetArrayLength() > 0, "expected the seeded JobDefinition row");
            jobDefKey = values[0].GetProperty("ID").GetString()!;
        }
        var baselineStartedUtc = await LatestExecutionRecordStartedUtcAsync(api);

        // 1. JobDefinition_ListView renders the seeded row -- proves the SVR-003 $top read fix in the
        //    real client (not just against a bare OData probe).
        await NavigateSpa("/list/JobDefinition_ListView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/JobDefinition_ListView$"), new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows).First).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows)).Not.ToHaveCountAsync(0, new() { Timeout = 15000 });
        await Shot("J-01-jobdef-list");

        // 2. DetailView by key -- the other half of the SVR-003 fix ($filter=ID eq). The Run Now
        //    button (Dispatch H) is gated to render only here.
        await NavigateSpa($"/detail/JobDefinition_DetailView/{jobDefKey}");
        await Expect(Page.Locator("h3")).ToHaveTextAsync(JobDefinitionDetailCaption, new() { Timeout = 15000 });
        var runNow = Page.GetByRole(AriaRole.Button, new() { Name = "Run Now", Exact = true });
        await Expect(runNow).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Shot("J-02-jobdef-detail-runnow");

        // 3. Click Run Now -> the command's own success message (EmailOrdersReportApiCommand.Execute).
        await runNow.ClickAsync();
        var success = Page.Locator(".alert-success");
        await Expect(success).ToContainTextAsync("Job enqueued.", new() { Timeout = 10000 });
        await Shot("J-03-enqueued");

        // 4. Poll (bounded -- 30 x 1s, ~30s budget; NOT a fixed sleep) for a NEW JobExecutionRecord to
        //    reach Success. The job renders a PDF + sends a real email -- SVR-003's fix report measured
        //    DurationMs 2,839 for the same command, so 30s leaves ample slack.
        JsonElement? successRecord = null;
        for (var attempt = 0; attempt < 30 && successRecord is null; attempt++) {
            var resp = await api.GetAsync("api/odata/JobExecutionRecord?$orderby=StartedUtc desc&$top=1");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var values = doc.RootElement.GetProperty("value");
            if (values.GetArrayLength() > 0) {
                var record = values[0];
                var startedUtc = record.GetProperty("StartedUtc").GetDateTime();
                var isNew = baselineStartedUtc is null || startedUtc > baselineStartedUtc;
                if (isNew && IsSuccessStatus(record.GetProperty("Status"))) successRecord = record.Clone();
            }
            if (successRecord is null) await Task.Delay(1000);
        }
        Assert.IsNotNull(successRecord, "a NEW JobExecutionRecord should have reached Status=Success within 30s");
        Console.WriteLine(
            $"JobExecutionRecord Success -- StartedUtc={successRecord!.Value.GetProperty("StartedUtc").GetString()}, " +
            $"DurationMs={successRecord.Value.GetProperty("DurationMs").GetInt64()}");

        // 5. The client also renders it -- proves the SVR-003 fix for JobExecutionRecord too, not just
        //    JobDefinition.
        await NavigateSpa("/list/JobExecutionRecord_ListView");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/list/JobExecutionRecord_ListView$"), new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows).First).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(Page.Locator(DataRows)).Not.ToHaveCountAsync(0, new() { Timeout = 15000 });
        await Shot("J-04-execrecord-list");
    }

    static async Task<DateTime?> LatestExecutionRecordStartedUtcAsync(HttpClient api) {
        var resp = await api.GetAsync("api/odata/JobExecutionRecord?$orderby=StartedUtc desc&$top=1");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var values = doc.RootElement.GetProperty("value");
        return values.GetArrayLength() > 0 ? values[0].GetProperty("StartedUtc").GetDateTime() : null;
    }

    // JobRunStatus (NeverRun/Running/Success/Failed) is a plain C# enum on the host-shared BO; whether
    // the OData JSON serializer renders it as its string name or its int ordinal is a framework
    // decision, not something to guess -- handle both rather than assume one.
    static bool IsSuccessStatus(JsonElement status) => status.ValueKind switch {
        JsonValueKind.String => status.GetString() == "Success",
        JsonValueKind.Number => status.GetInt32() == 2, // JobRunStatus.Success ordinal
        _ => false,
    };
}
