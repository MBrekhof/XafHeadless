using Microsoft.Extensions.Logging.Abstractions;
using XafHeadless.JobServer.Jobs;

namespace XafHeadless.JobServer.Tests;

// SVR-001 Dispatch F's M-4 recipient guard, committed properly here -- the F implementer only ran this
// throwaway. Pure unit test, no running host: JobDispatchService.Deserialize
// is private+static, but the public DispatchByNameAsync calls it before dispatching, so this drives it
// through that. A stub IJobDispatcher (no-op methods) means no Hangfire/host is needed at all.
[TestClass]
public class M4RecipientGuardTests {
    // The exact 6-fail/1-pass matrix verified during that work.
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("{}")]
    [DataRow("""{"EmailRecipients":null}""")]
    [DataRow("""{"EmailRecipients":"  "}""")]
    public async Task DispatchByNameAsync_throws_when_EmailRecipients_is_missing_or_blank(string? json) {
        var svc = new JobDispatchService(new StubJobDispatcher(), NullLogger<JobDispatchService>.Instance);
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => svc.DispatchByNameAsync(JobDispatchService.EmailOrdersReport, json, default));
    }

    [TestMethod]
    public async Task DispatchByNameAsync_does_not_throw_when_EmailRecipients_is_present() {
        var svc = new JobDispatchService(new StubJobDispatcher(), NullLogger<JobDispatchService>.Instance);
        await svc.DispatchByNameAsync(JobDispatchService.EmailOrdersReport, """{"EmailRecipients":"a@b.com"}""", default);
    }

    sealed class StubJobDispatcher : IJobDispatcher {
        public Task DispatchAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default) where TCommand : notnull
            => Task.CompletedTask;
        public void Schedule<TCommand>(TCommand command, string cronExpression, string jobId) where TCommand : notnull { }
        public void RemoveSchedule(string jobId) { }
    }
}
