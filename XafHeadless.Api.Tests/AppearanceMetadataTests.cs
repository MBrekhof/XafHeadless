using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.Api.Tests;

// GAP-002: conditional-appearance projection. ViewMetadataProjector reads the class's ConditionalAppearance
// ViewItem rules (AppearanceController.GetRulesFromModel) and projects the color/style ones that apply to
// the requested view (Context-honored), as AppearanceRuleDto. Integration tests against the live host --
// the projection reads the real 26.1 model headlessly.
[TestClass]
public class AppearanceMetadataTests : TestBase {
    static async Task<List<JsonElement>> AppearanceOf(HttpClient c, string viewId) =>
        (await c.GetFromJsonAsync<JsonElement>($"api/model/views/{viewId}"))
        .GetProperty("Appearance").EnumerateArray().ToList();

    [TestMethod]
    public async Task Nested_evaluations_list_projects_the_green_Rating_rule_targeting_the_whole_row() {
        var client = await GetClientAsync("Admin");
        var rules = await AppearanceOf(client, KnownModel.EvaluationNestedListViewId);

        var green = rules.SingleOrDefault(r =>
            r.GetProperty("Criteria").GetString() == KnownModel.EvaluationAppearanceCriteria);
        Assert.AreNotEqual(JsonValueKind.Undefined, green.ValueKind,
            $"expected the {KnownModel.EvaluationAppearanceCriteria} appearance rule on {KnownModel.EvaluationNestedListViewId}");
        Assert.AreEqual(KnownModel.EvaluationAppearanceFontColor, green.GetProperty("FontColor").GetString(),
            "Color.Green must project as CSS hex #008000");
        var targets = green.GetProperty("TargetItems").EnumerateArray().Select(t => t.GetString()).ToList();
        CollectionAssert.AreEqual(new[] { "*" }, targets, "the rule styles the whole row");
    }

    [TestMethod]
    public async Task Projection_honors_Context_excluding_child_clone_rules() {
        // The Blue StartOn rule is bound to the "_Child" clone context, so it must NOT be projected onto
        // Employee_Evaluations_ListView -- proving Context filtering (IsRuleFitToContext parity), not a
        // blanket "return every class rule".
        var client = await GetClientAsync("Admin");
        var rules = await AppearanceOf(client, KnownModel.EvaluationNestedListViewId);
        Assert.IsFalse(
            rules.Any(r => r.GetProperty("FontColor").GetString() == KnownModel.EvaluationChildOnlyFontColor),
            "a _Child-context-only rule leaked onto the non-child view -- Context is not being honored");
    }

    [TestMethod]
    public async Task View_without_appearance_rules_projects_empty_not_null() {
        // Order carries no [Appearance] rules -> empty list (present, not null; no leak from other classes).
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.OrderListViewId}");
        var appearance = meta.GetProperty("Appearance");
        Assert.AreEqual(JsonValueKind.Array, appearance.ValueKind);
        Assert.AreEqual(0, appearance.GetArrayLength());
    }

    [TestMethod]
    public async Task Nested_evaluations_list_projects_AppearanceEnums_for_the_noncolumn_Rating_member() {
        // GAP-002 enum-fix: Rating is the appearance rule's referenced enum member (Rating='Good') but is
        // NOT a column of Employee_Evaluations_ListView -- prove the new AppearanceEnums channel carries its
        // enum metadata (incl. Name) anyway, closing the gap that made the client's caption->name rewrite
        // structurally inert for this member.
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>($"api/model/views/{KnownModel.EvaluationNestedListViewId}");

        var columnMembers = meta.GetProperty("Columns").EnumerateArray()
            .Select(c => c.GetProperty("Member").GetString()).ToList();
        CollectionAssert.DoesNotContain(columnMembers, KnownModel.EvaluationRatingMember,
            "test premise: Rating must NOT be a displayed column of this view");

        Assert.IsTrue(meta.TryGetProperty("AppearanceEnums", out var appearanceEnums),
            "expected an AppearanceEnums property on a view with appearance rules");
        Assert.IsTrue(appearanceEnums.TryGetProperty(KnownModel.EvaluationRatingMember, out var ratingEnum),
            "expected AppearanceEnums to carry metadata for the non-column Rating member");
        var values = ratingEnum.EnumerateArray().ToList();
        Assert.IsTrue(values.Any(e => e.GetProperty("Name").GetString() == "Good"),
            "Rating's AppearanceEnums metadata should carry the CLR member Name (Good)");
    }

    [TestMethod]
    public async Task Enum_metadata_now_carries_the_member_Name() {
        // GAP-002 added EnumValueMetadata.Name (Enum.GetName) so the client can normalize a criteria's
        // enum literal. Prove it round-trips on a real enum column.
        var client = await GetClientAsync("Admin");
        var meta = await client.GetFromJsonAsync<JsonElement>("api/model/views/EmployeeTask_ListView");
        var statusEnum = meta.GetProperty("Columns").EnumerateArray()
            .First(c => c.GetProperty("Member").GetString() == "Status")
            .GetProperty("Enum").EnumerateArray().ToList();
        Assert.IsTrue(statusEnum.Any(e => e.GetProperty("Name").GetString() == "NeedAssistance"),
            "enum metadata should carry the CLR member Name");
    }
}
