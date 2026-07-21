using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// Task 2.3: the new validating DELETE api/save/{type}/{key} route on SaveController -- a SEPARATE
// path from OData DELETE (which stays 405-blocked by ODataReadOnlyMiddleware; see ODataWriteGuardTests).
// Shape mirrors Save/Create: allowlist -> key conversion -> lookup -> CanDelete gate -> delete + commit.
//
// JobDefinition (host-shared BO) create/update/delete goes through SaveController.OpenWriteContext's
// admin-gated HOST-context object space instead of the normal per-object CanWrite/CanDelete path --
// see docs/notes/command-authorization.md. Restricted (a real, non-admin tenant user) proves the gate;
// Admin proves the round trip actually works end to end.
//
// The permission-denied pair below is now safe: Restricted's Order role fixture gained an explicit
// Delete:Deny (TestFixturesController.EnsureRestrictedRole) -- Delete does NOT default-deny when
// unspecified the way Write does (see docs/DEVIATIONS.md for the empirical finding, which cost one
// real demo Order row the first time this was tried). Both halves below operate on a THROWAWAY Order
// created via the already-proven Create path, never a probed real row, so this is safe regardless of
// what the permission default turns out to be for any future type.
//
// TEST-001: creates/deletes real (throwaway) rows. [DoNotParallelize] keeps it out of the assembly's
// MethodLevel parallel pool, same reasoning as SaveCreateTests / SaveReferenceAndEnumTests.
[TestClass]
[DoNotParallelize]
public class SaveDeleteTests : TestBase {
    [TestMethod]
    public async Task Delete_returns_404_for_unexposed_type() {
        var client = await GetClientAsync("Admin");
        var resp = await client.DeleteAsync($"api/save/DoesNotExist/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [TestMethod]
    public async Task Delete_returns_404_for_unknown_key() {
        var client = await GetClientAsync("Admin");
        var resp = await client.DeleteAsync($"api/save/JobDefinition/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [TestMethod]
    public async Task Delete_returns_400_for_malformed_key() {
        var client = await GetClientAsync("Admin");
        var resp = await client.DeleteAsync("api/save/JobDefinition/not-a-guid");
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // Restricted now carries an explicit Delete:Deny on Order (TestFixturesController fixture) -- a
    // real 403 case for the CanDelete gate. Admin control proves the identical code path succeeds
    // absent the permission gate. Both operate on a throwaway row created for this test only.
    [TestMethod]
    public async Task Delete_denied_for_restricted_role_and_204_for_admin_control() {
        var admin = await GetClientAsync("Admin");
        var createResp = await admin.PostAsJsonAsync("api/save/Order", new Dictionary<string, object?> {
            [KnownModel.OrderScalarMember] = $"PO-{Guid.NewGuid():N}",
        });
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"expected 201, got {createResp.StatusCode}: {await createResp.Content.ReadAsStringAsync()}");
        var key = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString();

        var restricted = await GetClientAsync("Restricted");
        var restrictedResp = await restricted.DeleteAsync($"api/save/Order/{key}");
        Assert.AreEqual(HttpStatusCode.Forbidden, restrictedResp.StatusCode,
            $"expected 403, got {restrictedResp.StatusCode}: {await restrictedResp.Content.ReadAsStringAsync()}");
        Assert.AreEqual("application/json", restrictedResp.Content.Headers.ContentType?.MediaType,
            "403 must be SaveController's own structured JSON error");

        var adminDeleteResp = await admin.DeleteAsync($"api/save/Order/{key}");
        Assert.AreEqual(HttpStatusCode.NoContent, adminDeleteResp.StatusCode,
            $"expected 204 (control), got {adminDeleteResp.StatusCode}: {await adminDeleteResp.Content.ReadAsStringAsync()}");
    }

    // The manual round trip the brief asked for, as an automated test: Admin creates, updates, then
    // deletes a JobDefinition through the admin-gated host-context path -- proves all three operations
    // (not just Delete) actually work now that OpenWriteContext routes this type correctly.
    [TestMethod]
    public async Task Admin_can_create_update_and_delete_a_JobDefinition() {
        var admin = await GetClientAsync("Admin");
        var name = $"Test-{Guid.NewGuid():N}";

        // SVR-003: JobTypeName defaults to "EmailOrdersReport" which now collides with the seeded row's
        // IX_JobDefinition_JobTypeName unique index -- send a unique value so this create is a clean 201.
        var createResp = await admin.PostAsJsonAsync("api/save/JobDefinition",
            new Dictionary<string, object?> { ["Name"] = name, ["JobTypeName"] = $"TestJob-{Guid.NewGuid():N}" });
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"expected 201, got {createResp.StatusCode}: {await createResp.Content.ReadAsStringAsync()}");
        var key = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString();

        var updateResp = await admin.PostAsJsonAsync($"api/save/JobDefinition/{key}",
            new Dictionary<string, object?> { ["IsEnabled"] = true });
        Assert.AreEqual(HttpStatusCode.OK, updateResp.StatusCode,
            $"expected 200, got {updateResp.StatusCode}: {await updateResp.Content.ReadAsStringAsync()}");
        // SVR-003: read the full entity via the single-entity ({key}) segment (that path already worked);
        // $select is dropped -- it hits a separate unfixed edmModel serialization bug on host-shared types
        // (see docs/DEVIATIONS.md).
        var reread = await admin.GetFromJsonAsync<JsonElement>($"api/odata/JobDefinition({key})");
        Assert.IsTrue(reread.GetProperty("IsEnabled").GetBoolean(), "update did not persist");

        var deleteResp = await admin.DeleteAsync($"api/save/JobDefinition/{key}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResp.StatusCode,
            $"expected 204, got {deleteResp.StatusCode}: {await deleteResp.Content.ReadAsStringAsync()}");
        var gone = await admin.GetAsync($"api/odata/JobDefinition({key})");
        Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode, "deleted JobDefinition must be gone");
    }

    // SVR-003: creating a JobDefinition whose JobTypeName collides with the seeded "EmailOrdersReport"
    // row violates SVR-002's IX_JobDefinition_JobTypeName unique index. SaveController.CommitWithValidation
    // now catches that DbUpdateException and returns 409 Conflict with the structured-JSON error shape
    // (was an unhandled 500). The commit fails, so no row
    // is created and there is nothing to clean up.
    [TestMethod]
    public async Task Create_duplicate_JobTypeName_returns_409_conflict() {
        var admin = await GetClientAsync("Admin");
        var resp = await admin.PostAsJsonAsync("api/save/JobDefinition",
            new Dictionary<string, object?> { ["Name"] = $"Dup-{Guid.NewGuid():N}", ["JobTypeName"] = "EmailOrdersReport" });
        Assert.AreEqual(HttpStatusCode.Conflict, resp.StatusCode,
            $"expected 409, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        Assert.AreEqual("application/json", resp.Content.Headers.ContentType?.MediaType,
            "409 must be SaveController's own structured JSON error");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("Messages", out var messages) && messages.GetArrayLength() > 0,
            "409 body must carry the structured { MemberErrors, Messages } shape");
    }

    // Restricted is a real, non-admin tenant user (see TestFixturesController) -- proves
    // OpenWriteContext's admin gate actually gates, for all three JobDefinition operations, not just
    // one. Nothing is created on the Create/Delete 403 paths (the gate runs before any object space is
    // opened); the Update path needs a real row to attempt against, created/cleaned up by Admin.
    [TestMethod]
    public async Task Restricted_role_is_denied_403_on_all_JobDefinition_write_operations() {
        var restricted = await GetClientAsync("Restricted");

        var createResp = await restricted.PostAsJsonAsync("api/save/JobDefinition",
            new Dictionary<string, object?> { ["Name"] = $"ShouldNotExist-{Guid.NewGuid():N}" });
        Assert.AreEqual(HttpStatusCode.Forbidden, createResp.StatusCode);

        var admin = await GetClientAsync("Admin");
        // SVR-003: unique JobTypeName so this admin helper create doesn't collide with the seeded row.
        var adminCreateResp = await admin.PostAsJsonAsync("api/save/JobDefinition",
            new Dictionary<string, object?> { ["Name"] = $"Test-{Guid.NewGuid():N}", ["JobTypeName"] = $"TestJob-{Guid.NewGuid():N}" });
        var key = (await adminCreateResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString();
        try {
            var updateResp = await restricted.PostAsJsonAsync($"api/save/JobDefinition/{key}",
                new Dictionary<string, object?> { ["IsEnabled"] = true });
            Assert.AreEqual(HttpStatusCode.Forbidden, updateResp.StatusCode);

            var deleteResp = await restricted.DeleteAsync($"api/save/JobDefinition/{key}");
            Assert.AreEqual(HttpStatusCode.Forbidden, deleteResp.StatusCode);
        } finally {
            await admin.DeleteAsync($"api/save/JobDefinition/{key}");
        }
    }
}
