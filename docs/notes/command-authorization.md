# In-action admin gating (when there's no secured ObjectSpace to ask)

**Rule:** any endpoint whose effect does not run through a secured `ObjectSpace` -- because it
mutates data outside the normal per-request security context entirely (a non-secured host object
space, a raw `IObjectSpace.CommitChanges()` bypass, an infrastructure action with no XAF permission
model behind it) -- must carry its own explicit admin check in the action body. There is no framework
permission check left to lean on once you've stepped outside a secured `ObjectSpace`, so the endpoint
has to do it itself.

**Why not `[Authorize(Roles = "Administrators")]`:** this host's JWTs carry no role claims. Verified
against installed 26.1 source: `StandardAuthenticationIdentityCreator.CreateClaims`
(`DevExpress.ExpressApp.Security`) -- the exact path `JwtTokenProviderService`/`SignInManager` use to
build this host's tokens -- emits only `NameIdentifier`/`AuthenticationPassed`/`Name`/`Issuer`(/`LogonParams`)
claims, never a role claim. A `Roles=` attribute would therefore 403 *every* caller, including a real
admin. So the check has to read the actual per-request security user
(`ISecurityStrategyBase.User`/`ISecurityProvider.GetSecurity().User`) and ask its roles directly:
`user is PermissionPolicyUser caller && caller.Roles.Any(r => r.IsAdministrative)` -- the same flag
XAF's own security engine uses to grant a role full access, asked here rather than reimplemented.

## Examples in this codebase

1. **`TestFixturesController.EnsureRestrictedRole` / `DeleteObject`** -- these endpoints mutate
   security data (roles/users) or delete arbitrary rows purely to seed/clean up test fixtures, gated
   twice: `IWebHostEnvironment.IsDevelopment()` (404 outside Development) and the `IsAdministrative`
   check above (403 for a non-admin caller). See its header comment for the full rationale.

2. **`SaveController.OpenWriteContext`** (SVR-001 Task 2.3) -- `JobDefinition` is a host-shared BO
   (`Startup.WithSharedBusinessObjects`). Shared types are read-only from any tenant-resolved request
   by DevExpress design (dxdocs "Shared Data Support in a Multi-Tenant Application": "the Web API
   service does not allow authorization using the host account"; confirmed in installed 26.1 source --
   `MultiTenantReadOnlySelectDataSecurity.IsGranted` returns `false` for every non-Read/Navigate
   operation, unconditionally, for every type/user, *replacing* the normal `PermissionPolicyRole` check
   rather than running alongside it). The only writable path is a HOST-context object space (a fresh
   DI scope where `ITenantProvider.TenantId` was never set -- mirrors `PrefsController.HostObjectSpace`)
   -- which has no logged-on tenant user, so there is no framework permission to ask. `OpenWriteContext`
   gates entry to that path with the same `IsAdministrative` check before opening the host object space,
   and `ApplyChanges`/`Delete` skip their normal per-member `CanWrite`/`CanDelete` calls for this path
   (passing `security: null`) since there is no tenant security context left to consult there either.
