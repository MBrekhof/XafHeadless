using DevExpress.ExpressApp;                        // INonSecuredObjectSpaceFactory, IObjectSpace, FirstOrDefault
using DevExpress.ExpressApp.AmbientContext;         // IValueManagerStorageContext
using DevExpress.ExpressApp.MultiTenancy;           // ITenantProvider
using DevExpress.ExpressApp.MultiTenancy.Internal;  // ITenantNameHelper
using DevExpress.ExpressApp.ReportsV2;              // IReportExportService
using DevExpress.ExpressApp.Security;               // ISecurityStrategyBase, SecurityStrategy, AuthenticationStandardLogonParameters
using DevExpress.Persistent.BaseImpl.EF;            // ReportDataV2
using DevExpress.XtraPrinting;                      // ExportTarget
using OutlookInspiredDemo.Module.BusinessObjects;   // ApplicationUser

namespace XafHeadless.JobServer.Reports;

public sealed record RenderedReport(byte[] Pdf, string DisplayName);

// Renders a stored ReportDataV2 layout to PDF bytes. Ported from a companion headless implementation's
// report render service; export chain (LoadReport(IReportDataV2) -> SetupReport(criteria, sort:null)
// -> ExportReportAsync(Pdf)) unchanged. Two deliberate divergences from the port:
//
// 1. Lookup by the STABLE ReportDataV2.PredefinedReportTypeName (the report's resource-type name),
//    not the sequential GUID primary key. This host's tenant DB is a disposable dev catalog seeded by
//    the demo; its ReportDataV2 GUIDs regenerate on every re-seed, so a hardcoded GUID would rot. The
//    resource-type name is stable across re-seeds. The brief sanctions "a stable identifier".
//
// 2. TENANT SELECTION IN AN ISOLATED CHILD SCOPE (SVR-001, verified against installed 26.1 source):
//    ReportDataV2 is a TENANT-scoped type (not a shared BO), so it lives in OutlookInspiredDemo_company1,
//    not the host catalog. A Hangfire worker resolves no tenant from an HTTP request, so we set it
//    explicitly: ITenantProvider.TenantId = ITenantNameHelper.GetTenantIdByName("company1.com"), which
//    flips INonSecuredObjectSpaceFactory routing to the tenant provider (IsTenantSet -> tenant branch;
//    ApplicationExtensions.cs:89-91, WebApiXAFApplicationBuilderWrapper.cs:70).
//
//    ITenantProvider is registered AddScoped (MultiTenancyCoreStartupExtensions.cs:50), so setting
//    TenantId mutates the state of the CURRENT DI scope. If we set it in the shared Hangfire job scope,
//    the recorder's LATER shared-BO write (JobExecutionRecord) would see IsTenantSet == true and route
//    into the read-only-shared-data branch (MultiTenantObjectSpaceFactory.cs:100-104), silently failing
//    to persist to the host catalog. So we do the tenant work in a FRESH child scope wrapped in
//    IValueManagerStorageContext.RunWithStorageAsync -- exactly the pattern DevExpress uses for its own
//    non-request tenant work (TenantDatabaseUpdater.cs:63-74, MultiTenantServiceScopeFactory.cs:56).
//    The outer job scope's TenantProvider.TenantId stays null, so the recorder's shared writes always
//    hit the genuine writable host branch. See docs/DEVIATIONS.md.
public sealed class ReportRenderService(
    IServiceScopeFactory scopeFactory,
    IValueManagerStorageContext valueManagerStorageContext) {
    const string TenantName = "company1.com";
    // The report LAYOUT loads via a non-secured space, but ReportsV2 populates the report's DATA source
    // through a SECURED object space (ScopedReportObjectSpaceProvider -> SecuredObjectSpaceFactory ->
    // EnsureLogon), which throws "The user name must not be empty" without an authenticated context.
    // Empirically confirmed live (Dispatch D). So this trusted background render logs on the demo's
    // seeded tenant admin (empty password) inside the tenant child scope. This is why the OUTER job
    // scope can stay on NoOpJobScopeInitializer: only the report data-fill needs a logon, and it needs
    // it against the TENANT database -- which is exactly this child scope.
    const string ServiceUserName = "Admin@" + TenantName;

    public Task<RenderedReport> RenderPdfAsync(string predefinedReportTypeName, string? criteria,
            Dictionary<string, string?>? parameters, CancellationToken ct) =>
        valueManagerStorageContext.RunWithStorageAsync(async () => {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantProvider>().TenantId =
                sp.GetRequiredService<ITenantNameHelper>().GetTenantIdByName(TenantName);

            LogonServiceUser(sp);

            var exportService = sp.GetRequiredService<IReportExportService>();
            var objectSpaceFactory = sp.GetRequiredService<INonSecuredObjectSpaceFactory>();

            using var os = objectSpaceFactory.CreateNonSecuredObjectSpace<ReportDataV2>();
            var reportData = os.FirstOrDefault<ReportDataV2>(r => r.PredefinedReportTypeName == predefinedReportTypeName)
                ?? throw new InvalidOperationException($"Report '{predefinedReportTypeName}' not found in ReportDataV2 (tenant '{TenantName}').");

            using var report = exportService.LoadReport(reportData);
            // RPT-001: apply the report's OWN parameters before setup, converting each supplied string to
            // the type the parameter declares -- the command carries strings because it round-trips through
            // Hangfire's JSON storage, and only here is the declared type known. An unknown name is ignored
            // rather than throwing: a stale client should not be able to fail a render by naming a
            // parameter the report no longer has. A value that will not convert IS fatal, because
            // silently rendering with the default would hand back a report that quietly answers the wrong
            // question.
            ApplyParameters(report, parameters);
            // Empty/whitespace criteria => no extra filter (the report's own FilterString still applies).
            exportService.SetupReport(report, string.IsNullOrWhiteSpace(criteria) ? null : criteria, sortProperties: null);
            using var stream = await exportService.ExportReportAsync(report, ExportTarget.Pdf);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return new RenderedReport(ms.ToArray(),
                string.IsNullOrWhiteSpace(reportData.DisplayName) ? predefinedReportTypeName : reportData.DisplayName);
        });

    static void ApplyParameters(DevExpress.XtraReports.UI.XtraReport report,
            Dictionary<string, string?>? parameters) {
        if (parameters is null || parameters.Count == 0) return;
        foreach (var (name, raw) in parameters) {
            var parameter = report.Parameters
                .OfType<DevExpress.XtraReports.Parameters.Parameter>()
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
            if (parameter is null) continue;                 // stale client naming a removed parameter
            if (string.IsNullOrWhiteSpace(raw)) continue;    // "leave the report's own default alone"
            var target = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;
            parameter.Value = target == typeof(Guid)
                ? Guid.Parse(raw)                            // ChangeType cannot do Guid
                : Convert.ChangeType(raw, target, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // Authenticate the child scope's security strategy as the tenant admin so the report data-fill's
    // SecuredObjectSpaceFactory.EnsureLogon succeeds. Same mechanism as the companion implementation's job scope initializer.
    static void LogonServiceUser(IServiceProvider sp) {
        var security = sp.GetRequiredService<ISecurityStrategyBase>();
        if (security.IsAuthenticated) return;
        if (security is not SecurityStrategy strategy)
            throw new InvalidOperationException($"Expected SecurityStrategy for report logon, got '{security.GetType().FullName}'.");
        strategy.Authentication.SetLogonParameters(new AuthenticationStandardLogonParameters(ServiceUserName, string.Empty));
        using var logonSpace = sp.GetRequiredService<INonSecuredObjectSpaceFactory>()
            .CreateNonSecuredObjectSpace(typeof(ApplicationUser));
        strategy.Logon(logonSpace);
    }
}
