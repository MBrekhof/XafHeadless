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
        var listing = await api.GetFromJsonAsync<JsonElement>("api/odata/JobDefinition");
        var rows = listing.GetProperty("value");
        Assert.IsGreaterThan(0, rows.GetArrayLength(), "expected the seeded JobDefinition row");
        var id = rows[0].GetProperty("ID").GetString()!;

        try {
            // Include ParametersJson so the M-4 cron guard doesn't abort the sync tick (docs/DEVIATIONS.md
            // "Dispatch G": an enabled cron row with null ParametersJson makes SyncScheduleByName throw
            // and NextRunUtc never fills).
            var enable = await api.PostAsJsonAsync($"api/save/JobDefinition/{id}", new Dictionary<string, object?> {
                ["CronExpression"] = "0 3 * * *",   // daily 03:00 -- never fires during a test run
                ["IsEnabled"] = true,
                ["ParametersJson"] = """{"EmailRecipients":"demo-recipient@xafheadless.local"}""",
            });
            Assert.AreEqual(HttpStatusCode.OK, enable.StatusCode,
                $"enable update failed: {enable.StatusCode} {await enable.Content.ReadAsStringAsync()}");

            var next = await PollNextRunUtcAsync(api, id, until: v => v is not null);
            Assert.IsNotNull(next,
                $"NextRunUtc stayed null for {SyncWaitSeconds}s -- ScheduleSyncService did not register the recurring job");
        } finally {
            // ALWAYS end disabled -- nothing may stay scheduled in the shared dev DB. Reset the cron back
            // to the seed default for cleanliness. Do NOT delete the row -- it is the demo's one real
            // definition, not a throwaway marker.
            var disable = await api.PostAsJsonAsync($"api/save/JobDefinition/{id}", new Dictionary<string, object?> {
                ["IsEnabled"] = false,
                ["CronExpression"] = "0 7 * * *",
            });
            Assert.AreEqual(HttpStatusCode.OK, disable.StatusCode,
                $"disable update failed -- the seeded definition may still be scheduled! {disable.StatusCode}");
        }

        // Disabling must remove the Hangfire schedule -> NextRunUtc nulls on the next tick.
        var cleared = await PollNextRunUtcAsync(api, id, until: v => v is null);
        Assert.IsNull(cleared,
            $"NextRunUtc still set {SyncWaitSeconds}s after disabling -- the schedule was not removed");
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
