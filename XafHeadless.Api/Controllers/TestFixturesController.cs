using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using OutlookInspiredDemo.Module.BusinessObjects;
using static DevExpress.ExpressApp.Security.SecurityOperations;
using static DevExpress.Persistent.Base.SecurityPermissionState;

namespace XafHeadless.Api.Controllers;

// PH2 two-role trimming fixture. The demo module's own seeded roles (department roles in the
// read-only Updater.cs) never deny a single MEMBER of Order -- they grant/deny whole types (e.g.
// Sales gets full Order CRUD; HR/Management/Engineering deny Order entirely) -- so there is no
// existing restricted-role fixture to reuse for the ListView/DetailView member-trimming tests.
// This host's own DB is disposable (plan's Global Constraints permit committing test fixtures), so
// this idempotent endpoint creates them on demand instead of an uncommitted scratch console seeder:
// role HeadlessDemo_Restricted (type Read Allow on Order, member Read Deny on RestrictedDeniedMember)
// + user restricted@company1.com (blank password, tenant-email form so TenantByEmailResolver routes
// it to the same tenant DB as Admin@company1.com).
//
// Review fix (defense-in-depth, PH2 review finding #1): this endpoint mutates security data (creates
// a role + a user) purely to seed a black-box test fixture and must never be reachable in a real
// deployment, so it is gated twice:
//   a) Environment: 404 outside Development (checked first, so a prod caller can't even tell the
//      route exists -- launchSettings.json's only profile sets ASPNETCORE_ENVIRONMENT=Development,
//      so `dotnet run` for tests is Development and this gate is a no-op there; a real deployment
//      would run Production/Staging and get 404).
//   b) Role: NOT via [Authorize(Roles="Administrators")]. Verified, not assumed: read
//      StandardAuthenticationIdentityCreator.CreateClaims (DevExpress.ExpressApp.Security, the exact
//      path JwtTokenProviderService/SignInManager use to build this host's JWTs) -- it emits only
//      NameIdentifier/AuthenticationPassed/Name/Issuer(/LogonParams) claims, never a role claim. A
//      Roles= attribute would therefore 403 EVERY caller, including the Admin fixture-seeder itself,
//      silently breaking this endpoint. So the admin check is explicit instead: the demo's own
//      "Administrators" role (DatabaseUpdate/Updater.cs: EnsureRole("Administrators", isAdmin: true))
//      is asked for via ISecurityStrategyBase.User (the real per-request security user, populated by
//      the RequireXafAuthentication() policy before this action runs) -- PermissionPolicyRole
//      .IsAdministrative is the same flag XAF's own security engine uses to grant a role full access,
//      asked here rather than reimplemented.
[ApiController, Route("api/test-fixtures"), Authorize]
public class TestFixturesController : ControllerBase {
    public const string RestrictedRoleName = "HeadlessDemo_Restricted";
    public const string RestrictedUserName = "restricted@company1.com";
    // Test-side constant (KnownModel.RestrictedDeniedMember, XafHeadless.Api.Tests) must be kept in
    // lockstep with this value -- there is no shared reference across the black-box test boundary,
    // so a drift here breaks the trimming/denial assertions loudly (wrong/no member trimmed, tests
    // fail), never silently.
    public const string RestrictedDeniedMember = "TotalAmount"; // real Order_ListView column + Order_DetailView item

    // PH2-002: same role, additive member-level WRITE deny (read on this member is untouched -- still
    // Allow via the type-level Read grant). Mirrors RestrictedDeniedMember's lockstep-constant
    // contract with KnownModel.RestrictedWriteDeniedMember, XafHeadless.Api.Tests.
    public const string RestrictedWriteDeniedMember = "PONumber"; // real writable Order scalar member

    // ponytail: single-instance host only (matches this POC's deployment) -- a plain in-process lock
    // closes the check-then-act window for any direct call to this endpoint. Swap for a distributed
    // lock (SQL app lock / Redis) if this host is ever scaled to more than one instance.
    static readonly object SeedLock = new();

    // GAP-003: cleanup endpoint for create-flow tests (SaveCreateTests.Create_Order_..._persists needs
    // a real way to delete the Order it creates). Deliberately a SEPARATE, narrower allowlist from
    // SaveController.ExposedTypes -- this is test infra, not a general delete API, so it only names the
    // one type a test actually needs to delete (Order; Employee is [ForbidDelete] and never needed here
    // since its create test 422s before anything commits).
    static readonly Dictionary<string, Type> DeletableTypes = new(StringComparer.OrdinalIgnoreCase) {
        [nameof(Order)] = typeof(Order),
    };

    readonly IObjectSpaceFactory objectSpaceFactory;
    readonly ISecurityStrategyBase security;
    readonly IWebHostEnvironment environment;
    public TestFixturesController(IObjectSpaceFactory objectSpaceFactory, ISecurityStrategyBase security, IWebHostEnvironment environment) {
        this.objectSpaceFactory = objectSpaceFactory;
        this.security = security;
        this.environment = environment;
    }

    [HttpPost("restricted-role")]
    public IActionResult EnsureRestrictedRole() {
        if (!environment.IsDevelopment()) return NotFound();
        if (security.User is not PermissionPolicyUser caller || !caller.Roles.Any(r => r.IsAdministrative)) return Forbid();

        lock (SeedLock) {
            using var os = objectSpaceFactory.CreateObjectSpace(typeof(PermissionPolicyRole));
            var role = os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == RestrictedRoleName);
            if (role is null) {
                role = os.CreateObject<PermissionPolicyRole>();
                role.Name = RestrictedRoleName;
                role.AddTypePermission<Order>(Read, Allow);
                role.AddMemberPermission<Order>(Read, RestrictedDeniedMember, null, Deny);
            }
            // PH2-002: type-level Write:Allow, ensured UNCONDITIONALLY (safe to call every run --
            // AddTypePermission only sets a state on the type's single permission object, it never
            // duplicates rows, unlike AddMemberPermission below). Empirically verified this is required
            // for the member-write-deny to mean anything as a MEMBER-level test: with no type-level
            // Write grant at all, EVERY member write already 403s at commit time ("Saving the '...'
            // object is prohibited by security rules") regardless of which member -- probed live via
            // curl against the running host (PONumber AND the unrelated InvoiceNumber both 403'd
            // identically before this line existed). Granting Write:Allow here makes the member-level
            // deny below the ONLY thing blocking that one member, so the 403/200 split in
            // SaveReferenceAndEnumTests actually proves PER-MEMBER enforcement, not a blanket
            // type-level block.
            role.AddTypePermission<Order>(Write, Allow);
            // PH2-002: ensure the member-WRITE-deny exists even for a role seeded by an EARLIER host
            // run against this same disposable DB (the role-existence check above is what makes this
            // whole endpoint idempotent across restarts). AddMemberPermission always CREATES a new row
            // (PermissionSettingHelper.cs, DevExpress 26.1 source) -- nesting this call inside the
            // "role is null" branch above would silently skip seeding it for any role that already
            // existed before this change shipped, so it is guarded by its own existence check instead.
            var orderTypePermission = role.FindFirstTypePermission<Order>();
            // Guard matches on member NAME only, not `operations` -- fine today since this member only
            // ever gets one Deny permission (Write), but it would false-positive (skip re-seeding) if a
            // future change added a second, differently-scoped permission (e.g. Read Deny) on this same
            // member: the Any() would see it and wrongly assume the Write deny already exists too.
            if (orderTypePermission?.MemberPermissions.Any(mp => mp.Members == RestrictedWriteDeniedMember) != true) {
                role.AddMemberPermission<Order>(Write, RestrictedWriteDeniedMember, null, Deny);
            }
            // SVR-001 Task 2.3: explicit Delete:Deny. Unlike Write (denied by default when no
            // type-level permission is set for it -- see the Write:Allow comment above), Delete
            // defaulted to ALLOW when left unspecified on this role (empirically discovered: an
            // earlier version of the DELETE-route test deleted a real Order row as Restricted with
            // no Delete permission set at all -- see docs/DEVIATIONS.md). So this needs an explicit
            // Deny, not an Allow, to give the DELETE route's CanDelete gate a real 403 case. Ensured
            // UNCONDITIONALLY, same as Write:Allow (AddTypePermission never duplicates rows).
            role.AddTypePermission<Order>(Delete, Deny);
            var user = os.FirstOrDefault<ApplicationUser>(u => u.UserName == RestrictedUserName);
            if (user is null) {
                user = os.CreateObject<ApplicationUser>();
                user.UserName = RestrictedUserName;
                os.CommitChanges(); // materialize the key before CreateUserLoginInfo needs it (mirrors demo Updater.EnsureUser)
                ((ISecurityUserWithLoginInfo)user).CreateUserLoginInfo(
                    SecurityDefaults.PasswordAuthentication, os.GetKeyValueAsString(user));
                user.Roles.Add(role);
            }
            os.CommitChanges();
        }
        return Ok();
    }

    // GAP-003: gated test-cleanup delete. Same env+admin double-gate as EnsureRestrictedRole above (see
    // its header comment for why role is checked via ISecurityStrategyBase.User rather than
    // [Authorize(Roles=...)]) -- this mutates real data too, so it gets the same defense-in-depth.
    [HttpDelete("{type}/{key}")]
    public IActionResult DeleteObject(string type, string key) {
        if (!environment.IsDevelopment()) return NotFound();
        if (security.User is not PermissionPolicyUser caller || !caller.Roles.Any(r => r.IsAdministrative)) return Forbid();
        if (!DeletableTypes.TryGetValue(type, out var clrType)) return NotFound();

        using var os = objectSpaceFactory.CreateObjectSpace(clrType);
        var keyMemberType = os.TypesInfo.FindTypeInfo(clrType).KeyMember.MemberType;
        object routeKey;
        try { routeKey = KeyConverter.Convert(key, keyMemberType); }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException) {
            return NotFound(); // test infra, not a client-facing contract -- malformed key is just "not found", kept minimal per brief
        }
        var obj = os.GetObjectByKey(clrType, routeKey);
        if (obj is null) return NotFound();

        os.Delete(obj); // verified: void Delete(Object obj) on IObjectSpace (IObjectSpace.cs)
        os.CommitChanges();
        return Ok();
    }
}
