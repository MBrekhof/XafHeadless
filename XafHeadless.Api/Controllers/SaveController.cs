using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using DevExpress.Persistent.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using OutlookInspiredDemo.Module.BusinessObjects;
using XafHeadless.JobServer.BusinessObjects;

namespace XafHeadless.Api.Controllers;

// Task 5 fallback save path. THE save contract for the client: POST with a partial-member body ->
// 200 on success, 422 + { MemberErrors: { Member: msg }, Messages: [...] } on a validation-rule break.
// Needed because the built-in OData PATCH does not provide this: the standalone Web API host never
// activates DevExpress.ExpressApp.Validation.PersistenceValidationController (it is a ViewController —
// it wires into IObjectSpace.Committing only from OnActivated/OnDeactivated, which require a live
// View/Frame that this headless host never creates). Verified empirically: a
// PATCH nulling out ApplicationUser.UserName (which carries DevExpress's own built-in
// [RuleRequiredField] on PermissionPolicyUser.UserName) returned 204 and silently persisted the null —
// confirming dxdocs' "Validate Data Sent to Web API Endpoints" note that CRUD endpoints don't
// initiate data validation on their own.
//
// So this controller wires IValidator itself (IValidator IS already in DI — AddAspNetCoreValidation()
// is called unconditionally by XAF's core ASP.NET Core wiring — just nothing calls it on the CRUD
// path), using the same Committing -> RuleSet.ValidateAllTargets -> throw ValidationException pattern
// dxdocs' own custom-IDataService example uses.
//
// Task 2: route generalized to {type}/{key}; type resolves only against the exposed-BO allowlist
// below (404 otherwise) -- never a bare Type.GetType/reflection lookup over the whole app domain.
[ApiController, Route("api/save"), Authorize]
public class SaveController : ControllerBase {
    // Exposed-BO allowlist: a deliberate SUBSET of Startup's options.BusinessObject<T>() surface, not
    // a lockstep mirror of it -- narrower than the OData/read surface is the safe direction (a type
    // missing here only loses the validating-save path and 404s; it never gains write access it
    // shouldn't have). Extend only when another type needs a validating save path (Order + Employee
    // cover the two save/validation demo scenarios this migration targets; SVR-001 Task 2.3 adds
    // JobDefinition so the generic client grid can create/edit a demo schedule through this same
    // validated path -- OData exposes it read-only, this is its only write path).
    static readonly Dictionary<string, Type> ExposedTypes = new(StringComparer.OrdinalIgnoreCase) {
        [nameof(Order)] = typeof(Order),
        [nameof(Employee)] = typeof(Employee),
        [nameof(JobDefinition)] = typeof(JobDefinition),
    };

    // SVR-001 Task 2.3: JobDefinition is a host-shared BO (Startup.WithSharedBusinessObjects). Shared
    // types are READ-ONLY from any tenant-resolved request BY DESIGN, not a config gap -- dxdocs
    // "Shared Data Support in a Multi-Tenant Application": "A tenant user has read-only access to
    // host shared business objects... The Web API service does not allow authorization using the host
    // account." Verified in installed 26.1 source too: MultiTenantObjectSpaceFactory.cs sets
    // MultiTenantSecurityOptions.IsReadOnlyAccessToSharedDataInHostDatabase=true whenever a
    // tenant-context caller reaches for shared data, which activates MultiTenantReadOnlySelectDataSecurity
    // -- its IsGranted (..Services\MultiTenantReadOnlySelectDataSecurity.cs) returns false for every
    // operation except Read/Navigate, UNCONDITIONALLY, for every type/user -- this REPLACES the normal
    // PermissionPolicyRole check rather than running alongside it, so even Admin 403s. The only writable
    // path is a HOST-context object space (fresh DI scope, same as PrefsController.HostObjectSpace) --
    // which has no logged-on tenant user, so there is no framework permission check left to lean on.
    // Gated explicitly by IsAdministrative instead: see docs/notes/command-authorization.md (same
    // reason TestFixturesController's fixture-seeding endpoints do this -- XAF JWTs carry no role
    // claims, so [Authorize(Roles=...)] can't express it).
    static readonly HashSet<Type> HostSharedGatedTypes = [typeof(JobDefinition)];

    readonly IObjectSpaceFactory objectSpaceFactory;
    readonly IValidator validator;
    readonly ISecurityProvider securityProvider;
    readonly IServiceScopeFactory scopeFactory;
    public SaveController(IObjectSpaceFactory objectSpaceFactory, IValidator validator, ISecurityProvider securityProvider,
        IServiceScopeFactory scopeFactory) {
        this.objectSpaceFactory = objectSpaceFactory;
        this.validator = validator;
        this.securityProvider = securityProvider;
        this.scopeFactory = scopeFactory;
    }

    [HttpPost("{type}/{key}")]
    public IActionResult Save(string type, string key, [FromBody] Dictionary<string, JsonElement> changes) {
        if (!ExposedTypes.TryGetValue(type, out var clrType)) return NotFound();
        var gateError = OpenWriteContext(clrType, out var ctx);
        if (gateError is not null) return gateError;
        using var scope = ctx.Scope;
        using var os = ctx.ObjectSpace;
        var typeInfo = os.TypesInfo.FindTypeInfo(clrType);
        object routeKey;
        try { routeKey = KeyConverter.Convert(key, typeInfo.KeyMember.MemberType); }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException) {
            return BadRequest(new { error = $"Malformed key '{key}' for type '{type}'." });
        }
        var obj = os.GetObjectByKey(clrType, routeKey);
        if (obj is null) return NotFound();
        var applyError = ApplyChanges(os, typeInfo, obj, changes, ctx.Security);
        if (applyError is not null) return applyError;
        var commitError = CommitWithValidation(os);
        return commitError ?? Ok();
    }

    // GAP-003: keyless create. Disambiguated from Save's {type}/{key} route by ASP.NET's segment-count
    // routing -- both are POST under the same "api/save" controller route.
    // os.CreateObject(Type) -- the NON-generic overload -- VERIFIED against installed 26.1 source
    // (DevExpress.ExpressApp/IObjectSpace.cs): `Object CreateObject(Type type);` exists on IObjectSpace
    // alongside the generic `ObjectType CreateObject<ObjectType>();`; the non-generic one is required
    // here because `type` only resolves to a Type at runtime via the ExposedTypes lookup.
    [HttpPost("{type}")]
    public IActionResult Create(string type, [FromBody] Dictionary<string, JsonElement> changes) {
        if (!ExposedTypes.TryGetValue(type, out var clrType)) return NotFound();
        var gateError = OpenWriteContext(clrType, out var ctx);
        if (gateError is not null) return gateError;
        using var scope = ctx.Scope;
        using var os = ctx.ObjectSpace;
        var typeInfo = os.TypesInfo.FindTypeInfo(clrType);
        var obj = os.CreateObject(clrType);
        var applyError = ApplyChanges(os, typeInfo, obj, changes, ctx.Security);
        if (applyError is not null) return applyError;
        var commitError = CommitWithValidation(os);
        // os.GetKeyValueAsString -- VERIFIED against installed 26.1 source (IObjectSpace.cs):
        // `String GetKeyValueAsString(Object obj);`. Server-generated key (BaseObject.ID, a Guid) --
        // the client never sent it; CreateObject/CommitChanges assigned it.
        return commitError ?? StatusCode(StatusCodes.Status201Created, new { key = os.GetKeyValueAsString(obj) });
    }

    // Task 2.3: validating DELETE path. OData DELETE stays 405-blocked by ODataReadOnlyMiddleware --
    // this is a SEPARATE route (allowlist -> key conversion -> lookup -> delete-permission gate ->
    // delete + commit), same shape as Save/Create above.
    [HttpDelete("{type}/{key}")]
    public IActionResult Delete(string type, string key) {
        if (!ExposedTypes.TryGetValue(type, out var clrType)) return NotFound();
        var gateError = OpenWriteContext(clrType, out var ctx);
        if (gateError is not null) return gateError;
        using var scope = ctx.Scope;
        using var os = ctx.ObjectSpace;
        var typeInfo = os.TypesInfo.FindTypeInfo(clrType);
        object routeKey;
        try { routeKey = KeyConverter.Convert(key, typeInfo.KeyMember.MemberType); }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException) {
            return BadRequest(new { error = $"Malformed key '{key}' for type '{type}'." });
        }
        var obj = os.GetObjectByKey(clrType, routeKey);
        if (obj is null) return NotFound();
        // Object-level delete-permission gate -- IsGrantedExtensions.CanDelete(this IRequestSecurityStrategy
        // security, IObjectSpace objectSpace, object targetObject) -- VERIFIED against THIS machine's installed
        // 26.1 source (DevExpress.ExpressApp/DevExpress.ExpressApp.Security/SecurityStrategy/
        // IsGrantedExtensions.cs:244; not [Obsolete] -- the (type, key)-only / no-ObjectSpace overload region
        // starting at line 493 IS [Obsolete(OverloadWithoutObjectSpaceIsObsoleteWarning)]). Chosen over the
        // (Type, IObjectSpace) type-level overload (line 232 -- what ViewMetadataProjector uses for the
        // read-side Delete-button metadata trim) because this route already holds the resolved instance --
        // the object-level overload also evaluates any row-level/criteria security the type-level one can't.
        // ctx.Security is null for the host-gated set (JobDefinition): that whole operation is already
        // admin-gated by OpenWriteContext, and there is no tenant security context in a host object space
        // to ask anyway (see HostSharedGatedTypes comment).
        if (ctx.Security is not null && !ctx.Security.CanDelete(os, obj))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"Delete denied for '{type}'." });
        os.Delete(obj); // verified: void Delete(Object obj) on IObjectSpace (IObjectSpace.cs) -- same API TestFixturesController.DeleteObject already uses
        var commitError = CommitWithValidation(os);
        return commitError ?? NoContent();
    }

    // Resolves the write context for Save/Create/Delete: the normal per-request secured object space
    // for everything, or -- for the small, explicitly-named HostSharedGatedTypes set -- an admin-gated
    // HOST-context object space (fresh DI scope, mirrors PrefsController.HostObjectSpace). Returns a
    // non-null IActionResult (403) on a failed admin gate; ctx.Security is null in the host-gated branch
    // (no tenant security context exists there -- see HostSharedGatedTypes comment).
    IActionResult? OpenWriteContext(Type clrType, out (IObjectSpace ObjectSpace, IRequestSecurityStrategy? Security, IDisposable? Scope) ctx) {
        if (HostSharedGatedTypes.Contains(clrType)) {
            if (securityProvider.GetSecurity() is not IRequestSecurityStrategy { User: PermissionPolicyUser caller } ||
                !caller.Roles.Any(r => r.IsAdministrative)) {
                ctx = default;
                return StatusCode(StatusCodes.Status403Forbidden, new { error = $"'{clrType.Name}' requires an administrator." });
            }
            var scope = scopeFactory.CreateScope();
            var hostOs = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace(clrType);
            ctx = (hostOs, null, scope);
            return null;
        }
        ctx = (objectSpaceFactory.CreateObjectSpace(clrType), (IRequestSecurityStrategy)securityProvider.GetSecurity(), null);
        return null;
    }

    // Extracted per-member write loop (GAP-003): identical reference/enum/scalar resolution and
    // identical 400 behavior for both Save (update) and Create -- do not duplicate this loop.
    // Returns a non-null IActionResult on any short-circuit -- 400 on an unknown/non-writable member,
    // 403 on a CanWrite-denied member, or 400 on a malformed/unresolvable reference key (PH2-002 added
    // the first two; a malformed key was the original case) -- and null on success.
    static IActionResult? ApplyChanges(IObjectSpace os, ITypeInfo typeInfo, object obj, Dictionary<string, JsonElement> changes,
        IRequestSecurityStrategy? security) {
        foreach (var (member, value) in changes) {
            var mi = typeInfo.FindMember(member);
            // PH2-002: an unknown member (typo'd, or just not on the type) is a client error -- 400,
            // naming the member. Previously silently skipped (`continue`), which let a client believe a
            // typo'd write succeeded when nothing happened. A collection member (mi.IsList) is also not
            // writable via this scalar/reference save path -- treated the same way (400, not a silent
            // skip) for the same reason: a client sending "OrderItems": [...] here deserves an explicit
            // rejection, not silence.
            if (mi is null || mi.IsList) {
                return new BadRequestObjectResult(new { error = $"Unknown or non-writable member '{member}' on type '{typeInfo.Type.Name}'." });
            }
            // PH2-002: per-member WRITE permission, asked via the FRAMEWORK security projection --
            // NEVER re-implemented. Same non-obsolete overload ViewMetadataProjector already uses for
            // the read-side AllowWrite trim: IsGrantedExtensions.CanWrite(this IRequestSecurityStrategy
            // security, Type type, IObjectSpace objectSpace, string memberName = null) -- verified
            // against installed 26.1 source (IsGrantedExtensions.cs:179; not [Obsolete]. The sibling
            // (Type, string) overload with no IObjectSpace, line 419, IS
            // [Obsolete(OverloadWithoutObjectSpaceIsObsoleteWarning)] -- ruled out for that reason).
            // Checked BEFORE any resolve/convert/set so a denied member never touches the object and a
            // request mixing an allowed + a denied member never partially applies. security is null only
            // for the HostSharedGatedTypes path (SaveController.OpenWriteContext) -- that whole operation
            // is already admin-gated and has no tenant security context to ask.
            if (security is not null && !security.CanWrite(typeInfo.Type, os, member)) {
                return new ObjectResult(new { error = $"Write denied for member '{member}'." }) { StatusCode = StatusCodes.Status403Forbidden };
            }
            if (value.ValueKind == JsonValueKind.Null) { mi.SetValue(obj, null); continue; }
            // Reference (lookup) member detection -- VERIFIED against installed 26.1 source, not memory:
            // IMemberInfo (DevExpress.ExpressApp/DC/IMemberInfo.cs) has NO "ReferenceType" member at all --
            // that candidate does not exist on this interface (grepped the whole DevExpress.ExpressApp source
            // tree; the only "ReferenceType" hits are on unrelated XPO-internal types). DevExpress's own
            // WebApi OData delta-patch layer solves this exact problem the same way used here:
            // DevExpress.ExpressApp.WebApi/Services/OData/DeltaPatch/ReferenceMemberModifier.cs --
            // CanApply/IsReferenceMember: (member.IsAssociation || (member.IsPersistent &&
            // member.MemberTypeInfo != null && member.MemberTypeInfo.IsPersistent)) && !member.IsList, then
            // resolves via ObjectSpace.GetObjectByKey(member.MemberType, key) -- mirrored below (IsList
            // already excluded above).
            if (mi.IsAssociation || (mi.IsPersistent && mi.MemberTypeInfo is { IsPersistent: true })) {
                var referencedTypeInfo = os.TypesInfo.FindTypeInfo(mi.MemberType);
                var referencedKeyMemberType = referencedTypeInfo.KeyMember.MemberType;
                var rawKey = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                object refKey;
                try { refKey = KeyConverter.Convert(rawKey, referencedKeyMemberType); }
                catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException) {
                    return new BadRequestObjectResult(new { error = $"Malformed key for member '{member}'." });
                }
                var referenced = os.GetObjectByKey(mi.MemberType, refKey);
                if (referenced is null) return new BadRequestObjectResult(new { error = $"No {mi.MemberType.Name} found for member '{member}'." });
                mi.SetValue(obj, referenced);
                continue;
            }
            mi.SetValue(obj, JsonSerializer.Deserialize(value.GetRawText(), mi.MemberType));
        }
        return null;
    }

    // Shared Committing/IValidator wiring + CommitChanges/422 handling -- identical for Save and
    // Create; only the success response differs (200 vs 201+key), so that stays with each caller.
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
        // SVR-003: a unique-constraint violation (e.g. a duplicate JobDefinition.JobTypeName against
        // SVR-002's IX_JobDefinition_JobTypeName) surfaces here as a DbUpdateException. Return it as the
        // SAME structured-JSON shape the 422 path uses, but 409 Conflict -- a clean typed error, not the
        // raw unhandled 500 that previously escaped. One
        // guard, covers Create + Save (update) + all callers. The `when` filter narrows to a genuine
        // unique violation; any OTHER DbUpdateException falls through and propagates, so real DB failures
        // still surface rather than being masked as a 409.
        catch (DbUpdateException dbex) when (IsUniqueViolation(dbex)) {
            return Conflict(new {
                MemberErrors = new Dictionary<string, string>(),
                Messages = new[] { "The value violates a uniqueness constraint (a record with the same key already exists)." }
            });
        }
    }

    // SVR-003: SQL Server reports a unique-index/PK violation as error 2601 (duplicate key row in an
    // object with a unique index) or 2627 (unique constraint). EF Core wraps it in a DbUpdateException
    // whose inner-exception chain carries the Microsoft.Data.SqlClient.SqlException. Verified numbers
    // against SVR-002's live proof (a duplicate JobTypeName insert -> 2601).
    static bool IsUniqueViolation(DbUpdateException ex) {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e is SqlException sql && sql.Number is 2601 or 2627)
                return true;
        return false;
    }
}
