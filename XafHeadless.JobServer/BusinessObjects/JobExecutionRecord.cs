using System.ComponentModel;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafHeadless.JobServer.BusinessObjects;

// SVR-001: host-owned audit row for a single job run. Shared BO -> host catalog XafHeadlessDemo.
[DefaultClassOptions]
[DefaultProperty(nameof(JobName))]
public class JobExecutionRecord : BaseObject {
    public virtual string JobName { get; set; } = string.Empty;

    public virtual string JobTypeName { get; set; } = string.Empty;

    public virtual DateTime StartedUtc { get; set; }

    public virtual DateTime? CompletedUtc { get; set; }

    public virtual JobRunStatus Status { get; set; } = JobRunStatus.Running;

    // No length attribute -> nvarchar(max): full exception text is retained, never truncated (DATA-001).
    public virtual string? ErrorMessage { get; set; }

    public virtual long DurationMs { get; set; }
}
