using XafHeadless.JobServer.Jobs;

namespace XafHeadless.JobServer.Tests;

// BUG-011: ScheduleSyncService only walked existing rows, so a deleted (or retyped) JobDefinition left its
// recurring job firing. Orphans() is the reconcile step, kept pure so this needs no host or object space.
[TestClass]
public class ScheduleOrphanTests {
    [TestMethod]
    public void No_rows_means_every_supported_type_is_an_orphan() =>
        CollectionAssert.AreEquivalent(JobDispatchService.SupportedJobTypes.ToList(),
            ScheduleSyncService.Orphans([]).ToList());

    [TestMethod]
    public void A_live_row_keeps_its_type_scheduled() =>
        Assert.IsEmpty(ScheduleSyncService.Orphans([(JobDispatchService.EmailOrdersReport, true, "0 7 * * *")]));

    [TestMethod]
    [DataRow(false, "0 7 * * *")]   // disabled
    [DataRow(true, null)]           // no cron
    [DataRow(true, "  ")]
    public void A_row_that_cannot_run_does_not_keep_its_type_scheduled(bool enabled, string? cron) =>
        CollectionAssert.Contains(ScheduleSyncService.Orphans([(JobDispatchService.EmailOrdersReport, enabled, cron)]).ToList(),
            JobDispatchService.EmailOrdersReport);

    [TestMethod]
    public void A_row_retyped_to_an_unsupported_name_orphans_the_supported_type() =>
        CollectionAssert.Contains(ScheduleSyncService.Orphans([("SomethingElse", true, "0 7 * * *")]).ToList(),
            JobDispatchService.EmailOrdersReport);
}
