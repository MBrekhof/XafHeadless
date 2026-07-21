namespace XafHeadless.JobServer.Jobs;

// Ported from a companion headless implementation's job execution recorder interface (namespace only).
public interface IJobExecutionRecorder {
    Task<Guid> RecordStartAsync(string jobName, string jobTypeName, string? parametersJson, CancellationToken cancellationToken = default);
    Task RecordCompletionAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task RecordFailureAsync(Guid recordId, string errorMessage, CancellationToken cancellationToken = default);
}
