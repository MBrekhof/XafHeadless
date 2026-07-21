using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// GAP-002: conditional-appearance colors, end to end. The demo's Evaluation.Rating='Good' [Appearance] rule
// (FontColor=Green, TargetItems="*", Context=Employee_Evaluations_ListView) is projected server-side and
// applied client-side by AppearanceEvaluator via DxGrid.CustomizeElement. THE proof: open the pinned
// Employee's DetailView, open the Evaluations nested tab, and assert the rows for that employee's Good-rated
// evaluations render with green text (computed color rgb(0, 128, 0)) -- exactly as many green rows as the
// employee has Good evaluations, and NOT the non-Good rows. Green counts are read from the API at test time,
// so the assertion self-adjusts to whatever the seeded data holds (the pinned Employee has 2 Good + 1 Unset).
[TestClass]
public class AppearanceE2ETests : PlaywrightFixture {
    const string DataRows = ".dxbl-grid-table tbody tr:not(.dxbl-grid-empty-row)";
    const string Green = "rgb(0, 128, 0)"; // System.Drawing.Color.Green -> #008000 -> computed rgb

    static async Task<(int total, int good)> EvaluationCountsAsync() {
        using var api = await ApiClientAsync();
        var resp = await api.GetFromJsonAsync<JsonElement>(
            $"api/odata/Evaluation?$filter=Employee/ID eq {EmployeeKey}&$select=Rating&$top=50");
        var ratings = resp.GetProperty("value").EnumerateArray()
            .Select(r => r.GetProperty("Rating").GetString()).ToList();
        return (ratings.Count, ratings.Count(r => r == "Good"));
    }

    static async Task<int> GreenRowCountAsync(ILocator rows) {
        var count = await rows.CountAsync();
        var green = 0;
        for (var i = 0; i < count; i++) {
            var color = await rows.Nth(i).Locator("td").First
                .EvaluateAsync<string>("el => getComputedStyle(el).color");
            if (color == Green) green++;
        }
        return green;
    }

    [TestMethod]
    public async Task Good_rated_evaluation_rows_render_green_in_the_Employee_Evaluations_tab() {
        var (totalEvals, goodEvals) = await EvaluationCountsAsync();
        Assert.IsTrue(goodEvals >= 1,
            $"the pinned Employee must have at least one Good evaluation for this proof (had {goodEvals} of {totalEvals})");

        await LoginAsync();
        await NavigateSpa($"/detail/Employee_DetailView/{EmployeeKey}");
        await Expect(Page.Locator("h3")).ToHaveTextAsync("Employee", new() { Timeout = 15000 });

        // Open the Evaluations nested tab.
        await Page.Locator("[role=tab]").Filter(new() { HasTextString = "Evaluations" }).First.ClickAsync();

        // Scope to the Evaluations nested-list section (Employee_DetailView also has Tasks + Picture grids).
        var evalSection = Page.Locator(".xaf-nested-list")
            .Filter(new() { Has = Page.GetByText("Evaluations", new() { Exact = true }) });
        var evalRows = evalSection.Locator(DataRows);
        await Expect(evalRows).ToHaveCountAsync(totalEvals, new() { Timeout = 15000 });

        // Poll (bounded) for the green styling to settle: CustomizeElement fires as the grid re-renders.
        var green = 0;
        for (var i = 0; i < 20; i++) {
            green = await GreenRowCountAsync(evalRows);
            if (green == goodEvals) break;
            await Page.WaitForTimeoutAsync(250);
        }
        await Shot("GAP002-evaluations-green");

        Assert.AreEqual(goodEvals, green,
            $"expected {goodEvals} green (Good-rated) rows of {totalEvals}, got {green} -- the Rating='Good' " +
            "appearance rule did not color exactly the Good rows");
        // Conditional, not blanket: the non-Good rows must NOT be green.
        Assert.IsTrue(green < totalEvals,
            "every row was green -- the rule is not being applied conditionally on Rating='Good'");
    }
}
