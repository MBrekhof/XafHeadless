using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// GAP-001: reference (lookup) member writes on SaveController, the enum-write proof, and the two
// folded PH2-002 400 (not 500) hardening bits on this exact code path. Sibling to
// SaveValidationTests.cs (same TestBase pattern: GetClientAsync("Admin"), KnownModel constants,
// OData reads to pick real rows, restore any mutation in a finally).
// TEST-001: this class mutates+restores shared Order rows (PONumber, InvoiceNumber, Customer,
// ShipmentStatus). [DoNotParallelize] takes it (and every test in it) out of the assembly's
// MethodLevel parallel pool entirely -- MSTest never runs a DoNotParallelize class concurrently
// with any other test, so no two mutating tests (in this class, in SaveCreateTests, or in any
// future one) can race on the same row/member. Read-only metadata suites are unaffected and stay
// parallel.
[TestClass]
[DoNotParallelize]
public class SaveReferenceAndEnumTests : TestBase {
    // Root-cause bug (docs/HOW-TO-IMPLEMENT.md gotcha 12): the incoming scalar FK key for a reference
    // member used to be JSON-deserialized straight into the referenced BO type
    // (JsonSerializer.Deserialize(scalarKey, typeof(Customer))) -> 500. It must instead be resolved as
    // a key against the SAME IObjectSpace via GetObjectByKey. Proven end-to-end: 200 + persisted.
    [TestMethod]
    public async Task SaveController_reference_member_write_persists_new_customer() {
        var client = await GetClientAsync("Admin");
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=50&$orderby={KnownModel.OrderColumn1} asc&$select={KnownModel.OrderKeyMember}&$expand={KnownModel.OrderReferenceMember}($select={KnownModel.OrderReferenceMemberKeyMember})");
        var rows = page.GetProperty("value").EnumerateArray().ToList();
        Assert.IsGreaterThan(1, rows.Count, "need at least two Orders to find two different customers");

        var orderA = rows[0];
        var orderAId = orderA.GetProperty(KnownModel.OrderKeyMember).GetString();
        var customerAId = orderA.GetProperty(KnownModel.OrderReferenceMember).GetProperty(KnownModel.OrderReferenceMemberKeyMember).GetString();
        var orderB = rows.FirstOrDefault(r =>
            r.GetProperty(KnownModel.OrderReferenceMember).GetProperty(KnownModel.OrderReferenceMemberKeyMember).GetString() != customerAId);
        Assert.AreNotEqual(JsonValueKind.Undefined,
orderB.ValueKind, "could not find an Order with a different Customer in the first 50 rows");
        var customerBId = orderB.GetProperty(KnownModel.OrderReferenceMember).GetProperty(KnownModel.OrderReferenceMemberKeyMember).GetString();

        var resp = await client.PostAsJsonAsync($"api/save/Order/{orderAId}",
            new Dictionary<string, object?> { [KnownModel.OrderReferenceMember] = customerBId });
        try {
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
                $"expected 200, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

            var reread = await client.GetFromJsonAsync<JsonElement>(
                $"api/odata/Order({orderAId})?$expand={KnownModel.OrderReferenceMember}($select={KnownModel.OrderReferenceMemberKeyMember})");
            Assert.AreEqual(customerBId,
                reread.GetProperty(KnownModel.OrderReferenceMember).GetProperty(KnownModel.OrderReferenceMemberKeyMember).GetString(),
                "reference member write did not persist");
        } finally {
            await client.PostAsJsonAsync($"api/save/Order/{orderAId}",
                new Dictionary<string, object?> { [KnownModel.OrderReferenceMember] = customerAId });
        }
    }

    // Expectation per brief: the demo enum types carry [JsonConverter(typeof(JsonStringEnumConverter))]
    // on the enum type itself, so the existing JsonSerializer.Deserialize line already honors string
    // names -- zero code change needed for this. This test exists to PROVE that, not to justify adding
    // speculative enum-parsing code.
    [TestMethod]
    public async Task SaveController_enum_member_write_persists_string_value() {
        var client = await GetClientAsync("Admin");
        var first = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=1&$orderby={KnownModel.OrderColumn1} asc&$select={KnownModel.OrderKeyMember},{KnownModel.OrderEnumMember}");
        var row = first.GetProperty("value")[0];
        var key = row.GetProperty(KnownModel.OrderKeyMember).GetString();
        var originalStatus = ReadEnumAsString(row, KnownModel.OrderEnumMember);
        var newStatus = originalStatus == KnownModel.OrderEnumValueAwaiting
            ? KnownModel.OrderEnumValueTransit : KnownModel.OrderEnumValueAwaiting;

        var resp = await client.PostAsJsonAsync($"api/save/Order/{key}",
            new Dictionary<string, object?> { [KnownModel.OrderEnumMember] = newStatus });
        try {
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
                $"expected 200, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

            var reread = await client.GetFromJsonAsync<JsonElement>(
                $"api/odata/Order({key})?$select={KnownModel.OrderEnumMember}");
            Assert.AreEqual(newStatus, ReadEnumAsString(reread, KnownModel.OrderEnumMember),
                "enum member write did not persist");
        } finally {
            await client.PostAsJsonAsync($"api/save/Order/{key}",
                new Dictionary<string, object?> { [KnownModel.OrderEnumMember] = originalStatus });
        }
    }

    // PH2-002 fold, bit 1: an FK key the client sent that resolves to no object (a genuinely
    // nonexistent Customer) is a client error, not a server fault -- 400, not 500. Nothing is
    // committed on this path (the controller returns before CommitChanges), so no restore needed.
    [TestMethod]
    public async Task SaveController_returns_400_for_unresolvable_reference_key() {
        var client = await GetClientAsync("Admin");
        var first = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=1&$select={KnownModel.OrderKeyMember}");
        var key = first.GetProperty("value")[0].GetProperty(KnownModel.OrderKeyMember).GetString();

        var resp = await client.PostAsJsonAsync($"api/save/Order/{key}",
            new Dictionary<string, object?> { [KnownModel.OrderReferenceMember] = Guid.NewGuid().ToString() });

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // PH2-002 fold, bit 2: the route-level KeyConverter.Convert(key, typeInfo.KeyMember.MemberType)
    // used to throw (Guid.Parse) on a non-Guid route key -> 500. Must be 400. No object is ever
    // looked up on this path, so no restore needed.
    [TestMethod]
    public async Task SaveController_returns_400_for_malformed_route_key() {
        var client = await GetClientAsync("Admin");
        var resp = await client.PostAsJsonAsync("api/save/Order/not-a-guid",
            new Dictionary<string, object?>());
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // Fix pass regression guard: the restructured loop routes EVERY member through the new
    // reference-detection predicate before falling through to the ordinary-scalar
    // JsonSerializer.Deserialize path. Prove a plain non-null scalar member (not a reference, not an
    // enum) still writes.
    [TestMethod]
    public async Task SaveController_scalar_member_write_persists_new_value() {
        var client = await GetClientAsync("Admin");
        var first = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=1&$orderby={KnownModel.OrderColumn1} asc&$select={KnownModel.OrderKeyMember},{KnownModel.OrderScalarMember}");
        var row = first.GetProperty("value")[0];
        var key = row.GetProperty(KnownModel.OrderKeyMember).GetString();
        var originalValue = row.GetProperty(KnownModel.OrderScalarMember).ValueKind == JsonValueKind.Null
            ? null : row.GetProperty(KnownModel.OrderScalarMember).GetString();
        var newValue = $"PO-{Guid.NewGuid():N}";

        var resp = await client.PostAsJsonAsync($"api/save/Order/{key}",
            new Dictionary<string, object?> { [KnownModel.OrderScalarMember] = newValue });
        try {
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
                $"expected 200, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

            var reread = await client.GetFromJsonAsync<JsonElement>(
                $"api/odata/Order({key})?$select={KnownModel.OrderScalarMember}");
            Assert.AreEqual(newValue, reread.GetProperty(KnownModel.OrderScalarMember).GetString(),
                "scalar member write did not persist");
        } finally {
            await client.PostAsJsonAsync($"api/save/Order/{key}",
                new Dictionary<string, object?> { [KnownModel.OrderScalarMember] = originalValue });
        }
    }

    // Fix pass: exercises the reference-key try/catch (previously untested) with a value that is not
    // a parseable Guid -- must be 400, not 500. No object is committed on this path, so no restore needed.
    [TestMethod]
    public async Task SaveController_returns_400_for_malformed_reference_key() {
        var client = await GetClientAsync("Admin");
        var first = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=1&$select={KnownModel.OrderKeyMember}");
        var key = first.GetProperty("value")[0].GetProperty(KnownModel.OrderKeyMember).GetString();

        var resp = await client.PostAsJsonAsync($"api/save/Order/{key}",
            new Dictionary<string, object?> { [KnownModel.OrderReferenceMember] = "not-a-guid" });

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // PH2-002: an unknown member (typo'd, or simply not on the type) used to be silently skipped
    // (`if (mi is null || mi.IsList) continue;`) -- the client would get a 200 believing the write
    // happened. Now a client error -- 400, naming the member. No restore needed: ApplyChanges returns
    // before os.CommitChanges() is ever reached.
    [TestMethod]
    public async Task SaveController_returns_400_for_unknown_member() {
        var client = await GetClientAsync("Admin");
        var first = await client.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=1&$select={KnownModel.OrderKeyMember}");
        var key = first.GetProperty("value")[0].GetProperty(KnownModel.OrderKeyMember).GetString();

        var resp = await client.PostAsJsonAsync($"api/save/Order/{key}",
            new Dictionary<string, object?> { ["NotARealMember"] = "x" });

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"expected 400, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    // PH2-002: per-member CanWrite, asked via the FRAMEWORK security projection -- the same non-obsolete
    // IsGrantedExtensions.CanWrite(security, type, objectSpace, memberName) overload ViewMetadataProjector
    // already uses for the read-side AllowWrite trim (IsGrantedExtensions.cs:179, verified against
    // installed 26.1 source -- not [Obsolete]; the (Type, string) overload without an IObjectSpace at
    // line 419 IS [Obsolete(OverloadWithoutObjectSpaceIsObsoleteWarning)]). TestFixturesController's
    // restricted role now ALSO denies WRITE on KnownModel.RestrictedWriteDeniedMember ("PONumber");
    // Admin carries no such deny -- same body, same key, opposite outcome proves the check is real
    // (not e.g. always-403 or always-200). Restricted's 403 short-circuits before os.CommitChanges(),
    // so no restore is needed for that half; Admin's 200 half does mutate, so it restores in finally.
    //
    // Content-Type assertion is load-bearing, not decorative: DevExpress's OWN commit-time security
    // enforcement ALREADY 403s this exact write with zero changes to SaveController.cs (discovered
    // live via curl, not assumed) -- but as a raw `text/plain` message ("Saving the '...Order.PONumber'
    // property is prohibited by security rules."), not this API's `{ error: "..." }` JSON contract
    // every other 400/403/422 on this controller uses. The status-code-only assertion alone would
    // pass against the UNMODIFIED controller and prove nothing; asserting the structured JSON contract
    // is what's actually new and what makes this a valid red/green TDD pair.
    [TestMethod]
    public async Task SaveController_write_denied_member_returns_403_for_restricted_and_200_for_admin_control() {
        var admin = await GetClientAsync("Admin");
        var first = await admin.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=1&$orderby={KnownModel.OrderColumn1} asc&$select={KnownModel.OrderKeyMember},{KnownModel.RestrictedWriteDeniedMember}");
        var row = first.GetProperty("value")[0];
        var key = row.GetProperty(KnownModel.OrderKeyMember).GetString();
        var originalValue = row.GetProperty(KnownModel.RestrictedWriteDeniedMember).ValueKind == JsonValueKind.Null
            ? null : row.GetProperty(KnownModel.RestrictedWriteDeniedMember).GetString();

        var restricted = await GetClientAsync("Restricted");
        var restrictedResp = await restricted.PostAsJsonAsync($"api/save/Order/{key}",
            new Dictionary<string, object?> { [KnownModel.RestrictedWriteDeniedMember] = "denied-write-attempt" });
        Assert.AreEqual(HttpStatusCode.Forbidden, restrictedResp.StatusCode,
            $"expected 403, got {restrictedResp.StatusCode}: {await restrictedResp.Content.ReadAsStringAsync()}");
        Assert.AreEqual("application/json", restrictedResp.Content.Headers.ContentType?.MediaType,
            "403 must be SaveController's own structured JSON error, not the framework's raw-text commit-time message");
        var restrictedBody = await restrictedResp.Content.ReadFromJsonAsync<JsonElement>();
        // Asserted non-null separately (was CS8604): a null "error" would otherwise fail as a confusing
        // empty-string mismatch instead of naming the actual problem.
        var restrictedError = restrictedBody.GetProperty("error").GetString();
        Assert.IsNotNull(restrictedError, "403 body must carry an error string");
        Assert.Contains(KnownModel.RestrictedWriteDeniedMember, restrictedError,
            "403 body must name the denied member");

        var newValue = $"PO-{Guid.NewGuid():N}";
        var adminResp = await admin.PostAsJsonAsync($"api/save/Order/{key}",
            new Dictionary<string, object?> { [KnownModel.RestrictedWriteDeniedMember] = newValue });
        try {
            Assert.AreEqual(HttpStatusCode.OK, adminResp.StatusCode,
                $"expected 200 (control), got {adminResp.StatusCode}: {await adminResp.Content.ReadAsStringAsync()}");
        } finally {
            await admin.PostAsJsonAsync($"api/save/Order/{key}",
                new Dictionary<string, object?> { [KnownModel.RestrictedWriteDeniedMember] = originalValue });
        }
    }

    // PH2-002 (per-member precision proof): TestFixturesController.EnsureRestrictedRole grants
    // Restricted type-level Write:Allow on Order -- ONLY KnownModel.RestrictedWriteDeniedMember is
    // denied at the member level. Without the type-level Write:Allow grant, Restricted would already
    // be blanket-denied ALL Order writes (verified live via curl before this fixture change: both the
    // to-be-denied member AND an unrelated member 403'd identically with "Saving the '...' object is
    // prohibited by security rules" -- an object-level commit-time check, not a member-level one).
    // This test proves the deny this task adds is scoped to the ONE named member, not a blanket block.
    [TestMethod]
    public async Task SaveController_restricted_role_can_still_write_a_different_non_denied_member() {
        var admin = await GetClientAsync("Admin");
        var first = await admin.GetFromJsonAsync<JsonElement>(
            $"api/odata/Order?$top=1&$orderby={KnownModel.OrderColumn1} asc&$select={KnownModel.OrderKeyMember},{KnownModel.OrderColumn1}");
        var row = first.GetProperty("value")[0];
        var key = row.GetProperty(KnownModel.OrderKeyMember).GetString();
        var originalValue = row.GetProperty(KnownModel.OrderColumn1).GetString();
        var newValue = $"INV-{Guid.NewGuid():N}";

        var restricted = await GetClientAsync("Restricted");
        var resp = await restricted.PostAsJsonAsync($"api/save/Order/{key}",
            new Dictionary<string, object?> { [KnownModel.OrderColumn1] = newValue });
        try {
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
                $"expected 200 -- Restricted has type-level Write:Allow on Order and no member-deny on " +
                $"{KnownModel.OrderColumn1}, got {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        } finally {
            await admin.PostAsJsonAsync($"api/save/Order/{key}",
                new Dictionary<string, object?> { [KnownModel.OrderColumn1] = originalValue });
        }
    }

    // OData enum projection may come back as a JSON string (expected, per JsonStringEnumConverter) or,
    // defensively, some other token shape; normalize either way so the test asserts on the value, not
    // on the wire representation.
    static string ReadEnumAsString(JsonElement root, string member) {
        var prop = root.GetProperty(member);
        return prop.ValueKind == JsonValueKind.String ? prop.GetString()! : prop.GetRawText();
    }
}
