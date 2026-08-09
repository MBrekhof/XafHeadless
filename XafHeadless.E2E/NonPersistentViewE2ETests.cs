using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace XafHeadless.E2E;

// NPO-001: the live proof that a non-persistent [DomainComponent] view now renders REAL DATA in the
// client. Before this card the view projected its metadata fine and then loaded nothing -- there is no
// DbSet behind Opportunity, so OData could not serve it at any URL.
//
// Opportunity is the subject because it is deterministic without seeded data: exactly 4 rows, one per
// Stage enum value except Summary. The values it aggregates come from Quote and will differ per seed;
// the ROW SET will not, so this asserts on the stage names rather than on money.
//
// Reached by direct route, not from the nav menu: the demo exposes Opportunity only through its
// `Opportunities` DashboardView, and rendering dashboards is DASH-001 (still open, and blocked on
// exactly this card). The view itself is reachable and that is what NPO-001 set out to deliver.
[TestClass]
public class NonPersistentViewE2ETests : PlaywrightFixture {
    const string ViewId = "Opportunity_ListView";

    [TestMethod]
    public async Task Non_persistent_view_renders_its_computed_rows() {
        await LoginAsync();
        await NavigateSpa($"/list/{ViewId}");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/list/{ViewId}$"),
            new() { Timeout = 15000 });

        // The grid must actually paint rows. "Loading…" or "View has no columns." would both mean the
        // data route failed -- which is precisely the pre-NPO-001 behaviour this guards against.
        var grid = Page.Locator(".dxbl-grid");
        await Expect(grid).ToBeVisibleAsync(new() { Timeout = 20000 });

        // One row per Stage except Summary. Asserted by looking for each stage's own cell text, so a
        // partial render (say, 2 rows) fails loudly rather than passing on a row count alone.
        foreach (var stage in new[] { "High", "Medium", "Low", "Unlikely" }) {
            await Expect(grid.GetByText(stage, new() { Exact = true }).First)
                .ToBeVisibleAsync(new() { Timeout = 15000 });
        }
        // Summary spans every other band, so including it would double-count every quote. The demo's own
        // controller excludes it; if it ever appears here the populator has drifted from the module.
        await Expect(grid.GetByText("Summary", new() { Exact = true })).ToHaveCountAsync(0);

        // Read-only by nature: there is no table behind a computed type, so a New button here would open a
        // form that cannot save. The first run of this test rendered one -- the model and the security
        // system both said "yes" and nothing represented "there is no write route".
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "New" })).ToHaveCountAsync(0);

        await Shot("npo-001-opportunity-listview");
    }
}
