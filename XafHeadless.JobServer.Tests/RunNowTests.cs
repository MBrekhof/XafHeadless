using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.JobServer.Tests;

// SVR-001 Task 3.4 / Dispatch H phase gate, automated. Divergence from the companion headless
// implementation's Run Now test: Run Now here is the Api's generic command endpoint (POST api/commands/
// EmailOrdersReport), NOT a JobServer api/jobs/run/{type} route -- that route does not exist in this
// design.
//
// The companion implementation's RunNow_is_admin_gated 403 test is deliberately NOT ported here. Owner decision (2026-07-21,
// docs/DEVIATIONS.md "auth posture for EmailOrdersReport"): command endpoints stay [Authorize]-only, no
// admin gate -- a Restricted user gets 200 here and successfully enqueues (a 403 assertion would FAIL).
// Restricted-403 coverage for JobDefinition *writes* (not command execution) already lives in
// XafHeadless.Api.Tests\SaveDeleteTests.cs (Restricted_role_is_denied_403_on_all_JobDefinition_write_operations).
[TestClass]
[DoNotParallelize]
public class RunNowTests : JobServerTestBase {
    [TestMethod]
    public async Task RunNow_EmailOrdersReport_enqueues_and_reaches_Success_with_exactly_one_new_record() {
        var api = await ApiClientAsync("Admin");
        var baseline = await LatestExecutionRecordStartedUtcAsync(api);

        await RunNowAsync(api);

        var success = await WaitForNewSuccessAsync(api, baseline);
        Assert.IsNotNull(success, "a NEW JobExecutionRecord should have reached Status=Success within 30s");

        // I-1 (docs/DEVIATIONS.md SVR-001 Dispatch F): this asserts the happy-path no-duplicate case
        // ONLY -- one enqueue produced exactly one execution record since baseline; a spurious Hangfire
        // retry would produce a second. The FULL at-most-once guarantee (no double-send when post-send
        // bookkeeping itself fails) is a documented residual ceiling, provable only via live SMTP fault
        // injection (Dispatch F did that by hand) -- not automatable here. Do not overclaim beyond this.
        var page = await api.GetFromJsonAsync<JsonElement>(
            "api/odata/JobExecutionRecord?$orderby=StartedUtc desc&$top=5");
        var newCount = page.GetProperty("value").EnumerateArray()
            .Count(r => baseline is null || r.GetProperty("StartedUtc").GetDateTime() > baseline);
        Assert.AreEqual(1, newCount,
            "expected exactly one new JobExecutionRecord since baseline -- a spurious retry would produce a second");
    }
}
