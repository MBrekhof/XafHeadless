namespace XafHeadless.JobServer.Jobs;

// Ported from a companion headless implementation's job dispatcher interface (namespace only). The no-jobId Schedule overload
// is dropped: JobDispatchService.SyncScheduleByName always passes an explicit RecurringJobId (Phase 5).
public interface IJobDispatcher {
    Task DispatchAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : notnull;

    void Schedule<TCommand>(TCommand command, string cronExpression, string jobId)
        where TCommand : notnull;

    void RemoveSchedule(string jobId);
}
