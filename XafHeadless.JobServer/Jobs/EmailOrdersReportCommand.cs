namespace XafHeadless.JobServer.Jobs;

// SVR-001: the one command this host dispatches. Renders OrdersReport and emails it to EmailRecipients.
// TEST-004: CorrelationId is chosen by whoever enqueues (the Api's Run Now command) so the caller can find
// THIS run's JobExecutionRecord and ReportArtifact instead of "the latest one". Scheduled runs come from
// ParametersJson, which carries no id -> Guid.Empty -> recorded as null.
public sealed record EmailOrdersReportCommand(string EmailRecipients, Guid CorrelationId = default) : IJobCorrelation;

// A command whose run can be found again by an id the enqueuer chose. Guid.Empty means "none".
public interface IJobCorrelation { Guid CorrelationId { get; } }
