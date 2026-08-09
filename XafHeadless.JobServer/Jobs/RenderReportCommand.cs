namespace XafHeadless.JobServer.Jobs;

// RPT-001: render a CHOSEN report and store it for its requester to collect.
//
// Distinct from EmailOrdersReportCommand, which renders one hardcoded report and emails it. This one
// takes the report identifier, carries the requester so the artifact can be scoped to them, and does NOT
// email -- a user who clicked "run" in the UI wants the PDF back, not a message. It therefore also has no
// dependency on SMTP, which is why it works in environments where the email job cannot.
//
// ReportTypeName is ReportDataV2.PredefinedReportTypeName, the stable resource-type name
// ReportRenderService resolves -- not the primary key, whose GUID regenerates on every re-seed.
public sealed record RenderReportCommand(
    string ReportTypeName,
    string? Criteria,
    string RequestedBy,
    Guid CorrelationId);
