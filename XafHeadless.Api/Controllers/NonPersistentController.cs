using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Core.Internal;
using DevExpress.ExpressApp.DC;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XafHeadless.Api.NonPersistent;

namespace XafHeadless.Api.Controllers;

// NPO-001: the read feed for non-persistent [DomainComponent] types -- the ones with no DbSet, which OData
// cannot serve at all (see NonPersistentRegistry for why they exist and why the seam is here).
//
// The response envelope is deliberately IDENTICAL to OData's ({"value":[...],"@odata.count":N}) so the
// client's existing grid binding is reused wholesale rather than forked. The only thing that differs for the
// client is the URL it fetches from.
//
// READ-ONLY by nature, and not by omission: a computed object has nowhere to save to. There is no POST here
// and there should not be one.
[ApiController, Route("api/nonpersistent"), Authorize]
public class NonPersistentController : ControllerBase {
    // Matches XafListView.RowCap / the OData MaxTop ceiling in Startup. Nobody reads a ListView of 10,000
    // rows; XAF's own grid does not either.
    const int MaxRows = 5000;

    readonly ISharedApplicationProvider applicationProvider;
    readonly IObjectSpaceFactory objectSpaceFactory;
    readonly NonPersistentRegistry registry;

    public NonPersistentController(ISharedApplicationProvider applicationProvider,
            IObjectSpaceFactory objectSpaceFactory, NonPersistentRegistry registry) {
        this.applicationProvider = applicationProvider;
        this.objectSpaceFactory = objectSpaceFactory;
        this.registry = registry;
    }

    [HttpGet("{type}")]
    public IActionResult Get(string type) {
        var model = applicationProvider.GetContainer().Application.Model;
        var modelClass = model.BOModel
            .FirstOrDefault(c => string.Equals(c.TypeInfo?.Type.Name, type, StringComparison.OrdinalIgnoreCase));
        var typeInfo = modelClass?.TypeInfo;
        // Unregistered is a 404, not an empty list: a type this host was never told how to populate is
        // indistinguishable at the wire from one that legitimately has no rows, and silently returning []
        // is exactly the failure mode NPO-001 set out to remove (CreateCollection's empty BindingList).
        if (typeInfo is null || !registry.IsRegistered(typeInfo.Type)) return NotFound();

        using var os = objectSpaceFactory.CreateObjectSpace(typeInfo.Type);
        // Fires ObjectsGetting through NonPersistentObjectSpace.CreateCollection -- the registry's populator
        // runs here, against an ObjectSpace whose AdditionalObjectSpaces were attached at creation, so the
        // populator can query persistent data (and gets it permission-trimmed by the secured provider).
        var rows = os.GetObjects(typeInfo.Type).Cast<object>().ToList();

        // The cap is MODEL-DECLARED, not invented here: IModelListView.TopReturnedObjects is XAF's own
        // per-view limit. Applied AFTER population because it has to be -- ObjectsGettingEventArgs carries
        // no skip/top, so XAF itself always materializes the full filtered set and bounds it afterwards.
        // This therefore bounds the WIRE, not the app's memory; bounding the fetch would require the
        // populator to return a DynamicCollection.
        var declaredCap = modelClass?.DefaultListView?.TopReturnedObjects ?? 0;
        var cap = declaredCap > 0 ? Math.Min(declaredCap, MaxRows) : MaxRows;
        var total = rows.Count;
        if (rows.Count > cap) rows = rows.Take(cap).ToList();

        var members = typeInfo.Members
            .Where(m => m.IsProperty && m.IsPublic && !m.IsList
                // Same test the projector uses to spot a lookup (a reference to another business object),
                // inverted: those are not scalars and have no place in a flat row.
                && m.MemberTypeInfo?.IsDomainComponent != true && m.MemberTypeInfo?.IsPersistent != true)
            .ToList();

        var value = rows.Select(row => {
            var dto = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var member in members) dto[member.Name] = Scalar(member, row);
            return dto;
        }).ToList();

        // "@odata.count" is the TRUE row count, before the cap -- the client shows "first N of M" from it,
        // so trimming it to the capped length would hide the truncation instead of reporting it.
        return Ok(new Dictionary<string, object?>(StringComparer.Ordinal) {
            ["value"] = value,
            ["@odata.count"] = total
        });
    }

    // Enums go out as their CLR NAME, which is the form OData V4 emits in JSON -- so a row from this
    // endpoint and a row from api/odata read identically to the client. (EnumValueCanon tolerates either
    // form, but matching OData is the point: one contract, not two.)
    static object? Scalar(IMemberInfo member, object row) {
        var value = member.GetValue(row);
        if (value is null) return null;
        var memberType = Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType;
        return memberType.IsEnum ? Enum.GetName(memberType, value) : value;
    }
}
