namespace XafHeadless.JobServer.Jobs;

// SVR-001: the one command this host dispatches. Renders OrdersReport and emails it to EmailRecipients.
public sealed record EmailOrdersReportCommand(string EmailRecipients);
