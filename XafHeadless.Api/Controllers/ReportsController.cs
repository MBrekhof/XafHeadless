using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ReportsV2;   // IReportExportService
using DevExpress.Persistent.BaseImpl.EF;   // ReportDataV2
using Hangfire;
using XafHeadless.JobServer.BusinessObjects;
using XafHeadless.JobServer.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace XafHeadless.Api.Controllers;

// RPT-001: the report CATALOGUE -- which reports this app has, so a client can offer them.
//
// SVR-001 built real rendering (JobServer's ReportRenderService: LoadReport -> SetupReport(criteria) ->
// ExportReportAsync(Pdf)) and it is already general -- it takes a report identifier and an optional
// criteria. What was missing is everything around it: nothing told a client WHICH reports exist, the only
// caller hardcoded one report name, and the rendered ReportArtifact was write-only (its comment calls a
// download endpoint "the only intended access path" -- intended, not built; the JobServer has no
// controllers at all).
//
// This is the first of those pieces. It is deliberately in the API rather than the JobServer: a catalogue
// is a cheap read of tenant data the API already serves, while the JobServer exists to keep heavy
// rendering off the request path (MIG-002 boundary B). Listing reports is not rendering them.
//
// The identifier is PredefinedReportTypeName, matching what ReportRenderService looks up -- and for the
// reason its own comment gives: this host's tenant DB is a disposable dev catalogue whose ReportDataV2
// GUIDs regenerate on every re-seed, so the primary key would rot while the resource-type name is stable.
[ApiController, Route("api/reports"), Authorize]
public class ReportsController(
    IObjectSpaceFactory objectSpaceFactory,
    IServiceScopeFactory scopeFactory,
    IReportExportService exportService,
    IBackgroundJobClient jobClient) : ControllerBase {
    public record ReportSummary(string Id, string Name);
    public record RunAccepted(Guid CorrelationId);

    [HttpGet]
    public IActionResult List() {
        // A SECURED space on purpose: reports read business data, so a user who cannot see the catalogue
        // should not be handed one. XAF's own trim decides, this code does not re-implement it.
        using var os = objectSpaceFactory.CreateObjectSpace<ReportDataV2>();
        var reports = os.GetObjects<ReportDataV2>()
            .Where(r => !string.IsNullOrWhiteSpace(r.PredefinedReportTypeName))
            .Select(r => new ReportSummary(
                r.PredefinedReportTypeName,
                string.IsNullOrWhiteSpace(r.DisplayName) ? r.PredefinedReportTypeName : r.DisplayName))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Ok(reports);
    }

    // A report's PARAMETERS, so a client can ask for them before running it.
    //
    // These are the report's OWN parameters (DevExpress.XtraReports.Parameters.Parameter), not XAF's
    // ReportParametersObjectBase -- this module declares 27 of the former and none of the latter, and the
    // report's own collection is the more universal mechanism anyway: it lives on the report, so it needs
    // no companion XAF type to exist.
    //
    // Loading a report layout is NOT rendering it: no data fill, no Skia, no export. That is why this can
    // sit in the API while the render stays in the worker -- MIG-002's boundary is about producing the
    // document. The API already registers AddReports, so IReportExportService is available here.
    //
    // Hidden parameters are omitted: a report marks a parameter Visible=false when it is set by code or
    // by a master report, and offering it in a form would invite a user to break the report.
    [HttpGet("{reportId}/parameters")]
    public IActionResult Parameters(string reportId) {
        using var os = objectSpaceFactory.CreateObjectSpace<ReportDataV2>();
        var reportData = os.GetObjects<ReportDataV2>()
            .FirstOrDefault(r => r.PredefinedReportTypeName == reportId);
        if (reportData is null) return NotFound();

        using var report = exportService.LoadReport(reportData);
        // OfType: ParameterCollection is a non-generic collection, so LINQ cannot infer the element type.
        var parameters = report.Parameters
            .OfType<DevExpress.XtraReports.Parameters.Parameter>()
            .Where(p => p.Visible)
            .Select(p => new ReportParameter(
                p.Name,
                string.IsNullOrWhiteSpace(p.Description) ? p.Name : p.Description.TrimEnd(':'),
                HintFor(p.Type),
                p.Value?.ToString()))
            .ToList();
        return Ok(parameters);
    }

    public record ReportParameter(string Name, string Caption, string Editor, string? DefaultValue);

    // CLR type -> the same editor hints ViewMetadataProjector.ClassifyDataType emits, so the client can
    // render a parameter form with the editors it already has rather than growing a second vocabulary.
    // Deliberately a small mirror rather than a call into the projector: that one classifies an
    // IMemberInfo (with lookups, collections and blobs to consider), and a report parameter is only ever
    // a scalar.
    static string HintFor(Type type) {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsEnum) return "enum";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(DateTime)) return "date";
        if (t == typeof(DateOnly)) return "dateonly";
        if (t == typeof(int) || t == typeof(long) || t == typeof(short)) return "int";
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return "decimal";
        // Guid included on purpose: a Guid-typed parameter is a key the user pastes or a lookup fills in,
        // and a text box is honest for it. Rendering it as a lookup would require knowing the target type,
        // which the parameter does not carry (DynamicListLookUpSettings does, and is a later step).
        return "string";
    }

    // Enqueue a render. Returns 202 with a correlation id the caller polls -- the artifact's own primary
    // key does not exist until the job commits, so the API chooses the id up front.
    //
    // Rendering deliberately does NOT happen here: it is CPU-heavy and pulls native dependencies (Skia),
    // which is the whole reason SVR-001 put it in a separate worker. This enqueues into the SAME shared
    // Hangfire storage the existing report job uses.
    [HttpPost("{reportId}/run")]
    public IActionResult Run(string reportId, [FromQuery] string? criteria = null,
            [FromBody] Dictionary<string, string?>? parameters = null) {
        // Only reports this user can actually see may be run -- otherwise the catalogue's security trim
        // would be advisory, bypassable by anyone who knew an identifier.
        using var os = objectSpaceFactory.CreateObjectSpace<ReportDataV2>();
        var exists = os.GetObjects<ReportDataV2>()
            .Any(r => r.PredefinedReportTypeName == reportId);
        if (!exists) return NotFound();

        var correlationId = Guid.NewGuid();
        jobClient.Enqueue<JobExecutor<RenderReportCommand>>(executor =>
            executor.RunAsync(
                new RenderReportCommand(reportId, criteria, RequesterName, correlationId, parameters),
                CancellationToken.None));
        return Accepted(new RunAccepted(correlationId));
    }

    // Collect a finished render. 202 while the job has not committed yet, 200 + PDF once it has.
    //
    // Scoped to the requester, and that is a SECURITY boundary rather than tidiness: the report was
    // rendered by a SERVICE user (ReportRenderService logs on the tenant admin because the data-fill
    // requires an authenticated context), so its contents can include rows this caller is not permitted
    // to see. Artifacts from the SCHEDULED job carry no requester and are therefore downloadable by
    // nobody here -- deny by default.
    //
    // Read through a FRESH DI scope, exactly as PrefsController does for its own host-shared BO -- and for
    // the reason documented there, which cost two wrong attempts here before I read it:
    //
    //   * the non-secured factory on the REQUEST scope routes to the TENANT context, where a host-shared
    //     type is not registered at all -> "type is not registered within the business model" (seen live);
    //   * the secured factory on the request scope reaches the host but under
    //     MultiTenantReadOnlySelectDataSecurity, which answers FalseCriteria -> zero rows, silently (also
    //     seen live: the job demonstrably wrote the artifact and this still returned 202 forever).
    //
    // ITenantProvider is registered AddScoped, so a fresh scope starts with TenantId == null -> host
    // context -> the host object space provider is active. The per-user boundary is the RequestedBy check
    // below, not XAF row security, which cannot express "belongs to this requester".
    [HttpGet("runs/{correlationId:guid}")]
    public IActionResult Collect(Guid correlationId) {
        using var scope = scopeFactory.CreateScope();
        using var os = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>()
            .CreateNonSecuredObjectSpace(typeof(ReportArtifact));
        var artifact = os.GetObjects<ReportArtifact>()
            .FirstOrDefault(a => a.CorrelationId == correlationId);
        if (artifact is null) return Accepted();                      // still rendering (or never ran)
        if (!string.Equals(artifact.RequestedBy, RequesterName, StringComparison.OrdinalIgnoreCase))
            return Forbid();                                          // someone else's render
        return File(artifact.Content, artifact.ContentType, $"{artifact.ReportKey}.pdf");
    }

    // The authenticated caller, as recorded on the artifact. Never trusted from the request body.
    string RequesterName => User.Identity?.Name ?? string.Empty;
}
