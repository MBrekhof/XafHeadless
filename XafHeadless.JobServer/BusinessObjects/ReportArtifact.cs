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
}
