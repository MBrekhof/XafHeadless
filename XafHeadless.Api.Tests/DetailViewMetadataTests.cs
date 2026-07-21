using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

[TestClass]
public class DetailViewMetadataTests : TestBase {
    [TestMethod]
    public async Task DetailView_has_layout_tree_with_items_and_nested_collection() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.OrderDetailViewId}");
        Assert.AreEqual("DetailView", meta.GetProperty("Type").GetString());
        Assert.AreEqual(KnownModel.OrderKeyMember, meta.GetProperty("KeyMember").GetString()); // PH2-001
        var flat = MetadataTestHelpers.Flatten(meta.GetProperty("Layout")).ToList();
        Assert.IsGreaterThanOrEqualTo(3, flat.Count(n => n.GetProperty("Kind").GetString() == "item"), "too few editors");
        var nested = flat.Where(n => n.GetProperty("Kind").GetString() == "nestedList"
            && n.GetProperty("Member").GetString() == KnownModel.OrderCollectionMember).ToList();
        Assert.IsNotEmpty(nested, "nested collection node missing");
        // Real projection output (discovered live, not guessed): OrderItems nests into its own
        // ListView with MasterKeyMember pointing back at the owning Order via OrderItem.Order.
        Assert.AreEqual(KnownModel.OrderCollectionViewId, nested[0].GetProperty("ViewId").GetString());
        Assert.AreEqual(KnownModel.OrderCollectionMasterKeyMember, nested[0].GetProperty("MasterKeyMember").GetString());
    }

    // Mirror of the ListView trimming proof, at the layout level: rule 1 says unreadable members are
    // OMITTED (not flagged). The Restricted role denies read on KnownModel.RestrictedDeniedMember
    // ("TotalAmount") -- the layout tree must not contain an "item" node for it at all.
    [TestMethod]
    public async Task Restricted_role_layout_omits_item_node_for_denied_member() {
        var restricted = await GetClientAsync("Restricted");
        var meta = await restricted.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.OrderDetailViewId}");
        var flat = MetadataTestHelpers.Flatten(meta.GetProperty("Layout")).ToList();
        Assert.IsFalse(
            flat.Any(n => n.GetProperty("Kind").GetString() == "item"
                && n.TryGetProperty("Member", out var m) && m.GetString() == KnownModel.RestrictedDeniedMember),
            "restricted role denies read on this member — layout must omit its item node, not flag it");
    }

    // Closes the old (original POC) metadata/save Required-mismatch finding: that host's ad-hoc
    // runtime rule enforced a 422 on a member the metadata endpoint reported as Required:false,
    // because the POC entity carried no real [RuleRequiredField]. Employee does carry real ones, so the
    // projector (IsRequired, reading DevExpress.Persistent.Validation.RuleRequiredFieldAttribute off
    // the model member) must now report Required:true for real -- verified live before writing this
    // assertion.
    [TestMethod]
    public async Task Employee_DetailView_reports_Required_true_on_RuleRequiredField_member() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.EmployeeDetailViewId}");
        var flat = MetadataTestHelpers.Flatten(meta.GetProperty("Layout")).ToList();
        var item = flat.Single(n => n.GetProperty("Kind").GetString() == "item"
            && n.TryGetProperty("Member", out var m) && m.GetString() == KnownModel.EmployeeRequiredMember);
        Assert.IsTrue(item.GetProperty("Required").GetBoolean(),
            "Employee carries a real [RuleRequiredField] on this member — metadata must report Required:true");
    }

    // DATA-001: after reconciling ClassifyDataType and ProjectLookup onto ONE predicate (IsLookupMember),
    // the classification (Editor) and the projection (Lookup) can never disagree for any member. This guard
    // pins that invariant on the case the fix most affects: LookupProbe.Ref is a genuinely INVERSE-LESS,
    // one-way reference (verified live: IsAssociation == false, AssociatedMember == null) to the persistent
    // shared TaxRate. The fix DROPS the old ClassifyDataType `IsAssociation` clause, so Ref now classifies as
    // a lookup purely on its target being a persistent/domain-component type — this asserts that still holds
    // (Editor "lookup" AND populated Lookup). NOTE: this does NOT go red on the pre-fix projector — for the
    // EF Core provider EFCoreTypeInfoSource co-sets IsDomainComponent==IsPersistent on every entity, so the
    // two old predicates were already equivalent over all EF members and agreed here too.
    [TestMethod]
    public async Task Inverse_less_reference_is_classified_as_a_consistent_lookup() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.LookupProbeDetailViewId}");
        var flat = MetadataTestHelpers.Flatten(meta.GetProperty("Layout")).ToList();
        var refItem = flat.Single(n => n.GetProperty("Kind").GetString() == "item"
            && n.TryGetProperty("Member", out var m) && m.GetString() == KnownModel.LookupProbeReferenceMember);
        Assert.AreEqual("lookup", refItem.GetProperty("Editor").GetString(),
            "inverse-less one-way reference must classify as 'lookup' (the fix must not regress it to a scalar)");
        Assert.AreEqual(JsonValueKind.Object, refItem.GetProperty("Lookup").ValueKind,
            "a 'lookup' classification must come with populated Lookup metadata — classification and projection must agree");
    }

    // BUG-001: a byte[] member reports IsList==true, so the projector used to emit a nested "list of Byte"
    // node whose ViewId ("Byte_ListView") 404s -> the client's "Failed to load view metadata" error. It must
    // now project as an "image" item, NOT a nestedList, while genuine business-object collections on the same
    // view stay nested. Customer_DetailView carries both (Logo byte[] + Orders/Employees collections).
    [TestMethod]
    public async Task Byte_array_member_is_an_image_item_not_a_broken_nested_view() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.CustomerDetailViewId}");
        var flat = MetadataTestHelpers.Flatten(meta.GetProperty("Layout")).ToList();

        // the byte[] member is a scalar "image" item, not a nested collection
        var logo = flat.Single(n => n.GetProperty("Kind").GetString() == "item"
            && n.TryGetProperty("Member", out var m) && m.GetString() == KnownModel.CustomerImageMember);
        Assert.AreEqual("image", logo.GetProperty("Editor").GetString(),
            "a byte[] member must classify as 'image', not be projected as a nested collection");

        // the exact symptom must be gone: no nested list points at the non-existent Byte_ListView
        Assert.IsFalse(
            flat.Any(n => n.GetProperty("Kind").GetString() == "nestedList"
                && n.TryGetProperty("ViewId", out var v) && v.GetString() == KnownModel.ByteListViewId),
            "no nestedList may reference Byte_ListView -- that view id does not exist and 404s");

        // regression guard: real business-object collections on the same view are still nested
        Assert.IsNotEmpty(
            flat.Where(n => n.GetProperty("Kind").GetString() == "nestedList").ToList(),
            "genuine BO collections (Orders/Employees/...) must still project as nestedList nodes");
    }

    // BUG-002: a nested collection whose child type isn't independently OData-exposed
    // (WebApiOptions.BusinessObjects) would 404 on the child grid's data fetch, so the projector omits the tab.
    // Customer_DetailView.Employees is ObservableCollection<CustomerEmployee> (a join type never registered) —
    // its nestedList must be ABSENT; Orders (child Order, exposed) must still be present.
    [TestMethod]
    public async Task Nested_collection_over_a_non_exposed_child_type_is_omitted() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.CustomerDetailViewId}");
        var flat = MetadataTestHelpers.Flatten(meta.GetProperty("Layout")).ToList();
        var nestedMembers = flat.Where(n => n.GetProperty("Kind").GetString() == "nestedList")
            .Select(n => n.GetProperty("Member").GetString()).ToList();
        Assert.DoesNotContain(KnownModel.CustomerOmittedNestedMember, nestedMembers,
            "a nested collection over a non-OData-exposed child type must be omitted, not projected as a 404-ing tab");
        Assert.Contains(KnownModel.CustomerKeptNestedMember, nestedMembers,
            "a nested collection over an exposed child type must still be projected");
    }

    // DATA-001 behavior-preservation guard: switching ClassifyDataType from the IsAssociation condition to the
    // IsDomainComponent/IsPersistent one must NOT change a live demo lookup. Order.Employee is a bidirectional
    // persistent association (IsAssociation true + IsPersistent true) and the one reference editor actually laid
    // out on the demo's Order_DetailView — it must STILL be a consistent lookup after the change.
    [TestMethod]
    public async Task Live_demo_lookup_Order_Employee_stays_a_consistent_lookup() {
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.OrderDetailViewId}");
        var flat = MetadataTestHelpers.Flatten(meta.GetProperty("Layout")).ToList();
        var employee = flat.Single(n => n.GetProperty("Kind").GetString() == "item"
            && n.TryGetProperty("Member", out var m) && m.GetString() == KnownModel.OrderEmployeeLookupMember);
        Assert.AreEqual("lookup", employee.GetProperty("Editor").GetString(),
            "behavior-preservation: Order.Employee must STILL classify as a lookup after the predicate change");
        Assert.AreEqual(JsonValueKind.Object, employee.GetProperty("Lookup").ValueKind,
            "Order.Employee must keep populated Lookup metadata");
    }
}
