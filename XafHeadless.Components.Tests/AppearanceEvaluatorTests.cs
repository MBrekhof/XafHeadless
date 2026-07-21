using System.Dynamic;
using XafHeadless.Components.Contracts;
using XafHeadless.Components.Services;

namespace XafHeadless.Components.Tests;

// GAP-002: the client conditional-appearance engine. Exercises the framework evaluator path
// (ExpressionEvaluator.Fit over an ExpandoObject row) plus the two mandatory guards:
//   (a) a rule whose criteria references a member the row lacks is skipped (Fit would throw);
//   (b) enum-literal normalization -- a criteria written with a multi-word enum's CAPTION is rewritten to
//       the member NAME (the form the grid row stores), the case the spike showed breaks without it.
// NOTE (verified live, diverges from the spike's assumption): OData emits the enum NAME and MaterializeRow
// keeps it, so rows hold the NAME and nameof-form criteria match directly; the rewrite is the guard for a
// criteria authored with the caption instead.
[TestClass]
public class AppearanceEvaluatorTests {
    const string Green = "#008000";

    static AppearanceRuleDto Rule(string criteria, string[] targets,
        string? font = null, string? back = null, string? style = null) => new(criteria, targets, font, back, style);

    static ColumnMetadata EnumCol(string member, params (long Value, string Caption, string Name)[] values) =>
        new(member, member, "enum", null, null, null,
            values.Select(v => new EnumValueMetadata((object)v.Value, v.Caption, v.Name)).ToList());

    static ColumnMetadata BoolCol(string member) => new(member, member, "bool", null, null, null, null);

    static IDictionary<string, object?> Row(params (string Key, object? Value)[] cells) {
        IDictionary<string, object?> row = new ExpandoObject();
        foreach (var (k, v) in cells) row[k] = v;
        return row;
    }

    [TestMethod]
    public void Rating_Good_rule_applies_green_to_the_whole_row() {
        var ev = new AppearanceEvaluator(new[] { Rule("Rating='Good'", new[] { "*" }, font: Green) }, columns: null);

        var applied = ev.Evaluate(Row(("Rating", "Good")));

        Assert.HasCount(1, applied);
        Assert.IsTrue(applied[0].WholeRow);
        Assert.AreEqual(Green, applied[0].FontColor);
        // Rating is exposed so XafListView carries it into rows (it isn't a displayed column of that view).
        CollectionAssert.Contains(ev.ReferencedMembers.ToList(), "Rating");
    }

    [TestMethod]
    public void Non_matching_row_yields_no_style() {
        var ev = new AppearanceEvaluator(new[] { Rule("Rating='Good'", new[] { "*" }, font: Green) }, columns: null);
        Assert.IsEmpty(ev.Evaluate(Row(("Rating", "Average"))));
    }

    [TestMethod]
    public void Rule_referencing_a_member_absent_from_the_row_is_skipped_without_throwing() {
        // Guard (a): the row has no "Rating" key, so ExpressionEvaluator.Fit would throw
        // InvalidPropertyPathException. The evaluator must skip the rule, not crash the row-render loop.
        var ev = new AppearanceEvaluator(new[] { Rule("Rating='Good'", new[] { "*" }, font: Green) }, columns: null);

        var applied = ev.Evaluate(Row(("Subject", "anything")));   // no Rating key at all

        Assert.IsEmpty(applied);
    }

    [TestMethod]
    public void Bool_negation_criteria_matches_only_when_false() {
        var ev = new AppearanceEvaluator(new[] { Rule("!Available", new[] { "*" }, style: "Strikeout") },
            new[] { BoolCol("Available") });
        Assert.HasCount(1, ev.Evaluate(Row(("Available", false))));
        Assert.IsEmpty(ev.Evaluate(Row(("Available", true))));
    }

    [TestMethod]
    public void Multiword_enum_criteria_evaluate_correctly_including_the_caption_rewrite() {
        // EmployeeTaskStatus.NeedAssistance: CLR name "NeedAssistance", caption "Need Assistance". The grid
        // row holds the NAME. Metadata carries both (Name + Caption), per GAP-002's EnumValueMetadata.Name.
        var cols = new[] { EnumCol("Status", (3, "Need Assistance", "NeedAssistance")) };
        var row = Row(("Status", "NeedAssistance"));

        // (i) the ordinary nameof form matches directly.
        var byName = new AppearanceEvaluator(new[] { Rule("Status='NeedAssistance'", new[] { "*" }, font: Green) }, cols);
        Assert.HasCount(1, byName.Evaluate(row), "multi-word enum criteria in nameof form must match");

        // (ii) THE REWRITE: a criteria written with the multi-word CAPTION is normalized to the member NAME
        // (walking the CriteriaOperator tree) so it still matches the row's name form.
        var byCaption = new AppearanceEvaluator(new[] { Rule("Status='Need Assistance'", new[] { "*" }, font: Green) }, cols);
        Assert.HasCount(1, byCaption.Evaluate(row), "caption literal must be rewritten to the member name and match");

        // (iii) load-bearing proof: with NO enum metadata there is nothing to rewrite against, so the caption
        // literal does NOT match the row's name -- (ii) passes because of the rewrite, not by coincidence.
        var noMeta = new AppearanceEvaluator(new[] { Rule("Status='Need Assistance'", new[] { "*" }, font: Green) }, columns: null);
        Assert.IsEmpty(noMeta.Evaluate(row), "without the rewrite the caption literal must not match the row's name");
    }

    [TestMethod]
    public void Noncolumn_enum_member_caption_rewrite_via_AppearanceEnums_channel() {
        // GAP-002 completeness fix: Rating (Employee_Evaluations_ListView's real appearance-rule member) is
        // NOT a displayed column of that view -- enumsByMember built purely from ColumnMetadata has no
        // channel for it, so the caption->name rewrite is structurally inert. AppearanceEnums is the new
        // channel: server-projected enum metadata for a member that isn't a column. Name "Good" is what the
        // grid row holds (per MaterializeRow); Caption "Very Good" diverges from it to make the rewrite provable.
        var appearanceEnums = new Dictionary<string, IReadOnlyList<EnumValueMetadata>> {
            ["Rating"] = new List<EnumValueMetadata> { new(1L, "Very Good", "Good") }
        };
        var row = Row(("Rating", "Good"));
        var rule = Rule("Rating='Very Good'", new[] { "*" }, font: Green);

        // WITH the AppearanceEnums metadata: the caption literal is rewritten to the name and Fits.
        var withMeta = new AppearanceEvaluator(new[] { rule }, columns: null, appearanceEnums);
        Assert.HasCount(1, withMeta.Evaluate(row),
            "non-column enum caption literal must be rewritten via AppearanceEnums and match the row's name");

        // WITHOUT it: nothing to rewrite against, so the caption literal must not match the row's name --
        // proving the channel is load-bearing (mirrors the existing column-backed test's noMeta case).
        var withoutMeta = new AppearanceEvaluator(new[] { rule }, columns: null, appearanceEnums: null);
        Assert.IsEmpty(withoutMeta.Evaluate(row),
            "without the AppearanceEnums channel the caption literal must not match the row's name");
    }

    [TestMethod]
    public void Rule_targeting_a_specific_member_is_not_whole_row() {
        var ev = new AppearanceEvaluator(new[] { Rule("1=1", new[] { "StartOn" }, style: "Bold") }, columns: null);

        var applied = ev.Evaluate(Row(("StartOn", "2026-01-01")));

        Assert.HasCount(1, applied);
        Assert.IsFalse(applied[0].WholeRow);
        CollectionAssert.AreEqual(new[] { "StartOn" }, applied[0].TargetItems.ToList());
        Assert.AreEqual("Bold", applied[0].FontStyle);
    }

    [TestMethod]
    public void No_rules_means_HasRules_false_and_no_referenced_members() {
        var ev = new AppearanceEvaluator(rules: null, columns: null);
        Assert.IsFalse(ev.HasRules);
        Assert.IsEmpty(ev.ReferencedMembers);
        Assert.IsEmpty(ev.Evaluate(Row(("X", 1))));
    }
}
