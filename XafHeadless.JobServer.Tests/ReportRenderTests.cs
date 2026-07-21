using Microsoft.Data.SqlClient;

namespace XafHeadless.JobServer.Tests;

// SVR-001 Task 3.3 render proof, automated. Divergence from the companion headless implementation's
// report render test:
// ReportArtifact is NOT OData-exposed and has NO download endpoint (by design, plan Task 2.2 -- see
// XafHeadless.JobServer\BusinessObjects\ReportArtifact.cs's header comment), so a direct SQL read of the
// host catalog is the only observable path -- there is no HTTP GET to assert against. Triggered the same
// way as RunNowTests (the Api's EmailOrdersReport command); the PDF render + artifact write happen as a
// side effect of that job reaching Success.
[TestClass]
[DoNotParallelize]
public class ReportRenderTests : JobServerTestBase {
    [TestMethod]
    public async Task RunNow_renders_a_valid_PDF_ReportArtifact() {
        var api = await ApiClientAsync("Admin");
        var baseline = await LatestExecutionRecordStartedUtcAsync(api);

        await RunNowAsync(api);
        var success = await WaitForNewSuccessAsync(api, baseline);
        Assert.IsNotNull(success, "a NEW JobExecutionRecord should have reached Status=Success within 30s");

        await using var connection = new SqlConnection(HostConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP 1 Content FROM ReportArtifact ORDER BY CreatedUtc DESC";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync(), "no ReportArtifact row found in the host catalog");
        var pdf = (byte[])reader["Content"];

        Assert.IsTrue(pdf.Length > 4 && pdf[0] == 0x25 && pdf[1] == 0x50 && pdf[2] == 0x44 && pdf[3] == 0x46,
            "stored ReportArtifact.Content does not begin with the %PDF magic number");
    }
}
