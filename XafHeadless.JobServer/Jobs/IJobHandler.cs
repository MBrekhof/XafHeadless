namespace XafHeadless.JobServer.Jobs;

// Ported from a companion headless implementation's job handler interface (namespace only).
public interface IJobHandler<in TCommand> {
    Task ExecuteAsync(TCommand command, CancellationToken cancellationToken = default);
}
