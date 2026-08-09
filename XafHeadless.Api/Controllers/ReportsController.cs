using DevExpress.ExpressApp;
using DevExpress.Persistent.BaseImpl.EF;   // ReportDataV2
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
public class ReportsController(IObjectSpaceFactory objectSpaceFactory) : ControllerBase {
    public record ReportSummary(string Id, string Name);

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
}
