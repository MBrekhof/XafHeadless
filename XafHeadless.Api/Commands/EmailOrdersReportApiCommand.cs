using DevExpress.ExpressApp;
using Hangfire;

namespace XafHeadless.Api.Commands;

// SVR-001 Task 3.4: enqueues the JobServer's EmailOrdersReport job into the shared Hangfire SQL storage.
// Fire-and-forget: the JobExecutionRecord does not exist until the JobServer worker picks the job up, so
// there is no executionId to return synchronously. Recipient is a fixed admin-owned demo value (per the
// design's trust model), NOT user-suppliable per request.
public class EmailOrdersReportApiCommand(IBackgroundJobClient jobClient) : IHeadlessCommand {
    public string Id => "EmailOrdersReport";

    public CommandResult Execute(IObjectSpace os, string[] objectKeys) {
        jobClient.Enqueue<XafHeadless.JobServer.Jobs.JobExecutor<XafHeadless.JobServer.Jobs.EmailOrdersReportCommand>>(
            executor => executor.RunAsync(
                new XafHeadless.JobServer.Jobs.EmailOrdersReportCommand("demo-recipient@xafheadless.local"),
                CancellationToken.None));
        return new CommandResult(true, "Job enqueued.", []);
    }
}
