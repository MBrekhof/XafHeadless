using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Core.Internal;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.WebApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using XafHeadless.Api.Metadata;

namespace XafHeadless.Api.Controllers;

// PH2-005 / LOOKUP-001: the read-only candidate feed a lookup editor binds to.
//
// Why this exists rather than reusing api/odata/{type}: the editor used to fetch the target type's first
// 50 rows over OData, which has two defects that are not theoretical in this very demo. Employee has 51
// rows, so one employee could never be picked; CustomerStore has 200, so three quarters were unreachable.
// And if the object a record ALREADY references fell outside that window, the combo rendered blank --
// the editor could silently drop an existing value. Neither is fixable by raising the cap.
//
// It also closes PH2-005's original argument: reaching a lookup target over OData means widening
// options.BusinessObject<T>() for every target type, which grows the general exposure surface per lookup.
// This endpoint reads through a secured IObjectSpace instead, so a lookup target needs no OData exposure
// at all -- only the user's own read permission, asked of XAF rather than re-implemented here.
//
// Text is resolved SERVER-side through ViewMetadataProjector.ResolveDisplayPath -- the same walk that puts
// the display path on the wire for grids (BUG-008). Sharing it is the point: a second copy would drift,
// and the client would render different text in a combo than in a grid cell for the same object.
[ApiController, Route("api/lookup"), Authorize]
public class LookupController : ControllerBase {
    // A lookup dropdown is for picking, not browsing: a bounded page plus server-side search is the whole
    // contract. The cap is enforced here, not trusted from the query string.
    const int DefaultTop = 50;
    const int MaxTop = 200;

    readonly ISharedApplicationProvider applicationProvider;
    readonly ISecurityProvider securityProvider;
    readonly IObjectSpaceFactory objectSpaceFactory;

    public LookupController(ISharedApplicationProvider applicationProvider,
            ISecurityProvider securityProvider, IObjectSpaceFactory objectSpaceFactory) {
        this.applicationProvider = applicationProvider;
        this.securityProvider = securityProvider;
        this.objectSpaceFactory = objectSpaceFactory;
    }

    public record LookupItemDto(string Key, string Text);

    [HttpGet("{type}")]
    public IActionResult Get(string type, [FromQuery] string? search, [FromQuery] string? key,
            [FromQuery] int? top = null) {
        var typeInfo = FindTypeInfo(type);
        if (typeInfo is null) return NotFound();

        var clrType = typeInfo.Type;
        var security = (IRequestSecurityStrategy)securityProvider.GetSecurity();
        using var os = objectSpaceFactory.CreateObjectSpace(clrType);
        // Rule 2 everywhere else in this host: ask security, never re-implement it. A user who cannot read
        // the target type gets 403 rather than an empty list, so the client can say why.
        if (!security.CanRead(clrType, os)) return Forbid();

        var (path, _) = ViewMetadataProjector.ResolveDisplayPath(typeInfo);
        var take = Math.Clamp(top ?? DefaultTop, 1, MaxTop);

        var items = new List<LookupItemDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The object the record ALREADY references comes first and unconditionally, even when a search term
        // would exclude it. Without this the editor cannot display its own current value, which is exactly
        // how the 50-row window used to lose one.
        if (!string.IsNullOrEmpty(key)) {
            object current;
            try { current = os.GetObjectByKey(clrType, KeyConverter.Convert(key, typeInfo.KeyMember.MemberType)); }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException) {
                return BadRequest(new { error = $"Malformed key '{key}' for type '{type}'." });
            }
            if (current is not null) Add(current);
        }

        // Contains() over the display PATH, evaluated by the data store. XAF criteria take dotted paths, so
        // a two-hop display member (Store -> Emblem -> CityName) searches on the text the user actually sees.
        var criteria = string.IsNullOrWhiteSpace(search)
            ? null
            : CriteriaOperator.Parse($"Contains([{path}], ?)", search);
        var candidates = os.GetObjects(clrType, criteria);
        // Documented bounded fetch (dxdocs, IObjectSpace.SetTopReturnedObjectsCount 26.1 -- explicitly
        // supports EFCollection, this host's provider): the cap reaches the DATABASE rather than trimming a
        // fully materialized list, which is the whole reason this endpoint exists.
        os.SetTopReturnedObjectsCount(candidates, take + 1);
        foreach (var obj in candidates) {
            if (items.Count >= take) break;
            Add(obj);
        }

        // Ordered by what the user reads, not by whatever order the store returned.
        return Ok(items.OrderBy(i => i.Text, StringComparer.CurrentCultureIgnoreCase).ToList());

        void Add(object obj) {
            var itemKey = os.GetKeyValueAsString(obj);
            if (itemKey is null || !seen.Add(itemKey)) return;
            items.Add(new LookupItemDto(itemKey, DisplayText(typeInfo, obj, path)));
        }
    }

    // Resolves by SHORT type name, which is what LookupMetadata.ObjectType carries (targetInfo.Type.Name),
    // so the client can pass straight back what the projector gave it.
    ITypeInfo? FindTypeInfo(string typeName) =>
        applicationProvider.GetContainer().Application.Model.BOModel
            .FirstOrDefault(c => string.Equals(c.TypeInfo?.Type.Name, typeName, StringComparison.OrdinalIgnoreCase))
            ?.TypeInfo;

    // Walks the display path member by member. A null anywhere along it yields "" rather than throwing --
    // an unset reference is ordinary data, not an error.
    static string DisplayText(ITypeInfo typeInfo, object obj, string path) {
        object? value = obj;
        var owner = typeInfo;
        foreach (var segment in path.Split('.')) {
            var member = owner?.FindMember(segment);
            if (member is null || value is null) return "";
            value = member.GetValue(value);
            owner = member.MemberTypeInfo;
        }
        return value?.ToString() ?? "";
    }
}
