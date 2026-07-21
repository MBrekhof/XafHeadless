using Hangfire;
using System.Text.Json;

namespace XafHeadless.JobServer.Jobs;

// Ported from a companion headless implementation's job executor (namespace only). The Hangfire entry point for every
// command type: initialize the scope, record start, run the handler, record completion/failure.
public sealed class JobExecutor<TCommand>(
    IJobHandler<TCommand> handler,
    IJobScopeInitializer scopeInitializer,
    IJobExecutionRecorder executionRecorder,
    IJobProgressReporter progressReporter,
    ILogger<JobExecutor<TCommand>> logger) {
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(TCommand command, CancellationToken cancellationToken) {
        await scopeInitializer.InitializeAsync(cancellationToken);

        string? parametersJson = null;
        try { parametersJson = JsonSerializer.Serialize(command); }
        catch { /* non-critical */ }

        var recordId = await executionRecorder.RecordStartAsync(
            typeof(TCommand).Name, typeof(TCommand).FullName ?? typeof(TCommand).Name,
            parametersJson, cancellationToken);

        progressReporter.Initialize(recordId);

        try {
            logger.LogInformation("Executing job {JobType} (record {RecordId})", typeof(TCommand).Name, recordId);
            await handler.ExecuteAsync(command, cancellationToken);
            await SafeRecordCompletionAsync(recordId, cancellationToken);
            logger.LogInformation("Job {JobType} completed (record {RecordId})", typeof(TCommand).Name, recordId);
        }
        catch (Exception ex) {
            logger.LogError(ex, "Job {JobType} failed (record {RecordId})", typeof(TCommand).Name, recordId);
            await SafeRecordFailureAsync(recordId, ex.ToString(), cancellationToken);
            throw;
        }
    }

    // I-1 (SVR-001 Dispatch F carry-forward): bookkeeping must be best-effort. A handler like
    // EmailOrdersReportHandler has real, non-idempotent side effects (an SMTP send) that finish before
    // this call runs -- if RecordCompletionAsync/RecordFailureAsync threw and that exception propagated,
    // this method's own catch would (for the completion case) misreport a false Failure and rethrow,
    // triggering a Hangfire retry that re-runs a handler whose send already succeeded. Swallow (logged)
    // instead; the handler's own success/failure still drives retry via the surrounding try/catch --
    // only the recorder calls become non-throwing.
    async Task SafeRecordCompletionAsync(Guid recordId, CancellationToken cancellationToken) {
        try { await executionRecorder.RecordCompletionAsync(recordId, cancellationToken); }
        catch (Exception ex) { logger.LogError(ex, "RecordCompletionAsync failed for record {RecordId} (best-effort, not rethrown)", recordId); }
    }

    async Task SafeRecordFailureAsync(Guid recordId, string errorMessage, CancellationToken cancellationToken) {
        try { await executionRecorder.RecordFailureAsync(recordId, errorMessage, cancellationToken); }
        catch (Exception ex) { logger.LogError(ex, "RecordFailureAsync failed for record {RecordId} (best-effort, not rethrown)", recordId); }
    }
}
