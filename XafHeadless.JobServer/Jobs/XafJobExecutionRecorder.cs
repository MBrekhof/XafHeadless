using DevExpress.ExpressApp;
using XafHeadless.JobServer.BusinessObjects;

namespace XafHeadless.JobServer.Jobs;

// Adapted from a companion headless implementation's API-side job execution recorder. Records job start/completion/failure
// into JobExecutionRecord via INonSecuredObjectSpaceFactory, and keeps the parent JobDefinition's
// LastRunStatus/LastRunUtc in sync so the seeded row shows its own last-run state.
//
// This is trusted worker code, never reached via an HTTP request, so it needs no admin gate (same as
// PrefsController's host-write and the startup JobDefinition seed).
//
// TENANT ISOLATION (SVR-001, verified 26.1 source): these writes run in the OUTER Hangfire job scope,
// where ITenantProvider.TenantId stays null. JobExecutionRecord/JobDefinition are SHARED BOs, so the
// null-tenant scope routes CreateNonSecuredObjectSpace to the genuine host branch (writable host
// catalog). ReportRenderService deliberately sets TenantId only inside its OWN child DI scope
// (ITenantProvider is AddScoped), so its tenant selection never bleeds into these shared-BO writes and
// cannot flip them into the read-only-shared-data branch. See docs/DEVIATIONS.md.
public sealed class XafJobExecutionRecorder(
    INonSecuredObjectSpaceFactory objectSpaceFactory,
    ILogger<XafJobExecutionRecorder> logger) : IJobExecutionRecorder {
    // "EmailOrdersReportCommand" -> "EmailOrdersReport" (matches the seeded JobDefinition.JobTypeName).
    static string ToJobTypeName(string commandTypeName)
        => commandTypeName.EndsWith("Command", StringComparison.OrdinalIgnoreCase)
            ? commandTypeName[..^"Command".Length]
            : commandTypeName;

    public Task<Guid> RecordStartAsync(string jobName, string jobTypeName, string? parametersJson, CancellationToken cancellationToken = default) {
        using var objectSpace = objectSpaceFactory.CreateNonSecuredObjectSpace<JobExecutionRecord>();

        var record = objectSpace.CreateObject<JobExecutionRecord>();
        record.JobName = jobName;
        record.JobTypeName = ToJobTypeName(jobName);
        record.StartedUtc = DateTime.UtcNow;
        record.Status = JobRunStatus.Running;

        var jobDef = objectSpace.FirstOrDefault<JobDefinition>(d => d.JobTypeName == record.JobTypeName);
        if (jobDef != null) {
            jobDef.LastRunStatus = JobRunStatus.Running;
            jobDef.LastRunUtc = record.StartedUtc;
        }
        else {
            logger.LogWarning("RecordStartAsync: no JobDefinition found for JobTypeName '{Key}' (command: '{JobName}')", record.JobTypeName, jobName);
        }

        objectSpace.CommitChanges();
        logger.LogDebug("Recorded job start: {JobName} ({Id})", jobName, record.ID);
        return Task.FromResult(record.ID);
    }

    public Task RecordCompletionAsync(Guid recordId, CancellationToken cancellationToken = default) {
        using var objectSpace = objectSpaceFactory.CreateNonSecuredObjectSpace<JobExecutionRecord>();
        var record = objectSpace.FirstOrDefault<JobExecutionRecord>(r => r.ID == recordId);
        if (record != null) {
            record.Status = JobRunStatus.Success;
            record.CompletedUtc = DateTime.UtcNow;
            record.DurationMs = (long)(record.CompletedUtc.Value - record.StartedUtc).TotalMilliseconds;

            var jobDef = objectSpace.FirstOrDefault<JobDefinition>(d => d.JobTypeName == record.JobTypeName);
            if (jobDef != null) {
                jobDef.LastRunStatus = JobRunStatus.Success;
                jobDef.LastRunUtc = record.CompletedUtc;
            }
            objectSpace.CommitChanges();
        }
        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(Guid recordId, string errorMessage, CancellationToken cancellationToken = default) {
        using var objectSpace = objectSpaceFactory.CreateNonSecuredObjectSpace<JobExecutionRecord>();
        var record = objectSpace.FirstOrDefault<JobExecutionRecord>(r => r.ID == recordId);
        if (record != null) {
            record.Status = JobRunStatus.Failed;
            record.CompletedUtc = DateTime.UtcNow;
            record.DurationMs = (long)(record.CompletedUtc.Value - record.StartedUtc).TotalMilliseconds;
            record.ErrorMessage = errorMessage;

            var jobDef = objectSpace.FirstOrDefault<JobDefinition>(d => d.JobTypeName == record.JobTypeName);
            if (jobDef != null) {
                jobDef.LastRunStatus = JobRunStatus.Failed;
                jobDef.LastRunUtc = record.CompletedUtc;
            }
            objectSpace.CommitChanges();
        }
        return Task.CompletedTask;
    }
}
