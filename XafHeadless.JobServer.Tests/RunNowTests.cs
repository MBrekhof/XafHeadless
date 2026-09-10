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
    public async Task RunNow_EmailOrdersReport_enqueues_and_reaches_Success_with_exactly_one_record() {
        var api = await ApiClientAsync("Admin");

        var correlationId = await RunNowAsync(api);

        var success = await WaitForSuccessAsync(api, correlationId);
        Assert.IsNotNull(success, $"the JobExecutionRecord for run {correlationId} should have reached Status=Success within 30s");

        // I-1 (docs/DEVIATIONS.md SVR-001 Dispatch F): this asserts the happy-path no-duplicate case
        // ONLY -- one enqueue produced exactly one execution record; a spurious Hangfire retry would run
        // the same command again and produce a second record with the SAME correlation id. The FULL
        // at-most-once guarantee (no double-send when post-send bookkeeping itself fails) is a documented
        // residual ceiling, provable only via live SMTP fault injection -- not automatable here.
        Assert.HasCount(1, await RecordsForAsync(api, correlationId),
            "expected exactly one JobExecutionRecord for this run -- a spurious retry would produce a second");
    }
}
