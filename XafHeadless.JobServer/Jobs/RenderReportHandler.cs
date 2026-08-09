using DevExpress.ExpressApp;
using XafHeadless.JobServer.BusinessObjects;
using XafHeadless.JobServer.Reports;

namespace XafHeadless.JobServer.Jobs;

// RPT-001: renders a chosen report and stores the PDF for its requester to collect.
//
// The same shape as EmailOrdersReportHandler minus the delivery step: render, write a ReportArtifact,
// stop. The requester and correlation id come through so the download endpoint can scope the result --
// see ReportArtifact.RequestedBy for why that is a security boundary rather than bookkeeping.
//
// The write goes through INonSecuredObjectSpaceFactory in the OUTER job scope (TenantId null), because
// ReportArtifact is a SHARED BO and the null-tenant scope routes to the writable host catalogue.
// ReportRenderService isolates its own tenant selection in a child scope, so it does not disturb this.
// Trusted worker code, no HTTP request, so no admin gate -- but note the render itself runs as the
// service user, which is exactly why the artifact records who asked for it.
public sealed class RenderReportHandler(
    ReportRenderService renderer,
    INonSecuredObjectSpaceFactory objectSpaceFactory,
    ILogger<RenderReportHandler> logger) : IJobHandler<RenderReportCommand> {

    public async Task ExecuteAsync(RenderReportCommand command, CancellationToken cancellationToken = default) {
        var rendered = await renderer.RenderPdfAsync(
            command.ReportTypeName, command.Criteria, command.Parameters, cancellationToken);

        Guid artifactId;
        using (var os = objectSpaceFactory.CreateNonSecuredObjectSpace<ReportArtifact>()) {
            var artifact = os.CreateObject<ReportArtifact>();
            artifact.ReportKey = rendered.DisplayName;
            artifact.ContentType = "application/pdf";
            artifact.Content = rendered.Pdf;
            artifact.CreatedUtc = DateTime.UtcNow;
            artifact.RequestedBy = command.RequestedBy;
            artifact.CorrelationId = command.CorrelationId;
            os.CommitChanges();
            artifactId = artifact.ID;
        }

        logger.LogInformation(
            "Rendered {ReportType} -> artifact {ArtifactId} ({Bytes} bytes) for {RequestedBy}, correlation {CorrelationId}",
            command.ReportTypeName, artifactId, rendered.Pdf.Length, command.RequestedBy, command.CorrelationId);
    }
}
