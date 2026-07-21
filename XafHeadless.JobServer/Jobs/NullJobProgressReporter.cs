namespace XafHeadless.JobServer.Jobs;

// Ported from a companion headless implementation's null job progress reporter (namespace only).
public sealed class NullJobProgressReporter : IJobProgressReporter {
    public void Initialize(Guid executionRecordId) { }
    public Task ReportAsync(int percentComplete, string? message = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
