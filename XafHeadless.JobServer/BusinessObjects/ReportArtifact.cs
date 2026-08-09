using System.ComponentModel;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafHeadless.JobServer.BusinessObjects;

// SVR-001: host-owned rendered-report blob. Deliberately NOT navigable / NOT OData-exposed — it
// carries PDF bytes, so a dedicated download endpoint is the only intended access path (same
// "not exposed" stance as UserLayoutPref). Shared BO -> host catalog XafHeadlessDemo.
[DefaultProperty(nameof(ReportKey))]
public class ReportArtifact : BaseObject {
    public virtual string ReportKey { get; set; } = string.Empty;

    public virtual string ContentType { get; set; } = string.Empty;

    // EF Core convention maps byte[] to varbinary(max) (mirrors the demo's Picture.Data blob).
    public virtual byte[] Content { get; set; } = [];

    public virtual DateTime CreatedUtc { get; set; }

    // RPT-001: who asked for this render, and which request it belongs to.
    //
    // Both exist for the download endpoint, and RequestedBy is a SECURITY boundary, not bookkeeping. A
    // report is rendered by a SERVICE user (the tenant admin -- see ReportRenderService, whose data-fill
    // needs an authenticated context), so its PDF can contain rows the requesting user is not permitted
    // to see. Serving artifacts to any authenticated caller would therefore hand out data past the
    // security trim. The endpoint requires a match on RequestedBy, which also means artifacts created by
    // the SCHEDULED job -- which has no requesting user, so this stays empty -- are downloadable by
    // nobody. Deny by default.
    //
    // CorrelationId is chosen by the API before enqueueing, so the client has something to poll for
    // before the job has run: the artifact's own primary key does not exist until the job commits.
    public virtual string RequestedBy { get; set; } = string.Empty;

    public virtual Guid CorrelationId { get; set; }
}
