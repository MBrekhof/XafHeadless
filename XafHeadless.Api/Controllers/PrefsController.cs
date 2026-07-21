using System.Text;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XafHeadless.Api.BusinessObjects;

namespace XafHeadless.Api.Controllers;

// GAP-008: per-user, per-view layout prefs. The server-side design-intent store (prefs follow the user
// across devices/render-modes), keyed by the authenticated identity + viewId.
//
// IDENTITY (verified against installed 26.1 source; NEVER taken from the request):
//   security.UserId is ISecurityStrategyBase.UserId (ISecurity.cs) => SecurityStrategy.UserId
//   (SecurityStrategy.cs:182) = LogonObjectSpace.GetKeyValue(User) -- the framework-computed key of the
//   current logged-on security user. UseIntegratedMode wires SecurityStrategyComplex : SecurityStrategy,
//   which inherits it. Confirmed live: the JWT's NameIdentifier == this Guid. Keying by (userKey, viewId)
//   with globally-unique XAF Guid user keys is safe multi-tenant.
//
// STORAGE (verified against installed 26.1 source -- the crux of this task):
//   UserLayoutPref is a HOST/shared BO (Startup.WithSharedBusinessObjects), so its table lives in the
//   host catalog. BUT XAF multi-tenancy makes shared/host BOs strictly READ-ONLY from a tenant-resolved
//   request: MultiTenantObjectSpaceFactory.CreateNonSecuredObjectSpace sets
//   MultiTenantSecurityOptions.IsReadOnlyAccessToSharedDataInHostDatabase = true and
//   MultiTenantReadOnlySelectDataSecurity grants shared types Read/Navigate only (write/delete ->
//   FalseCriteria). A plain objectSpaceFactory.CreateObjectSpace(typeof(UserLayoutPref)) on the request
//   scope therefore 403s the WRITE ("prohibited by security rules"), even for an administrator.
//   The writable host path exists only in a HOST context (ITenantProvider.TenantId == null). ITenantProvider
//   is registered AddScoped (MultiTenancyCoreStartupExtensions.cs:50), so a FRESH DI scope starts with
//   TenantId == null -> host context -> the non-secured host object space provider is active and writable.
//   That is exactly what HostObjectSpace() below creates. The per-user security boundary is the userKey
//   filter (from ISecurityStrategyBase), not row-level XAF security -- appropriate for an infrastructure
//   prefs table, and it never re-implements storage/security (both are IObjectSpace + ISecurityStrategyBase).
[ApiController, Route("api/prefs"), Authorize]
public class PrefsController : ControllerBase {
    // GAP-008-minors #2: grid-layout blobs are small (a handful of columns' Width/VisibleIndex/etc.);
    // 64 KB is generous headroom while still rejecting an unbounded/malicious body before it ever
    // reaches nvarchar(max).
    const int MaxBlobBytes = 64 * 1024;

    readonly IServiceScopeFactory scopeFactory;
    readonly ISecurityStrategyBase security;
    public PrefsController(IServiceScopeFactory scopeFactory, ISecurityStrategyBase security) {
        this.scopeFactory = scopeFactory;
        this.security = security;
    }

    // GET api/prefs/{viewId} -> the current user's saved blob for that view, or 204 if none.
    [HttpGet("{viewId}")]
    public IActionResult Get(string viewId) {
        if (UserKey() is not { } userKey) return Unauthorized();
        using var scope = scopeFactory.CreateScope();
        using var os = HostObjectSpace(scope);
        var pref = os.FirstOrDefault<UserLayoutPref>(p => p.UserKey == userKey && p.ViewId == viewId);
        return string.IsNullOrEmpty(pref?.PrefsJson)
            ? NoContent()
            : Content(pref!.PrefsJson, "application/json");
    }

    // PUT api/prefs/{viewId} (raw body = the layout blob) -> upsert one row per (user, view). An empty
    // body clears the pref (so a later GET returns 204 -> the client falls back to the default layout).
    [HttpPut("{viewId}")]
    public async Task<IActionResult> Put(string viewId) {
        if (UserKey() is not { } userKey) return Unauthorized();
        string body;
        using (var reader = new StreamReader(Request.Body)) body = await reader.ReadToEndAsync();

        // GAP-008-minors #2: size cap BEFORE touching the DB.
        if (Encoding.UTF8.GetByteCount(body) > MaxBlobBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { error = $"prefs blob exceeds the {MaxBlobBytes}-byte cap" });

        // GAP-008-minors #1: with the new (UserKey, ViewId) unique index, a concurrent first-write
        // (two requests both find no existing row, both CreateObject + commit) can lose the DB-level
        // race -> the loser's CommitChanges throws DbUpdateException (unique-violation). Handle it as
        // an idempotent upsert: re-read on a FRESH object space (the winner's row is now committed and
        // visible) and UPDATE it instead of inserting a duplicate. One retry is enough -- a second
        // collision in the same request is not a realistic scenario worth looping over.
        using (var scope = scopeFactory.CreateScope())
        using (var os = HostObjectSpace(scope)) {
            Upsert(os, userKey, viewId, body);
            try {
                os.CommitChanges();
                return Ok();
            } catch (DbUpdateException) {
                // fall through to the retry below with a fresh scope/object space.
            }
        }
        using (var retryScope = scopeFactory.CreateScope())
        using (var retryOs = HostObjectSpace(retryScope)) {
            Upsert(retryOs, userKey, viewId, body);
            retryOs.CommitChanges();
        }
        return Ok();
    }

    static void Upsert(IObjectSpace os, string userKey, string viewId, string body) {
        var pref = os.FirstOrDefault<UserLayoutPref>(p => p.UserKey == userKey && p.ViewId == viewId)
                   ?? os.CreateObject<UserLayoutPref>();
        pref.UserKey = userKey;   // set on both branches: CreateObject leaves it empty on the insert path
        pref.ViewId = viewId;
        pref.PrefsJson = body;
    }

    // A writable, non-secured object space over the HOST DB. Created from a fresh scope (TenantId == null)
    // so it is a host context, not the request's tenant context (where shared BOs are read-only).
    static IObjectSpace HostObjectSpace(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>()
            .CreateNonSecuredObjectSpace(typeof(UserLayoutPref));

    // Current user's stable key from the framework -- NEVER from the request. Read from the REQUEST-scoped
    // security (tenant context), which is where the user is actually logged on.
    string? UserKey() => security.UserId?.ToString();
}
