using DevExpress.Data.Filtering;
using XafHeadless.Components.Services;

namespace XafHeadless.Components.Tests;

// Correctness guard for the DxGrid filter-row -> OData $filter translation (GAP-005). CriteriaOperator
// shapes below are exactly what dxdocs' "Filter API in Blazor Grid" reference article demonstrates for
// each operator family (BinaryOperator/FunctionOperator/GroupOperator/UnaryOperator), not guessed.
[TestClass]
public class ODataFilterTranslatorTests {
    // Identity FieldName->path map for plain scalar columns (FieldFor == OrderPathFor for anything
    // that isn't a lookup, per GridBinding) -- mirrors what XafListView's real filterPathByField would
    // contain for these columns. A property NOT in this map is exactly how the enum/lookup ceiling is
    // enforced (see Translate_skips_a_property_not_present_in_the_map_ceiling below).
    static Dictionary<string, string> Map(params string[] scalarFields) =>
        scalarFields.ToDictionary(f => f, f => f);

    [TestMethod]
    public void Translate_string_contains() {
        var criteria = new FunctionOperator(FunctionOperatorType.Contains, new OperandProperty("InvoiceNumber"), "000");
        Assert.AreEqual("contains(InvoiceNumber,'000')", ODataFilterTranslator.Translate(criteria, Map("InvoiceNumber")));
    }

    [TestMethod]
    public void Translate_numeric_greater_than() {
        var criteria = new BinaryOperator("TotalAmount", 100m, BinaryOperatorType.Greater);
        Assert.AreEqual("TotalAmount gt 100", ODataFilterTranslator.Translate(criteria, Map("TotalAmount")));
    }

    [TestMethod]
    public void Translate_ge_and_le_range_group() {
        var criteria = GroupOperator.And(
            new BinaryOperator("TotalAmount", 100m, BinaryOperatorType.GreaterOrEqual),
            new BinaryOperator("TotalAmount", 500m, BinaryOperatorType.LessOrEqual));
        Assert.AreEqual("(TotalAmount ge 100 and TotalAmount le 500)",
            ODataFilterTranslator.Translate(criteria, Map("TotalAmount")));
    }

    [TestMethod]
    public void Translate_or_group() {
        var criteria = GroupOperator.Or(
            new BinaryOperator("ShippingType", "Standard", BinaryOperatorType.Equal),
            new BinaryOperator("ShippingType", "Express", BinaryOperatorType.Equal));
        Assert.AreEqual("(ShippingType eq 'Standard' or ShippingType eq 'Express')",
            ODataFilterTranslator.Translate(criteria, Map("ShippingType")));
    }

    [TestMethod]
    public void Translate_is_null() {
        var criteria = new UnaryOperator(UnaryOperatorType.IsNull, new OperandProperty("ShippedDate"));
        Assert.AreEqual("ShippedDate eq null", ODataFilterTranslator.Translate(criteria, Map("ShippedDate")));
    }

    [TestMethod]
    public void Translate_negated_is_null_becomes_ne_null() {
        var criteria = new UnaryOperator(UnaryOperatorType.Not,
            new UnaryOperator(UnaryOperatorType.IsNull, new OperandProperty("ShippedDate")));
        Assert.AreEqual("ShippedDate ne null", ODataFilterTranslator.Translate(criteria, Map("ShippedDate")));
    }

    [TestMethod]
    public void Translate_escapes_single_quotes_in_string_values() {
        var criteria = new BinaryOperator("Notes", "O'Brien", BinaryOperatorType.Equal);
        Assert.AreEqual("Notes eq 'O''Brien'", ODataFilterTranslator.Translate(criteria, Map("Notes")));
    }

    [TestMethod]
    public void Translate_maps_FieldName_to_a_different_OData_path() {
        // Mirrors a lookup's flattened FieldName ("Customer_Name") vs its real nav path
        // ("Customer/Name") -- same map shape XafListView's orderPathByField already produces.
        var map = new Dictionary<string, string> { ["Customer_Name"] = "Customer/Name" };
        var criteria = new FunctionOperator(FunctionOperatorType.Contains, new OperandProperty("Customer_Name"), "Acme");
        Assert.AreEqual("contains(Customer/Name,'Acme')", ODataFilterTranslator.Translate(criteria, map));
    }

    [TestMethod]
    public void Translate_formats_datetime_as_utc_iso8601() {
        var criteria = new BinaryOperator("OrderDate", new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            BinaryOperatorType.GreaterOrEqual);
        Assert.AreEqual("OrderDate ge 2026-01-15T10:30:00Z", ODataFilterTranslator.Translate(criteria, Map("OrderDate")));
    }

    [TestMethod]
    public void Translate_converts_wall_time_date_range_to_utc_instants() {
        // GRID-004 (companion-headless backport): the exact shape the date filter cell emits -- property >=
        // day and property < next-day, both Kind=Unspecified WALL times. The server compares $filter
        // literals as instants, so the translator must zone-convert (wall time + "Z" as-is misses
        // the rows -- proven live in the origin repo). On a UTC machine this degrades to a format
        // check; on any other zone it proves the conversion actually happened.
        var day = new DateTime(2026, 3, 19, 0, 0, 0, DateTimeKind.Unspecified);
        var next = day.AddDays(1);
        var criteria = GroupOperator.And(
            new BinaryOperator("OrderDate", day, BinaryOperatorType.GreaterOrEqual),
            new BinaryOperator("OrderDate", next, BinaryOperatorType.Less));

        static string Utc(DateTime dt) => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        Assert.AreEqual($"(OrderDate ge {Utc(day)} and OrderDate lt {Utc(next)})",
            ODataFilterTranslator.Translate(criteria, Map("OrderDate")));
        if (TimeZoneInfo.Local.GetUtcOffset(day) != TimeSpan.Zero)
            Assert.AreNotEqual($"(OrderDate ge 2026-03-19T00:00:00Z and OrderDate lt 2026-03-20T00:00:00Z)",
                ODataFilterTranslator.Translate(criteria, Map("OrderDate")));
    }

    [TestMethod]
    public void Translate_formats_bool_as_bare_true_false() {
        var criteria = new BinaryOperator("IsActive", true, BinaryOperatorType.Equal);
        Assert.AreEqual("IsActive eq true", ODataFilterTranslator.Translate(criteria, Map("IsActive")));
    }

    [TestMethod]
    public void Translate_returns_null_for_null_criteria() =>
        Assert.IsNull(ODataFilterTranslator.Translate(null, Map()));

    [TestMethod]
    public void Translate_skips_a_property_not_present_in_the_map_ceiling() {
        // Ceiling: enum/lookup columns are out of scope. XafListView enforces this by never putting
        // their FieldNames in the map handed to the translator -- unlike GridBinding.BuildOrderBy's
        // sort fallback, a missing map entry here means "skip", not "use the raw name".
        var criteria = new BinaryOperator("Status", "Draft", BinaryOperatorType.Equal);
        Assert.IsNull(ODataFilterTranslator.Translate(criteria, Map()));
    }

    [TestMethod]
    public void Translate_returns_null_for_an_out_of_scope_operator_type() {
        // Ceiling exercised directly: InOperator has no case in Translate's switch at all (unlike the
        // missing-map-entry ceiling above, which drops an in-scope BinaryOperator via a bad map) -- it
        // falls through the `_ => null` default rather than being guessed at.
        var criteria = new InOperator("Status", new object[] { "Draft", "Sent" });
        Assert.IsNull(ODataFilterTranslator.Translate(criteria, Map("Status")));
    }

    [TestMethod]
    public void Combine_ANDs_master_and_user_filters_when_both_present() =>
        Assert.AreEqual("(Order/ID eq 1) and (TotalAmount gt 100)",
            ODataFilterTranslator.Combine("Order/ID eq 1", "TotalAmount gt 100"));

    [TestMethod]
    public void Combine_uses_master_only_when_no_user_filter() =>
        Assert.AreEqual("Order/ID eq 1", ODataFilterTranslator.Combine("Order/ID eq 1", null));

    [TestMethod]
    public void Combine_uses_user_only_when_no_master_filter() =>
        Assert.AreEqual("TotalAmount gt 100", ODataFilterTranslator.Combine(null, "TotalAmount gt 100"));

    [TestMethod]
    public void Combine_returns_null_when_neither_filter_present() =>
        Assert.IsNull(ODataFilterTranslator.Combine(null, null));
}
