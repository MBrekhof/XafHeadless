using Hangfire;

namespace XafHeadless.JobServer.Jobs;

// Ported from a companion headless implementation's Hangfire job dispatcher (namespace only;
// no-jobId Schedule overload dropped -- see IJobDispatcher). Enqueues / schedules a JobExecutor<TCommand> on Hangfire.
public sealed class HangfireJobDispatcher(
    IBackgroundJobClient jobClient,
    ILogger<HangfireJobDispatcher> logger) : IJobDispatcher {
    public Task DispatchAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : notnull {
        var jobId = jobClient.Enqueue<JobExecutor<TCommand>>(
            executor => executor.RunAsync(command, CancellationToken.None));
        logger.LogInformation("Dispatched {CommandType} as Hangfire job {JobId}", typeof(TCommand).Name, jobId);
        return Task.CompletedTask;
    }

    public void Schedule<TCommand>(TCommand command, string cronExpression, string jobId)
        where TCommand : notnull {
        RecurringJob.AddOrUpdate<JobExecutor<TCommand>>(
            jobId,
            executor => executor.RunAsync(command, CancellationToken.None),
            cronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
        logger.LogInformation("Scheduled {CommandType} as '{JobId}' with cron '{Cron}'", typeof(TCommand).Name, jobId, cronExpression);
    }

    public void RemoveSchedule(string jobId) {
        RecurringJob.RemoveIfExists(jobId);
        logger.LogInformation("Removed recurring job '{JobId}'", jobId);
    }
}
