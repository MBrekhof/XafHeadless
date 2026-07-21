using DevExpress.Persistent.BaseImpl.EF;
using OutlookInspiredDemo.Module.BusinessObjects;

namespace XafHeadless.Api.BusinessObjects;

// DATA-001 projection-test fixture (Development-only; dev-gated in Startup.WithSharedBusinessObjects so
// it never ships in a production model). Its sole reason to exist is to exercise the reconciled
// lookup-classification predicate in ViewMetadataProjector with the one case today's demo model has NO
// member for: a ONE-WAY, inverse-less reference to a persistent type (PH2-006's "Direction A").
//
// WHY `Ref` is Direction A (verified against installed 26.1 source, not memory):
//   IMemberInfo.IsAssociation for the EF Core provider is `EFCoreNavigation?.Inverse != null`
//   (EFCoreMemberInfoInternal.cs:71) -- true ONLY when the navigation has a configured reciprocal
//   inverse. `Ref` is a plain optional reference to the shared TaxRate, and TaxRate has NO reverse
//   collection back to LookupProbe, so `Ref.IsAssociation == false`. But `Ref.MemberTypeInfo.IsPersistent`
//   is true (TaxRate maps to an entity -- ITypeInfo.IsPersistent, ITypeInfo.cs:57). That (IsAssociation
//   false / IsPersistent true) split is exactly what the OLD projector classified inconsistently:
//   ClassifyDataType -> "string" (IsAssociation false) while ProjectLookup populated Lookup (IsPersistent).
//
// HOST-OWNED, same WithSharedBusinessObjects path as UserLayoutPref (GAP-008): the table lands in the
// host catalog (XafHeadlessDemo), NOT any tenant DB. NOT OData-exposed. Its auto-generated
// LookupProbe_DetailView is what the projector classifies.
//
// xaf-efcore-entities gotchas honored: property is virtual (change-tracking proxies auto-applied to the
// host ServiceDbContext, same as TaxRate/UserLayoutPref); no OwnsOne; no collections; no decimals; key =
// inherited BaseObject.ID (Guid). `Ref` is a plain optional navigation with NO explicit FK and NO inverse
// -- deliberately the minimal shape that yields Direction A (an explicit FK would only add editor noise;
// this entity is never edited, only projected).
public class LookupProbe : BaseObject {
    public virtual TaxRate? Ref { get; set; }
}
