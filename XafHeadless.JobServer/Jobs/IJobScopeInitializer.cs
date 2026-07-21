namespace XafHeadless.JobServer.Jobs;

// Ported from a companion headless implementation's job scope initializer interface (namespace only).
public interface IJobScopeInitializer {
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
