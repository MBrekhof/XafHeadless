using System.Globalization;
using DevExpress.ExpressApp;
using Hangfire;
using XafHeadless.JobServer.BusinessObjects;

namespace XafHeadless.JobServer.Jobs;

// SVR-001 Task 5.1 (cron sync): reconciles JobDefinition rows into Hangfire recurring jobs on startup
// and every Jobs:ScheduleSyncSeconds (default 15). Adapted from a companion headless implementation's
// schedule sync service, trimmed to this host's single supported+handled job type: per-row
// JobDispatchService.SyncScheduleByName (no SyncDefinitions here; correct because SVR-002's unique
// index enforces one JobDefinition row per JobTypeName) and no HandledJobTypes cross-check. A sync
// failure logs and retries next tick -- a transient DB/storage error must not kill the host.
public sealed class ScheduleSyncService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ScheduleSyncService> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Jobs:ScheduleSyncSeconds", 15));
        using var timer = new PeriodicTimer(interval);
        do {
            try { SyncOnce(); }
            catch (Exception ex) {
                logger.LogError(ex, "JobDefinition schedule sync failed; retrying on the next tick");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct) {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    void SyncOnce() {
        using var scope = scopeFactory.CreateScope();
        var objectSpaceFactory = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>();
        var dispatch = scope.ServiceProvider.GetRequiredService<JobDispatchService>();
        using var objectSpace = objectSpaceFactory.CreateNonSecuredObjectSpace<JobDefinition>();
        var definitions = objectSpace.GetObjects<JobDefinition>().ToList();

        var dirty = false;
        foreach (var d in definitions) {
            if (!JobDispatchService.SupportedJobTypes.Contains(d.JobTypeName)) {
                logger.LogWarning("JobDefinition '{Name}' has unsupported JobTypeName '{Type}' -- not scheduled",
                    d.Name, d.JobTypeName);
                continue;
            }
            dispatch.SyncScheduleByName(d.JobTypeName, d.ParametersJson, d.CronExpression, d.IsEnabled);
            var next = d.IsEnabled && !string.IsNullOrWhiteSpace(d.CronExpression) ? ReadNextRunUtc() : null;
            if (d.NextRunUtc != next) { d.NextRunUtc = next; dirty = true; }
        }
        if (dirty) objectSpace.CommitChanges();
        logger.LogDebug("Schedule sync reconciled {Count} JobDefinition rows", definitions.Count);
    }

    // Reads Hangfire's own recurring-job state for the single recurring job. On the live SqlServer
    // storage (schema V2) NextExecution is Unix epoch MILLISECONDS -- parse epoch-ms FIRST, tolerate the
    // ISO string form as a fallback. (Documented in docs/DEVIATIONS.md; same handling the port source uses.)
    DateTime? ReadNextRunUtc() {
        try {
            if (JobStorage.Current is null) return null;
            using var connection = JobStorage.Current.GetConnection();
            var hash = connection.GetAllEntriesFromHash($"recurring-job:{JobDispatchService.RecurringJobId}");
            if (hash is null || !hash.TryGetValue("NextExecution", out var raw) || string.IsNullOrWhiteSpace(raw))
                return null;
            if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var epochMs))
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToUniversalTime() : null;
        }
        catch (Exception ex) {
            logger.LogWarning(ex, "Unable to read NextRunUtc");
            return null;
        }
    }
}
