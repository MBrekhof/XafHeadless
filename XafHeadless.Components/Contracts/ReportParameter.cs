namespace XafHeadless.Components.Contracts;

// RPT-001: one of a report's OWN parameters (DevExpress.XtraReports.Parameters.Parameter), projected for
// a client to collect a value for.
//
// Editor uses the same vocabulary ClassifyDataType emits for a DetailView item, which is the point: the
// parameter form renders through the editors this client already has rather than a parallel set.
// DefaultValue is the report's own, as a string -- the server converts it back to the declared CLR type.
public record ReportParameter(string Name, string Caption, string Editor, string? DefaultValue);
