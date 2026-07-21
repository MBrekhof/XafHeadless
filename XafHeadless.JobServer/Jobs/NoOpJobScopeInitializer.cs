namespace XafHeadless.JobServer.Jobs;

// SVR-001: every worker-scope write in this design goes through INonSecuredObjectSpaceFactory
// (report load, execution record, email archive) -- nothing downstream needs an authenticated
// object space, so there's nothing for a service-user logon to unlock. Registered only because
// JobExecutor<TCommand> requires an IJobScopeInitializer.
public sealed class NoOpJobScopeInitializer : IJobScopeInitializer {
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
