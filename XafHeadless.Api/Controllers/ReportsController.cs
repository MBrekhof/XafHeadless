using DevExpress.ExpressApp;
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

    // Enqueue a render. Returns 202 with a correlation id the caller polls -- the artifact's own primary
    // key does not exist until the job commits, so the API chooses the id up front.
    //
    // Rendering deliberately does NOT happen here: it is CPU-heavy and pulls native dependencies (Skia),
    // which is the whole reason SVR-001 put it in a separate worker. This enqueues into the SAME shared
    // Hangfire storage the existing report job uses.
    [HttpPost("{reportId}/run")]
    public IActionResult Run(string reportId, [FromQuery] string? criteria = null) {
        // Only reports this user can actually see may be run -- otherwise the catalogue's security trim
        // would be advisory, bypassable by anyone who knew an identifier.
        using var os = objectSpaceFactory.CreateObjectSpace<ReportDataV2>();
        var exists = os.GetObjects<ReportDataV2>()
            .Any(r => r.PredefinedReportTypeName == reportId);
        if (!exists) return NotFound();

        var correlationId = Guid.NewGuid();
        jobClient.Enqueue<JobExecutor<RenderReportCommand>>(executor =>
            executor.RunAsync(
                new RenderReportCommand(reportId, criteria, RequesterName, correlationId),
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
