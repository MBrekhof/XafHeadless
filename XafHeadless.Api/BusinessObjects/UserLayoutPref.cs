using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafHeadless.Api.BusinessObjects;

// GAP-008: host-owned, per-user, per-view grid-layout prefs blob. Lives in the HOST catalog
// (XafHeadlessDemo), NOT any tenant DB.
//
// GAP-008-minors #1: (UserKey, ViewId) UNIQUE INDEX -- what was verified, and why the brief's
// hypothesis ("filtered on GCRecord IS NULL") does NOT apply verbatim to this entity:
//   - BaseObject.GCRecord here is `DevExpress.Persistent.BaseImpl.EF.BaseObject` (EF Core), whose
//     GCRecord is `public virtual int GCRecord { get; set; } = 0;` (installed 26.1 source,
//     .../DevExpress.Persistent.BaseImpl.EFCore/BaseObject.cs:75) -- an int, NOT nullable. On
//     soft-delete, EFCoreDeferredDeletionInterceptor.SetCurrentPropertyValue sets it to the LITERAL
//     `1` (.../DevExpress.ExpressApp.EFCore/DeferredDeletion/EFCoreDeferredDeletionInterceptor.cs:118),
//     and EFCoreDeferredDeletionRegistration's own auto query-filter is `GCRecord == 0`
//     (.../DeferredDeletion/EFCoreDeferredDeletionRegistration.cs:81, `filterValue = 0`). So for THIS
//     EF Core BaseObject, the equivalent-intent filter predicate would be `GCRecord = 0`, never
//     `IS NULL` -- the xaf-efcore-entities skill's own documented pattern
//     (`HasIndex(...).IsUnique().HasFilter("\"GCRecord\" = 0")`) confirms `= 0`, not `IS NULL`, is
//     the project's real convention (the "IS NULL" framing is XPO's nullable-GCRecord convention,
//     which is what SEC-002 actually touched -- a DIFFERENT, XPO-based separate (production) dev
//     database, not this EF Core host; see docs/notes/test-fixtures.md -- confirmed NOT the same schema).
//   - That Fluent-API `.HasFilter(...)` pattern needs `OnModelCreating` access, which is NOT
//     available here: UserLayoutPref is a SharedBusinessObject on the HOST's internal
//     `ServiceDbContext : HostDbContext` (DevExpress-owned, `internal`), and
//     `HostDbContext.RegisterCustomEntityTypes` registers every shared type via a bare
//     `modelBuilder.Entity(sharedType)` -- no lambda, no OnModelCreating hook exposed to this project
//     (.../DevExpress.ExpressApp.MultiTenancy.EFCore/Objects/ServiceDbContext.cs:77-79).
//   - The verified, DevExpress-shipped answer for exactly this shape (a BaseObject needing a real
//     unique index, registered only via a no-lambda `modelBuilder.Entity(type)`, with no reachable
//     OnModelCreating) is `[DisableDeferredDeletion]` + a plain
//     `[Microsoft.EntityFrameworkCore.Index(..., IsUnique = true)]` -- ZERO filter needed, because
//     disabling deferred deletion makes GCRecord soft-delete never happen for this type at all (a
//     `Delete` becomes a real SQL DELETE, not a GCRecord=1 tombstone) -- so there is never a
//     soft-deleted row left behind to collide with a fresh insert on the same (UserKey, ViewId).
//     Proof this combo is real and EF-Core-convention-driven (not a guess), both from installed 26.1
//     source: `UserToken` uses precisely this pairing
//     (.../DevExpress.Persistent.BaseImpl.EFCore/PermissionPolicy/UserToken.cs:44-46:
//     `[Microsoft.EntityFrameworkCore.Index(nameof(UserId), IsUnique = false)] [DisableDeferredDeletion]`),
//     and `Tenant` proves the `[Index]` ATTRIBUTE is honored even when the entity is registered via the
//     exact same no-lambda `modelBuilder.Entity(type)` call this project's SharedBusinessObjects use
//     (.../PermissionPolicy/../MultiTenancy/Tenant.cs:48, registered from
//     `HostDbContext.RegisterCustomEntityTypes` line 73 -- the SAME method, same call shape, that
//     registers `SharedBusinessObjects` two lines later at line 77-79). `IsDeferredDeletionDisabled`
//     checks the CLR attribute directly (`System.Attribute.GetCustomAttributes(entityType.ClrType)`,
//     EFCoreDeferredDeletionInterceptor.cs:130), so the attribute alone is enough -- no Fluent API call
//     needed anywhere in this project for either the index or the disable.
//   - Practical impact: PrefsController never calls `os.Delete()` on a UserLayoutPref (only
//     find-or-create + commit), so disabling deferred deletion changes nothing observable today --
//     it only forecloses the theoretical future collision (an admin delete, or a later feature, that
//     would otherwise leave a GCRecord=1 tombstone colliding with a same-key re-insert).
//
// HOW IT REACHES THE HOST DB (verified against installed 26.1 source, not memory):
//   Startup registers it via .WithSharedBusinessObjects(typeof(TaxRate), typeof(UserLayoutPref)).
//   MultiTenancyApplicationBuilder.WithSharedBusinessObjects (DevExpress.ExpressApp.MultiTenancy.EFCore)
//   adds each type to InternalExtensionModuleOptions.SharedBusinessObjects AND DeclaredExportedTypes.
//   The host DB uses the internal ServiceDbContext : HostDbContext (WithHostDbContext -> ServiceDbContext);
//   HostDbContext.RegisterCustomEntityTypes (ServiceDbContext.cs) calls modelBuilder.Entity(sharedType)
//   for every SharedBusinessObject -> the table is created by the host's schema auto-update
//   (CheckCompatibilityType.DatabaseSchema + DatabaseVersionMismatch -> Updater.Update(), Startup.cs).
//   This is byte-for-byte how the demo's TaxRate becomes a host-DB table (TaxRate is NOT a DbSet in the
//   demo's OutlookInspiredEFCoreDbContext either) -- so this entity mirrors a proven shape.
//
// xaf-efcore-entities gotchas honored: all properties are virtual (change-tracking proxies + INPC --
// the secured-EFCore WithDbContext path auto-applies UseChangeTrackingProxies to the host context,
// verified in ObjectSpaceProviderBuilderExtensions.ConfigureDbContext); no OwnsOne; no collections; no
// decimals; key = BaseObject.ID (Guid), auto-assigned -- rows are found/upserted by (UserKey, ViewId),
// never by the key. Deliberately NOT exposed via OData (no options.BusinessObject<T>() in Startup):
// PrefsController is the only access path, per the brief -- don't widen the OData surface.
[DisableDeferredDeletion]
[Microsoft.EntityFrameworkCore.Index(nameof(UserKey), nameof(ViewId), IsUnique = true)]
public class UserLayoutPref : BaseObject {
    // The authenticated user's stable framework key -- ISecurityStrategyBase.UserId.ToString(). A string
    // (not Guid) on purpose: the key is read only via the framework (never assumed), and its CLR type is
    // provider-defined (EF PermissionPolicyUser's key is a Guid here); storing its string form avoids
    // hardcoding that. XAF user keys are globally unique across tenants, so (UserKey, ViewId) is a safe
    // multi-tenant composite identity.
    public virtual string UserKey { get; set; } = string.Empty;

    public virtual string ViewId { get; set; } = string.Empty;

    // The serialized DxGrid GridPersistentLayout. No length attribute -> EF Core maps a no-max string to
    // nvarchar(max) on SQL Server, which is the "size generously" the brief asks for.
    public virtual string PrefsJson { get; set; } = string.Empty;
}
