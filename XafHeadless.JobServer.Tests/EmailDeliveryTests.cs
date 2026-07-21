using System.Net.Http.Json;
using System.Text.Json;

namespace XafHeadless.JobServer.Tests;

// SVR-001 Task 4.1/4.2 (Dispatch F's live email proof), automated. Divergence from the companion
// headless implementation's email delivery test: the recipient is HARDCODED to
// demo-recipient@xafheadless.local (EmailOrdersReportApiCommand -- not user-suppliable from the
// request), not unique per run like the companion implementation's, so this test distinguishes ITS
// OWN run by TIME (a UtcNow baseline recorded before enqueue), not by a unique recipient.
//
// Precondition: the xafheadless-smtp smtp4dev container (SMTP 2526, web 5312) must be running
// (docker start xafheadless-smtp if it's stopped; use a distinct port if you run other smtp4dev
// containers). EmailSettings:SmtpPort in the JobServer's appsettings.json is 2526.
[TestClass]
[DoNotParallelize]
public class EmailDeliveryTests : JobServerTestBase {
    const string Recipient = "demo-recipient@xafheadless.local";

    [TestMethod]
    public async Task RunNow_delivers_the_PDF_to_the_demo_recipient() {
        var baseline = DateTime.UtcNow;
        var api = await ApiClientAsync("Admin");
        var executionBaseline = await LatestExecutionRecordStartedUtcAsync(api);

        await RunNowAsync(api);

        // smtp4dev field names confirmed live against the running xafheadless-smtp sink: results[]
        // with .to (array of recipient strings), .subject, .attachmentCount, .receivedDate (ISO),
        // newest-first; pageSize=100 keeps the target on page 1.
        using var smtp = new HttpClient { BaseAddress = new Uri(Smtp4devUrl) };
        JsonElement? message = null;
        for (var i = 0; i < 40 && message is null; i++) {
            await Task.Delay(1500);
            JsonElement page;
            try { page = await smtp.GetFromJsonAsync<JsonElement>("api/Messages?pageSize=100"); }
            catch (HttpRequestException e) {
                Assert.Fail($"smtp4dev is not reachable at {Smtp4devUrl} -- start it: " +
                    $"docker start xafheadless-smtp (or docker run -d --name xafheadless-smtp -p 2526:25 -p 5312:80 rnwood/smtp4dev) ({e.Message})");
                return; // unreachable (Assert.Fail throws); keeps the compiler's definite-assignment happy
            }
            foreach (var m in page.GetProperty("results").EnumerateArray()) {
                var receivedDate = DateTimeOffset.Parse(m.GetProperty("receivedDate").GetString()!).UtcDateTime;
                if (receivedDate <= baseline) continue;
                var to = m.GetProperty("to").EnumerateArray().Select(t => t.GetString());
                if (to.Contains(Recipient)) { message = m.Clone(); break; }
            }
        }
        Assert.IsNotNull(message, $"expected mail to {Recipient} within ~60s -- check the JobServer log / smtp4dev sink");
        StringAssert.Contains(message!.Value.GetProperty("subject").GetString(), "Orders",
            "subject should carry the report's display name");
        Assert.AreEqual(1, message.Value.GetProperty("attachmentCount").GetInt32(),
            "the PDF must ride as an attachment");

        // The job's JobExecutionRecord must ALSO reach Success -- the archive write happens after the
        // send, and a failed archive still fails the job even though the mail already left (same lesson
        // the companion implementation's test documents).
        var success = await WaitForNewSuccessAsync(api, executionBaseline);
        Assert.IsNotNull(success, "no Success JobExecutionRecord for this run within 30s");
    }
}
