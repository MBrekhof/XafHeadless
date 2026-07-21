using System.Net;

namespace XafHeadless.JobServer.Tests;

// Ported from a companion headless implementation's job server boot test -- same shape, this design's JobServer
// exposes the identical anonymous /health endpoint. If the host boots and /health answers 200, Hangfire's
// server + client resolved at startup (and, in this design, HostDatabaseInitializer's IDBUpdater run and
// the seed completed without throwing -- both happen before the host starts accepting requests).
[TestClass]
public class JobServerBootTests : JobServerTestBase {
    [TestMethod]
    public async Task Health_endpoint_returns_200_proving_the_host_booted_with_hangfire() {
        using var http = JobServerHealthClient();
        var resp = await http.GetAsync("health");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"JobServer /health should be 200 (host booted with Hangfire): {resp.StatusCode}");
    }
}
