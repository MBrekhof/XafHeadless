using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections;
using OutlookInspiredDemo.Module.BusinessObjects;

namespace XafHeadless.Api.Controllers;

// GAP-010: Link / Unlink for a master's to-many association -- the headless equivalent of XAF's
// LinkUnlinkController (verified against installed 26.1 source: LinkObjectsCore -> collectionSource.Add(obj),
// UnlinkObjectsCore -> collectionSource.Remove(obj)). Link ADDS an existing object to the master's collection
// member; Unlink REMOVES the association WITHOUT deleting the object. Only NON-aggregated (shared) collections
// -- aggregated (owned) children use create/delete (create = SaveController's keyless POST). The write goes
// through a secured ObjectSpace + the SAME Committing/IValidator -> 422 contract as SaveController, because the
// OData $ref link path is blocked by ODataReadOnlyMiddleware (every write must run the validation contract).
//
// SCOPE (owner: "server + aggregation projection only now"): these endpoints + the projected LayoutNode.Aggregated
// flag. The client Link picker (a "pick an existing object" dialog) waits for the write-capable lookup editor
// (see TODO MIG-002); the Aggregated flag is what lets the client eventually offer Link/Unlink vs New/Delete.
[ApiController, Route("api/link"), Authorize]
public class LinkController : ControllerBase {
    // Master-type allowlist -- same deliberate-subset rationale as SaveController.ExposedTypes. A type here can
    // be the TARGET of a link/unlink. Employee.AssignedEmployeeTasks (a non-aggregated many-to-many with
    // EmployeeTask) is the demo/test collection. Extend when another master needs the link path.
    static readonly Dictionary<string, Type> ExposedTypes = new(StringComparer.OrdinalIgnoreCase) {
        [nameof(Employee)] = typeof(Employee),
    };

    readonly IObjectSpaceFactory objectSpaceFactory;
    readonly IValidator validator;
    readonly ISecurityProvider securityProvider;
    public LinkController(IObjectSpaceFactory objectSpaceFactory, IValidator validator, ISecurityProvider securityProvider) {
        this.objectSpaceFactory = objectSpaceFactory;
        this.validator = validator;
        this.securityProvider = securityProvider;
    }

    [HttpPost("{type}/{key}/{member}/{childKey}")]
    public IActionResult Link(string type, string key, string member, string childKey) =>
        LinkUnlink(type, key, member, childKey, link: true);

    [HttpDelete("{type}/{key}/{member}/{childKey}")]
    public IActionResult Unlink(string type, string key, string member, string childKey) =>
        LinkUnlink(type, key, member, childKey, link: false);

    IActionResult LinkUnlink(string type, string key, string member, string childKey, bool link) {
        if (!ExposedTypes.TryGetValue(type, out var clrType)) return NotFound();
        using var os = objectSpaceFactory.CreateObjectSpace(clrType);
        var typeInfo = os.TypesInfo.FindTypeInfo(clrType);

        object masterKey;
        try { masterKey = KeyConverter.Convert(key, typeInfo.KeyMember.MemberType); }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException) {
            return BadRequest(new { error = $"Malformed key '{key}' for type '{type}'." });
        }
        var master = os.GetObjectByKey(clrType, masterKey);
        if (master is null) return NotFound();

        var mi = typeInfo.FindMember(member);
        if (mi is null || !mi.IsList)
            return BadRequest(new { error = $"'{member}' is not a collection on type '{typeInfo.Type.Name}'." });
        // GAP-010: aggregated (owned) children use create/delete -- Link/Unlink only applies to shared collections.
        if (mi.IsAggregated)
            return BadRequest(new { error = $"'{member}' is an aggregated (owned) collection -- use create/delete, not link/unlink." });

        // Per-member WRITE permission via the framework security projection (same non-obsolete overload
        // SaveController uses) -- checked before touching the collection.
        var security = (IRequestSecurityStrategy)securityProvider.GetSecurity();
        if (!security.CanWrite(typeInfo.Type, os, member))
            return new ObjectResult(new { error = $"Write denied for member '{member}'." }) { StatusCode = StatusCodes.Status403Forbidden };

        var childType = mi.ListElementTypeInfo.Type;
        object childKeyValue;
        try { childKeyValue = KeyConverter.Convert(childKey, os.TypesInfo.FindTypeInfo(childType).KeyMember.MemberType); }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException) {
            return BadRequest(new { error = $"Malformed child key '{childKey}' for member '{member}'." });
        }
        var child = os.GetObjectByKey(childType, childKeyValue);
        if (child is null) return BadRequest(new { error = $"No {childType.Name} found for child key '{childKey}'." });

        // Mirror of LinkUnlinkController's core: Add/Remove on the master's collection member; the ORM handles
        // the association mechanics (join row for many-to-many, FK for one-to-many). Link is idempotent;
        // Unlink on an absent member is a no-op (both then just commit nothing).
        var collection = (IList)mi.GetValue(master)!;
        if (link) { if (!collection.Contains(child)) collection.Add(child); }
        else collection.Remove(child);

        return CommitWithValidation(os) ?? Ok();
    }

    // Mirrors SaveController.CommitWithValidation -- the standalone host runs no validation on commit unless we
    // wire IValidator ourselves (PersistenceValidationController is a dormant ViewController here). 422 shape
    // identical to the save contract.
    IActionResult? CommitWithValidation(IObjectSpace os) {
        os.Committing += (_, _) => {
            var result = validator.RuleSet.ValidateAllTargets(os, os.ModifiedObjects, DefaultContexts.Save);
            if (result.ValidationOutcome == ValidationOutcome.Error) throw new ValidationException(result);
        };
        try { os.CommitChanges(); return null; }
        catch (ValidationException vex) {
            var errors = vex.Result.Results.Where(r => r.ValidationOutcome == ValidationOutcome.Error).ToList();
            var memberErrors = errors
                .SelectMany(r => r.Rule.UsedProperties.Select(p => (p, r.ErrorMessage)))
                .GroupBy(x => x.p).ToDictionary(g => g.Key, g => g.First().ErrorMessage);
            return UnprocessableEntity(new { MemberErrors = memberErrors, Messages = errors.Select(r => r.ErrorMessage).ToArray() });
        }
    }
}
