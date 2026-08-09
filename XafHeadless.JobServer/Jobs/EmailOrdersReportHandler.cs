using DevExpress.ExpressApp;
using XafHeadless.JobServer.BusinessObjects;
using XafHeadless.JobServer.Reports;
using XafHeadless.JobServer.Services.Email;

namespace XafHeadless.JobServer.Jobs;

// SVR-001 Task 3.3/4.1: render the demo's Orders report to PDF, store it as a ReportArtifact BO, then
// email it (one recipient, one PDF attachment). Adapted from a companion headless implementation's
// report generation handler, trimmed: no ReportKey/Criteria/FileType parameters (this handler renders
// exactly one report -- OrdersReport, PDF, no criteria).
//
// The ReportArtifact write goes through INonSecuredObjectSpaceFactory in the OUTER job scope (where
// TenantId stays null): ReportArtifact is a SHARED BO, so the null-tenant scope routes to the writable
// host catalog. ReportRenderService isolates its own tenant selection in a child scope, so it does not
// affect this write. This is trusted worker code (no HTTP request), so it needs no admin gate.
//
// Email delivery: IEmailService.SendEmailWithAttachmentsAsync sends via MailKit and archives the send
// in EmailArchive (best-effort on its own, see EmailService's I-1 note) -- this handler just calls it
// once after the artifact is committed. A retry of THIS handler after a successful send would still
// re-send (I-1's residual ceiling, see docs/DEVIATIONS.md); JobExecutor's best-effort bookkeeping only
// prevents the *recorder* from turning a successful run into a retry trigger.
public sealed class EmailOrdersReportHandler(
    ReportRenderService renderer,
    INonSecuredObjectSpaceFactory objectSpaceFactory,
    IEmailService emailService,
    ILogger<EmailOrdersReportHandler> logger) : IJobHandler<EmailOrdersReportCommand> {
    // The one report this host renders: the demo's "Orders" report, identified by its stable
    // resource-type name (ReportDataV2.PredefinedReportTypeName). See ReportRenderService.
    const string OrdersReportType = "OutlookInspiredDemo.Module.Resources.Reports.ProductOrders";

    public async Task ExecuteAsync(EmailOrdersReportCommand command, CancellationToken cancellationToken = default) {
        var rendered = await renderer.RenderPdfAsync(OrdersReportType, criteria: null, parameters: null, cancellationToken);

        Guid artifactId;
        using (var os = objectSpaceFactory.CreateNonSecuredObjectSpace<ReportArtifact>()) {
            var artifact = os.CreateObject<ReportArtifact>();
            artifact.ReportKey = rendered.DisplayName;
            artifact.ContentType = "application/pdf";
            artifact.Content = rendered.Pdf;
            artifact.CreatedUtc = DateTime.UtcNow;
            os.CommitChanges();
            artifactId = artifact.ID;
        }

        logger.LogInformation("Rendered OrdersReport -> artifact {ArtifactId} ({Bytes} bytes); recipients: {Recipients}",
            artifactId, rendered.Pdf.Length, command.EmailRecipients);

        await emailService.SendEmailWithAttachmentsAsync(
            command.EmailRecipients,
            $"Report: {rendered.DisplayName}",
            $"<p>The report <strong>{rendered.DisplayName}</strong> is attached.</p>",
            new Dictionary<string, byte[]> { [$"{rendered.DisplayName}.pdf"] = rendered.Pdf });
    }
}
