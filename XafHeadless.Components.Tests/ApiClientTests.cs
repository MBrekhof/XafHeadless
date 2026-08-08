using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XafHeadless.Components.Contracts;
using XafHeadless.Components.Services;

namespace XafHeadless.Components.Tests;

// Runtime diagnostics (docs/superpowers/specs/2026-08-08-runtime-diagnostics-design.md): a failed API
// call must name itself. The motivating case cost a live debugging session -- the only thing the old
// EnsureSuccessStatusCode() threw was "Response status code does not indicate success: 400 (Bad
// Request)": no URL, no query string, and not the server's error body, which for OData is where the
// actual reason lives.
[TestClass]
public class ApiClientTests {
    sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(body),
                RequestMessage = request
            });
    }

    static ApiClient Client(HttpStatusCode status, string body) {
        var http = new HttpClient(new StubHandler(status, body)) {
            BaseAddress = new Uri("http://localhost:5200/")
        };
        var auth = new AuthState();
        auth.SetToken("a-token");
        return new ApiClient(http, auth, NullLogger<ApiClient>.Instance);
    }

    [TestMethod]
    public async Task GetPageAsync_failure_names_the_request_and_the_servers_own_reason() {
        // The real 400 body this host returns for an instant-literal date filter (see
        // ODataFilterTranslatorTests.DateTime_comparisons_translate_over_date_not_instant_literals).
        const string odataError = """
            {"error":{"code":"","message":"The binary operator GreaterThanOrEqual is not defined for the types 'System.Nullable`1[System.DateTime]' and 'System.Nullable`1[System.DateTimeOffset]'."}}
            """;
        var client = Client(HttpStatusCode.BadRequest, odataError);

        var ex = await Assert.ThrowsExactlyAsync<ApiRequestException>(() => client.GetPageAsync(
            "Order", new ODataQuery(0, 25, null, "date(OrderDate) ge 2026-04-04", null, null)));

        StringAssert.Contains(ex.Message, "GET", "the method must be in the message");
        StringAssert.Contains(ex.Message, "api/odata/Order", "the URL must be in the message");
        StringAssert.Contains(ex.Message, "$filter=date(OrderDate)", "the query string is the whole point");
        StringAssert.Contains(ex.Message, "400", "the status must be in the message");
        StringAssert.Contains(ex.Message, "GreaterThanOrEqual is not defined",
            "the server's own reason must survive into the exception");
        Assert.AreEqual(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [TestMethod]
    public async Task ApiRequestException_body_is_bounded_so_an_html_error_page_cannot_flood_the_log() {
        var client = Client(HttpStatusCode.InternalServerError, new string('x', 10_000));

        var ex = await Assert.ThrowsExactlyAsync<ApiRequestException>(() => client.GetPageAsync(
            "Order", new ODataQuery(0, 25, null, null, null, null)));

        Assert.IsTrue(ex.Body!.Length <= ApiRequestException.MaxBodyLength,
            $"body excerpt must be capped at {ApiRequestException.MaxBodyLength}, was {ex.Body.Length}");
        StringAssert.Contains(ex.Message, "500");
    }
}
