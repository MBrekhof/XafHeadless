using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XafHeadless.E2E;

// RPT-001 (run half): clicking Run renders a report in the JOB SERVER and hands the PDF to the browser.
//
// The whole chain is exercised here, which is the point -- the API endpoints were proven by hand, but
// nothing had ever driven them from the UI: enqueue -> Hangfire -> JobServer renders -> artifact stored
// with its requester -> the page polls -> the bytes come back and become a download.
//
// The assertion is on the browser's DOWNLOAD event, not on a success message. A page can say "downloaded"
// without a file ever existing; a download event cannot be faked by optimistic UI. The file's size and
// PDF magic bytes are checked too, because a zero-byte or HTML-error "download" would still fire the event.
//
// REQUIRES A RUNNING JOBSERVER. Unlike the Api-level tests, this one cannot avoid that -- there is no
// render without a worker. It is the only new test with that dependency, and it is inherent, not an
// oversight.
[TestClass]
public class ReportRunE2ETests : PlaywrightFixture {
    [TestMethod]
    public async Task RunningAReport_DownloadsAPdf() {
        await LoginAsync();
        await NavigateSpa("/reports");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/reports$"), new() { Timeout = 20000 });

        // "Orders" is the report the existing scheduled job renders, so it is known to render cleanly.
        var row = Page.GetByRole(AriaRole.Row).Filter(new() { HasTextString = "ProductOrders" });
        await Expect(row).ToHaveCountAsync(1, new() { Timeout = 20000 });

        var run = row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^run", RegexOptions.IgnoreCase) });
        await Expect(run).ToBeEnabledAsync(new() { Timeout = 10000 });

        // Arm the download waiter BEFORE clicking: the render takes seconds, but the download fires as
        // soon as the poll succeeds and a waiter attached afterwards could miss it.
        var downloadTask = Page.WaitForDownloadAsync(new() { Timeout = 90000 });
        await run.ClickAsync();

        var download = await downloadTask;
        var path = await download.PathAsync();
        Assert.IsNotNull(path, "the browser reported a download with no file behind it");

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.IsTrue(bytes.Length > 1000,
            $"a real rendered report is not a handful of bytes -- got {bytes.Length}, which an error page would be");
        Assert.AreEqual("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5),
            "the downloaded file must actually be a PDF, not an error body with a .pdf name");

        await Expect(Page.GetByText(new Regex("downloaded", RegexOptions.IgnoreCase)).First)
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Shot("rpt001-02-report-downloaded");
    }
}
