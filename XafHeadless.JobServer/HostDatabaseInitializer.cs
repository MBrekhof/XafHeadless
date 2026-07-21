using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Utils;
using XafHeadless.JobServer.BusinessObjects;

namespace XafHeadless.JobServer;

// SVR-001: provision the HOST catalog (XafHeadlessDemo) schema at startup. This host serves no XAF/
// OData request and performs no logon, so nothing would otherwise trigger the host-catalog schema
// update that creates the shared-BO tables (the four SVR-001 BOs). Verified along the way: neither
// creating a non-secured ObjectSpace nor running CheckCompatibility for the host (null-tenant, which
// targets the in-memory shared application) provisions the host SQL catalog.
//
// IDBUpdater.Update() is the DevExpress-provided mechanism for exactly this: it builds the application,
// iterates its ObjectSpaceProviders, and runs the schema update on each (installed 26.1 source:
// DevExpress.ExpressApp\Utils\IDBUpdater.cs -> DBUpdater<T>.UpdateCore). It is the same path the demo's
// own OutlookInspiredDemo.Blazor.Server runs for its `--updateDatabase` command (Program.cs), which
// provisions this module's host catalog. Running it automatically at startup means a fresh clone needs
// no manual `--updateDatabase` step. Idempotent (the demo host-branch seeder is find-or-create), so it
// returns "not needed" once the schema is current.
public sealed class HostDatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<HostDatabaseInitializer> logger) : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) {
        using var scope = scopeFactory.CreateScope();
        var updater = scope.ServiceProvider.GetRequiredService<IDBUpdater>();
        var status = updater.Update(forceUpdate: false, silent: true);
        logger.LogInformation(
            "SVR-001 host DB update via IDBUpdater; status {Status} (0=Completed, 1=Error, 2=NotNeeded).", status);

        SeedDemoJobDefinition(scope);
        return Task.CompletedTask;
    }

    // SVR-001 Task 2.2: seed the one demo JobDefinition row on a fresh clone -- no manual step needed.
    // Same fresh-scope (TenantId == null -> host context) + INonSecuredObjectSpaceFactory path
    // PrefsController uses to write other host-shared BOs; gated on GetObjectsCount == 0, mirroring the
    // demo's own DataGenerator.Execute() idempotency pattern (MT-001), so re-running this on every
    // startup is a no-op once the row exists.
    static void SeedDemoJobDefinition(IServiceScope scope) {
        using var os = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>()
            .CreateNonSecuredObjectSpace(typeof(JobDefinition));
        if (os.GetObjectsCount(typeof(JobDefinition), null) > 0) return;
        var job = os.CreateObject<JobDefinition>();
        job.Name = "Daily Orders Report";
        job.JobTypeName = "EmailOrdersReport";
        job.CronExpression = "0 7 * * *";
        job.IsEnabled = false;
        // SVR-001 Dispatch G: F's M-4 guard throws if EmailRecipients is missing once a row is enabled
        // with a cron (ScheduleSyncService.SyncOnce -> SyncScheduleByName -> Deserialize). Seed a
        // recipient so a freshly-seeded row's enabled-cron path is valid out of the box.
        job.ParametersJson = """{"EmailRecipients":"demo-recipient@xafheadless.local"}""";
        os.CommitChanges();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
