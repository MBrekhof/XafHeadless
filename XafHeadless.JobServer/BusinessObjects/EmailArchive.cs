using System.ComponentModel;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafHeadless.JobServer.BusinessObjects;

// SVR-001: host-owned email-send audit row. System audit only — not navigable / not OData-exposed.
// Shared BO -> host catalog XafHeadlessDemo.
[DefaultProperty(nameof(Subject))]
public class EmailArchive : BaseObject {
    public virtual DateTime SentUtc { get; set; }

    public virtual string From { get; set; } = string.Empty;

    public virtual string To { get; set; } = string.Empty;

    public virtual string Subject { get; set; } = string.Empty;

    public virtual bool Success { get; set; }

    // No length attribute -> nvarchar(max): full delivery error is retained, never truncated (DATA-001).
    public virtual string? ErrorMessage { get; set; }
}
