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
    public void DateTime_comparisons_translate_over_date_not_instant_literals() {
        // These members are Edm.DateTimeOffset in the EDM but DateTime in the CLR, so the server has NO
        // operator for the pair and rejects an instant literal outright (live evidence, GET
        // api/odata/Order?$filter=OrderDate ge 2026-04-04T00:00:00Z -> 400 "The binary operator
        // GreaterThanOrEqual is not defined for the types 'System.Nullable`1[System.DateTime]' and
        // 'System.Nullable`1[System.DateTimeOffset]'"). A no-offset literal fares no better -- it fails
        // to PARSE as Edm.DateTimeOffset. date(path) compared against a date literal is the one form
        // this host accepts (verified live: 200, and the same row count the day holds).
        var map = Map("OrderDate");
        Assert.AreEqual("date(OrderDate) ge 2026-03-19", ODataFilterTranslator.Translate(
            new BinaryOperator("OrderDate", new DateTime(2026, 3, 19), BinaryOperatorType.GreaterOrEqual), map));
        Assert.AreEqual("date(OrderDate) eq 2026-03-19", ODataFilterTranslator.Translate(
            new BinaryOperator("OrderDate", new DateTime(2026, 3, 19), BinaryOperatorType.Equal), map));
        // No zone conversion: the day the user picked is the day compared. The previous
        // ToUniversalTime() step pushed a late-evening wall time onto a NEIGHBOURING date, which is a
        // wrong-answer bug independent of the 400 above.
        Assert.AreEqual("date(OrderDate) lt 2026-03-19", ODataFilterTranslator.Translate(
            new BinaryOperator("OrderDate", new DateTime(2026, 3, 19, 23, 30, 0), BinaryOperatorType.Less), map));
        // DateTimeKind is irrelevant by design now -- a Kind=Utc value renders its own date, unconverted.
        Assert.AreEqual("date(OrderDate) ge 2026-01-15", ODataFilterTranslator.Translate(
            new BinaryOperator("OrderDate", new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                BinaryOperatorType.GreaterOrEqual), map));
    }

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

    // (Translate_formats_datetime_as_utc_iso8601 and Translate_converts_wall_time_date_range_to_utc_instants
    // are gone: both asserted the UTC-instant wire format, which this host answers with 400. The
    // replacement contract is DateTime_comparisons_translate_over_date_not_instant_literals above, plus
    // GridBindingTests.BuildDayRangeCriteria_round_trips_and_translates_to_a_date_range for the range shape.)

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
