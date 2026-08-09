namespace XafHeadless.Components.Contracts;

// RPT-001: one entry in the report catalogue. Id is ReportDataV2.PredefinedReportTypeName -- the stable
// resource-type name the renderer looks up, deliberately not the primary key, whose GUID regenerates on
// every re-seed of the disposable dev catalogue.
public record ReportSummary(string Id, string Name);
