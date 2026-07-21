namespace XafHeadless.JobServer.Jobs;

// Ported from a companion headless implementation's job progress reporter interface (namespace only).
public interface IJobProgressReporter {
    void Initialize(Guid executionRecordId);
    Task ReportAsync(int percentComplete, string? message = null, CancellationToken cancellationToken = default);
}
