using System.ComponentModel;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.Model;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafHeadless.JobServer.BusinessObjects;

// SVR-001: host-owned shared BO. Lands in the host catalog XafHeadlessDemo via the same
// .WithSharedBusinessObjects mechanism UserLayoutPref uses (see XafHeadless.Api\BusinessObjects\
// UserLayoutPref.cs for the verified host-DB routing) — auto-schema-updated, no DbSet needed.
// SVR-002: one-row-per-JobTypeName is a design invariant (Dispatch G's cron sync does a per-row
// SyncScheduleByName). Same GAP-008 [DisableDeferredDeletion]+plain-[Index] pattern as
// UserLayoutPref (see its verified rationale) — a plain unfiltered unique index is correct here too.
[DisableDeferredDeletion]
[Microsoft.EntityFrameworkCore.Index(nameof(JobTypeName), IsUnique = true)]
[DefaultClassOptions]
[DefaultProperty(nameof(Name))]
public class JobDefinition : BaseObject {
    public virtual string Name { get; set; } = string.Empty;

    [ModelDefault("PredefinedValues", "EmailOrdersReport")]
    public virtual string JobTypeName { get; set; } = "EmailOrdersReport";

    // No length attribute -> EF Core convention maps an unconstrained string to nvarchar(max).
    // SVR-001: never repeat the sibling project's varchar(1) truncation mistake (DATA-001 lesson).
    public virtual string? ParametersJson { get; set; }

    public virtual string? CronExpression { get; set; }

    public virtual bool IsEnabled { get; set; }

    public virtual DateTime? LastRunUtc { get; set; }

    public virtual DateTime? NextRunUtc { get; set; }

    public virtual JobRunStatus LastRunStatus { get; set; } = JobRunStatus.NeverRun;
}

public enum JobRunStatus { NeverRun, Running, Success, Failed }
