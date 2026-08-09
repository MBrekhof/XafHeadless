using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// RPT-001 (catalogue half): SVR-001 built real PDF rendering in the JobServer and nothing could reach it.
// There was no way to see which reports exist, the only caller hardcoded one report name, and the rendered
// artifact was write-only -- its comment calls a download endpoint "the only intended access path", which
// is intent, not a shipped endpoint. The JobServer has no controllers at all.
//
// This covers the first piece: the catalogue is served, reachable from the menu, and lists real reports.
// Running one is deliberately not here -- rendering is CPU- and native-dependency-heavy, so MIG-002's
// boundary decision keeps it off the API request path.
[TestClass]
public class ReportCatalogueE2ETests : PlaywrightFixture {
    [TestMethod]
    public async Task ReportsPage_IsReachableFromTheMenu_AndListsRealReports() {
        await LoginAsync();

        // Reached the way a user would, not by typing the URL: the page is worthless if nothing links to
        // it, and it is appended client-side rather than coming from api/model/navigation.
        var reportsLink = Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^reports$", RegexOptions.IgnoreCase) });
        await Expect(reportsLink).ToBeVisibleAsync(new() { Timeout = 20000 });
        await reportsLink.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"/reports$"), new() { Timeout = 20000 });

        // The demo's catalogue is a known set of predefined reports; assert on one by name rather than on
        // a row count, so a re-seed that adds or drops a report does not fail the test spuriously.
        await Expect(Page.GetByText("Revenue Analysis").First).ToBeVisibleAsync(new() { Timeout = 20000 });

        // The identifier column matters: two reports in this catalogue are both called "Profile", so the
        // stable resource-type name is the only thing distinguishing them.
        await Expect(Page.GetByText(new Regex(@"OutlookInspiredDemo\.Module\.Resources\.Reports\.")).First)
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Shot("rpt001-01-report-catalogue");
    }
}
