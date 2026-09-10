using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.JobServer.Tests;

// SVR-001 Task 5.2 phase gate, automated. Divergence from the companion headless implementation's
// schedule sync test: this design enforces exactly one JobDefinition row per JobTypeName (SVR-002's
// unique index), so this test operates on the SEEDED row -- find, never create/delete a marker row
// like the companion implementation does (that row is the demo's one real JobDefinition, not a
// throwaway). Always restores IsEnabled:false (and the seed's
// default CronExpression) in `finally` so nothing stays scheduled in the shared dev DB.
[TestClass]
[DoNotParallelize]
public class ScheduleSyncTests : JobServerTestBase {
    // 2x the JobServer's committed Jobs:ScheduleSyncSeconds (15) + slack: two full ticks must pass.
    const int SyncWaitSeconds = 40;

    [TestMethod]
    public async Task Enabled_seeded_definition_gets_scheduled_and_disabling_removes_it() {
        var api = await ApiClientAsync("Admin");

        // NO $select -- SVR-003 (throws edmModel on host-shared types). Exactly one JobDefinition row
        // exists by design (SVR-002's unique index on JobTypeName), so value[0] IS the seeded row.
        // TEST-004: the exact seeded row by JobTypeName, not rows[0] -- and a snapshot of every property this
        // test overwrites, restored verbatim in `finally`. The old "restore" wrote hardcoded seed values and
        // never put ParametersJson back, on the demo's one real definition.
        var listing = await api.GetFromJsonAsync<JsonElement>(
            "api/odata/JobDefinition?$filter=JobTypeName eq 'EmailOrdersReport'");
        var rows = listing.GetProperty("value");
        Assert.AreEqual(1, rows.GetArrayLength(), "expected exactly the seeded EmailOrdersReport JobDefinition row");
        var seeded = rows[0];
        var id = seeded.GetProperty("ID").GetString()!;
        var snapshot = new Dictionary<string, object?> {
            ["CronExpression"] = seeded.GetProperty("CronExpression").GetString(),
            ["IsEnabled"] = seeded.GetProperty("IsEnabled").GetBoolean(),
            ["ParametersJson"] = seeded.GetProperty("ParametersJson").GetString(),
        };

        try {
            // Codex review (TEST-004 follow-up): a seed that was ALREADY enabled with a populated NextRunUtc
            // would satisfy "enable -> non-null" without any reconciliation happening. So: disable and prove
            // removal first, then enable and prove registration, then disable and prove removal again.
            await SetAsync(api, id, enabled: false, cron: "0 3 * * *");
            Assert.IsNull(await PollNextRunUtcAsync(api, id, until: v => v is null),
                $"NextRunUtc still set {SyncWaitSeconds}s after disabling -- the schedule was not removed");

            // Include ParametersJson so the M-4 cron guard doesn't abort the sync tick (docs/DEVIATIONS.md
            // "Dispatch G": an enabled cron row with null ParametersJson makes SyncScheduleByName throw
            // and NextRunUtc never fills). Daily 03:00 -- never fires during a test run.
            await SetAsync(api, id, enabled: true, cron: "0 3 * * *");
            Assert.IsNotNull(await PollNextRunUtcAsync(api, id, until: v => v is not null),
                $"NextRunUtc stayed null for {SyncWaitSeconds}s -- ScheduleSyncService did not register the recurring job");

            await SetAsync(api, id, enabled: false, cron: "0 3 * * *");
            Assert.IsNull(await PollNextRunUtcAsync(api, id, until: v => v is null),
                $"NextRunUtc still set {SyncWaitSeconds}s after disabling -- the schedule was not removed");
        } finally {
            // Restore the snapshot verbatim. Do NOT delete the row -- it is the demo's one real definition.
            var restore = await api.PostAsJsonAsync($"api/save/JobDefinition/{id}", snapshot);
            Assert.AreEqual(HttpStatusCode.OK, restore.StatusCode,
                $"restore failed -- the seeded definition may be left in the test's state! {restore.StatusCode}");
        }
    }

    static async Task SetAsync(HttpClient api, string id, bool enabled, string cron) {
        var resp = await api.PostAsJsonAsync($"api/save/JobDefinition/{id}", new Dictionary<string, object?> {
            ["CronExpression"] = cron,
            ["IsEnabled"] = enabled,
            ["ParametersJson"] = """{"EmailRecipients":"demo-recipient@xafheadless.local"}""",
        });
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"update (IsEnabled={enabled}) failed: {resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    static async Task<DateTime?> PollNextRunUtcAsync(HttpClient api, string id, Func<DateTime?, bool> until) {
        DateTime? last = null;
        for (var i = 0; i < SyncWaitSeconds; i++) {
            await Task.Delay(1000);
            // Single-entity ({id}) path, NO $select -- the companion implementation's $select=NextRunUtc throws here (SVR-003).
            var row = await api.GetFromJsonAsync<JsonElement>($"api/odata/JobDefinition({id})");
            var value = row.GetProperty("NextRunUtc");
            // NextRunUtc serializes with a local offset -- cosmetic, presence is what matters.
            last = value.ValueKind == JsonValueKind.Null ? null
                : DateTimeOffset.Parse(value.GetString()!).DateTime;
            if (until(last)) return last;
        }
        return last;
    }
}
