using System.Text.Json;
using XafHeadless.Components.Contracts;
using XafHeadless.Components.Services;

namespace XafHeadless.Components.Tests;

[TestClass]
public class DetailBindingTests {
    // LayoutNode ctor order: Kind, Caption, Member, Editor, AllowWrite, Required, MaxLength,
    //                        ViewId, MasterKeyMember, Lookup, Enum, Children
    static LayoutNode Item(string member, string editor = "string", LookupMetadata? lookup = null,
        bool allowWrite = true, List<EnumValueMetadata>? @enum = null) =>
        new("item", member, member, editor, allowWrite, false, null, null, null, lookup, @enum, null);
    static LayoutNode Group(string? caption, params LayoutNode[] children) =>
        new("group", caption, null, null, null, null, null, null, null, null, null, children.ToList());
    static LayoutNode Tabs(params LayoutNode[] tabs) =>
        new("tabs", null, null, null, null, null, null, null, null, null, null, tabs.ToList());
    static LayoutNode Tab(string caption, params LayoutNode[] children) =>
        new("tab", caption, null, null, null, null, null, null, null, null, null, children.ToList());
    static LayoutNode Nested(string member, string viewId, string masterKey) =>
        new("nestedList", member, member, null, null, null, null, viewId, masterKey, null, null, null);
    static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

    // ---- ValuesEqual ----
    [TestMethod]
    public void ValuesEqual_null_and_empty_string_are_equal() {
        Assert.IsTrue(DetailBinding.ValuesEqual(null, null));
        Assert.IsTrue(DetailBinding.ValuesEqual(null, ""));
        Assert.IsTrue(DetailBinding.ValuesEqual("", null));
    }

    [TestMethod]
    public void ValuesEqual_distinguishes_different_strings_and_matches_same() {
        Assert.IsTrue(DetailBinding.ValuesEqual("A", "A"));
        Assert.IsFalse(DetailBinding.ValuesEqual("A", "B"));
        Assert.IsFalse(DetailBinding.ValuesEqual("A", null));   // clearing a real value IS a change
    }

    [TestMethod]
    public void ValuesEqual_normalizes_numeric_types() {
        Assert.IsTrue(DetailBinding.ValuesEqual(5m, 5));        // decimal vs int
        Assert.IsTrue(DetailBinding.ValuesEqual(5m, 5L));       // decimal vs long
        Assert.IsFalse(DetailBinding.ValuesEqual(5m, 6m));
    }

    [TestMethod]
    public void ValuesEqual_compares_dates_and_bools() {
        Assert.IsTrue(DetailBinding.ValuesEqual(new DateTime(2020, 1, 1), new DateTime(2020, 1, 1)));
        Assert.IsFalse(DetailBinding.ValuesEqual(new DateTime(2020, 1, 1), new DateTime(2021, 1, 1)));
        Assert.IsTrue(DetailBinding.ValuesEqual(true, true));
        Assert.IsFalse(DetailBinding.ValuesEqual(true, false));
    }

    // ---- ApplyChange (dirty-diff) ----
    [TestMethod]
    public void ApplyChange_records_a_real_change() {
        var changes = new Dictionary<string, object?>();
        DetailBinding.ApplyChange(changes, original: "A", "Status", "B");
        Assert.AreEqual("B", changes["Status"]);
    }

    [TestMethod]
    public void ApplyChange_removes_member_when_toggled_back_to_original() {
        var changes = new Dictionary<string, object?>();
        DetailBinding.ApplyChange(changes, "A", "Status", "B");
        DetailBinding.ApplyChange(changes, "A", "Status", "A");   // back to original
        Assert.IsFalse(changes.ContainsKey("Status"));
    }

    [TestMethod]
    public void ApplyChange_does_not_record_empty_string_over_null_original() {
        var changes = new Dictionary<string, object?>();
        DetailBinding.ApplyChange(changes, original: null, "Remark", "");  // empty box over null
        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public void ApplyChange_records_clearing_a_populated_member() {
        var changes = new Dictionary<string, object?>();
        DetailBinding.ApplyChange(changes, original: "A", "Status", null);  // the Status-clear vehicle
        Assert.IsTrue(changes.ContainsKey("Status"));
        Assert.IsNull(changes["Status"]);
    }

    // ---- ShouldCollapse / Unwrap ----
    [TestMethod]
    public void ShouldCollapse_true_only_for_captionless_single_child_group() {
        Assert.IsTrue(DetailBinding.ShouldCollapse(Group(null, Item("A"))));
        Assert.IsFalse(DetailBinding.ShouldCollapse(Group("Section", Item("A"))));      // captioned
        Assert.IsFalse(DetailBinding.ShouldCollapse(Group(null, Item("A"), Item("B")))); // 2 children
        Assert.IsFalse(DetailBinding.ShouldCollapse(Group(null)));                       // 0 children
        Assert.IsFalse(DetailBinding.ShouldCollapse(Item("A")));                         // not a group
        Assert.IsFalse(DetailBinding.ShouldCollapse(Tabs(Tab("t", Item("A")))));         // tabs
    }

    [TestMethod]
    public void Unwrap_peels_nested_captionless_wrappers_down_to_the_meaningful_node() {
        var inner = Group("Real Section", Item("A"), Item("B"));
        var wrapped = Group(null, Group(null, inner));    // two wrapper layers
        Assert.AreSame(inner, DetailBinding.Unwrap(wrapped));
    }

    [TestMethod]
    public void Unwrap_stops_at_a_captionless_group_with_multiple_children() {
        var multi = Group(null, Item("A"), Item("B"));
        Assert.AreSame(multi, DetailBinding.Unwrap(multi));
    }

    [TestMethod]
    public void Unwrap_returns_item_unchanged() {
        var item = Item("A");
        Assert.AreSame(item, DetailBinding.Unwrap(item));
    }

    // ---- AllItems ----
    [TestMethod]
    public void AllItems_walks_groups_tabs_and_tabpages_collecting_only_item_nodes() {
        var tree = Group("Root",
            Item("A"),
            Group(null, Item("B"), Nested("Results", "Result_ListView", "SampleNumber")),
            Tabs(Tab("Tab1", Item("C")), Tab("Tab2", Item("D"))));
        var members = DetailBinding.AllItems(tree).Select(i => i.Member).ToList();
        CollectionAssert.AreEquivalent(new[] { "A", "B", "C", "D" }, members);
    }

    [TestMethod]
    public void AllItems_of_null_is_empty() =>
        Assert.AreEqual(0, DetailBinding.AllItems(null).Count());

    // ---- LookupExpand ----
    [TestMethod]
    public void LookupExpand_collects_lookup_item_members_only() {
        var tree = Group("Root",
            Item("Status"),
            Item("Customer", "lookup", new LookupMetadata("Customer", "CustomerNumber", "Name")),
            Group(null, Item("Project", "lookup", new LookupMetadata("Project", "ProjectNumber", "Name"))),
            Nested("Results", "Result_ListView", "SampleNumber"));
        Assert.AreEqual("Customer,Project", DetailBinding.LookupExpand(tree));
    }

    [TestMethod]
    public void LookupExpand_is_null_when_no_lookups() =>
        Assert.IsNull(DetailBinding.LookupExpand(Group("Root", Item("Status"), Item("Amount", "decimal"))));

    // ---- ItemValue (materialization) ----
    [TestMethod]
    public void ItemValue_reads_string_int_decimal_bool_and_date() {
        var row = Row("""{"Status":"A","Count":7,"Amount":12.5,"Active":true,"Created":"2020-03-04T00:00:00"}""");
        Assert.AreEqual("A", DetailBinding.ItemValue(row, Item("Status")));
        Assert.AreEqual(7m, DetailBinding.ItemValue(row, Item("Count", "int")));
        Assert.AreEqual(12.5m, DetailBinding.ItemValue(row, Item("Amount", "decimal")));
        Assert.IsTrue((bool?)DetailBinding.ItemValue(row, Item("Active", "bool")));
        Assert.AreEqual(new DateTime(2020, 3, 4), DetailBinding.ItemValue(row, Item("Created", "date")));
    }

    [TestMethod]
    public void ItemValue_is_null_for_a_member_absent_from_the_row() {
        // Model-vs-EDM divergence: a layout item whose member isn't a real OData property.
        var row = Row("""{"Status":"A"}""");
        Assert.IsNull(DetailBinding.ItemValue(row, Item("ModelOnlyFlag", "bool")));
    }

    [TestMethod]
    public void ItemValue_lookup_returns_the_expanded_nav_key() {
        var row = Row("""{"Customer":{"CustomerNumber":42,"Name":"WBG"}}""");
        var item = Item("Customer", "lookup", new LookupMetadata("Customer", "CustomerNumber", "Name"));
        Assert.AreEqual(42m, DetailBinding.ItemValue(row, item));
    }

    [TestMethod]
    public void ItemValue_lookup_is_null_when_nav_absent() {
        var item = Item("Customer", "lookup", new LookupMetadata("Customer", "CustomerNumber", "Name"));
        Assert.IsNull(DetailBinding.ItemValue(Row("""{"Status":"A"}"""), item));
    }

    [TestMethod]
    public void ItemValue_enum_canonicalizes_numeric_value_to_string() {
        var e = new List<EnumValueMetadata> { new((object)1L, "Draft"), new((object)2L, "Approved") };
        var row = Row("""{"Status":2}""");
        Assert.AreEqual("2", DetailBinding.ItemValue(row, Item("Status", "enum", @enum: e)));
    }

    // ---- KeyFilter ----
    [TestMethod]
    public void KeyFilter_builds_an_odata_equality_filter() =>
        Assert.AreEqual("SampleNumber eq 1665361", DetailBinding.KeyFilter("SampleNumber", "1665361"));
}
