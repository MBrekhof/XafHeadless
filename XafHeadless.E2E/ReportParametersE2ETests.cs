using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// RPT-001 (parameter form): a report that declares parameters asks for them before running; one that
// declares none still runs on a single click.
//
// The form renders through the SAME editors a DetailView uses -- the API projects a parameter's CLR type
// as the same hint ClassifyDataType emits, so a synthetic LayoutNode plus a DetailViewState is enough.
// That is why a date parameter gets a real date editor rather than a text box, and it is worth asserting:
// the reuse is the design, not an implementation detail.
//
// REQUIRES A RUNNING JOBSERVER for the run half -- there is no render without a worker.
[TestClass]
public class ReportParametersE2ETests : PlaywrightFixture {
    // ProductOrders declares two visible parameters (Product: Guid -> string, OrderDate: date).
    const string WithParameters = "ProductOrders";
    // CustomerProfile declares none, so it must not stop to ask.
    const string WithoutParameters = "CustomerProfile";

    [TestMethod]
    public async Task AReportWithParameters_AsksForThemBeforeRunning() {
        await LoginAsync();
        await NavigateSpa("/reports");

        var row = Page.GetByRole(AriaRole.Row).Filter(new() { HasTextString = WithParameters });
        await Expect(row).ToHaveCountAsync(1, new() { Timeout = 20000 });
        await row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^run", RegexOptions.IgnoreCase) })
            .ClickAsync();

        // The form appears instead of the report running immediately.
        await Expect(Page.GetByText(new Regex("Parameters for", RegexOptions.IgnoreCase)))
            .ToBeVisibleAsync(new() { Timeout = 20000 });

        // Both declared parameters are offered, by their captions.
        await Expect(Page.GetByText("OrderDate", new() { Exact = true }).First)
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.GetByText("Product", new() { Exact = true }).First)
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Shot("rpt001-03-parameter-form");

        // The date parameter renders as a real DATE editor rather than a text box -- the point of sharing
        // the hint vocabulary with the DetailView editors.
        //
        // Asserted BEHAVIOURALLY, not by CSS class. DevExpress class names are internal and this session
        // has already had three guesses at them fail. The observable difference is the value: the API
        // sends this default as "23/05/2024 00:00:00", and only a date editor parses and re-renders it
        // without the time component. A StringEditor would show the raw string verbatim.
        var values = await Page.Locator("input").AllAsync();
        var sawDateOnly = false;
        foreach (var input in values) {
            var value = await input.InputValueAsync();
            if (Regex.IsMatch(value, @"^\d{1,2}[/.-]\d{1,2}[/.-]\d{4}$")) { sawDateOnly = true; break; }
        }
        Assert.IsTrue(sawDateOnly,
            "the date parameter must render through the date editor -- a raw 'dd/MM/yyyy 00:00:00' string means it fell back to a text box");

        // Running from the form still produces a PDF. The download event is the assertion, not a message:
        // a page can say "downloaded" without a file existing.
        var downloadTask = Page.WaitForDownloadAsync(new() { Timeout = 90000 });
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^run report$", RegexOptions.IgnoreCase) })
            .ClickAsync();
        var download = await downloadTask;
        var path = await download.PathAsync();
        Assert.IsNotNull(path);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.AreEqual("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [TestMethod]
    public async Task AReportWithoutParameters_RunsOnASingleClick() {
        await LoginAsync();
        await NavigateSpa("/reports");

        var row = Page.GetByRole(AriaRole.Row).Filter(new() { HasTextString = WithoutParameters });
        await Expect(row).ToHaveCountAsync(1, new() { Timeout = 20000 });

        // Arm before clicking: with no parameters this goes straight to rendering.
        var downloadTask = Page.WaitForDownloadAsync(new() { Timeout = 90000 });
        await row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^run", RegexOptions.IgnoreCase) })
            .ClickAsync();

        // No form -- making every report open an empty one would be worse than useless.
        await Expect(Page.GetByText(new Regex("Parameters for", RegexOptions.IgnoreCase)))
            .ToHaveCountAsync(0, new() { Timeout = 5000 });

        var download = await downloadTask;
        var path = await download.PathAsync();
        Assert.IsNotNull(path, "the browser reported a download with no file behind it");

        // Assertions inherited from ReportRunE2ETests, which this test replaced: that file covered
        // "click Run -> get a PDF" against ProductOrders, and the parameter form made its premise false --
        // that report now stops to ask. Rather than leave a test asserting behaviour that changed, its
        // checks live here, on the report that genuinely still runs in one click.
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.IsTrue(bytes.Length > 1000,
            $"a real rendered report is not a handful of bytes -- got {bytes.Length}, which an error page would be");
        Assert.AreEqual("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5),
            "the downloaded file must actually be a PDF, not an error body with a .pdf name");
        await Shot("rpt001-04-parameterless-one-click");
    }
}
