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

    // Records the URL the client actually built, so a test can assert on query parameters rather than
    // trusting that they were sent.
    sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler {
        public string? LastUrl { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(body), RequestMessage = request
            });
        }
    }

    static ApiClient ClientWith(HttpMessageHandler handler) {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5200/") };
        var auth = new AuthState();
        auth.SetToken("a-token");
        return new ApiClient(http, auth, NullLogger<ApiClient>.Instance);
    }

    static ApiClient Client(HttpStatusCode status, string body) {
        var http = new HttpClient(new StubHandler(status, body)) {
            BaseAddress = new Uri("http://localhost:5200/")
        };
        var auth = new AuthState();
        auth.SetToken("a-token");
        return new ApiClient(http, auth, NullLogger<ApiClient>.Instance);
    }

    // ---- CRUD-002: removing an aggregated child ----

    // The delete endpoint answers 204 (proven live against the host before this was written). The caller
    // refreshes the grid on success, so the outcome has to be reported rather than assumed.
    [TestMethod]
    public async Task DeleteAsync_reports_success_and_targets_the_save_route() {
        var handler = new CapturingHandler(HttpStatusCode.NoContent, "");
        var client = ClientWith(handler);

        Assert.IsTrue(await client.DeleteAsync("OrderItem", "k1"));
        StringAssert.Contains(handler.LastUrl!, "api/save/OrderItem/k1");
    }

    // A refused delete (no permission, or a validation rule) must NOT report success -- the row would
    // vanish from the grid on refresh only to reappear, or worse, look deleted when it is not.
    [TestMethod]
    public async Task DeleteAsync_reports_failure_rather_than_pretending() {
        Assert.IsFalse(await Client(HttpStatusCode.Forbidden, "").DeleteAsync("OrderItem", "k1"));
    }

    // ---- PH2-005 / LOOKUP-001: the lookup candidate feed ----

    // The `key` parameter is the whole point: it asks the server to include the object the record ALREADY
    // references even when it falls outside the page or the search. Without it an editor silently drops an
    // existing value, which is exactly what the old OData top-50 fetch did (Employee has 51 rows).
    [TestMethod]
    public async Task GetLookupItemsAsync_passes_the_current_key_so_the_server_can_include_it() {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """[{"Key":"k1","Text":"Amelia Harper"}]""");
        var client = ClientWith(handler);

        var items = await client.GetLookupItemsAsync("Employee", search: "har", key: "k1", top: 25);

        Assert.HasCount(1, items);
        Assert.AreEqual("Amelia Harper", items[0].Text);
        StringAssert.Contains(handler.LastUrl!, "api/lookup/Employee", "wrong endpoint");
        StringAssert.Contains(handler.LastUrl!, "key=k1", "the current key must reach the server");
        StringAssert.Contains(handler.LastUrl!, "search=har", "the search term must reach the server");
        StringAssert.Contains(handler.LastUrl!, "top=25");
    }

    // A lookup that cannot load its candidates must not take the whole form down -- every other binding in
    // this client degrades the same way.
    [TestMethod]
    public async Task GetLookupItemsAsync_degrades_to_empty_rather_than_throwing() {
        var client = Client(HttpStatusCode.InternalServerError, "boom");
        Assert.IsEmpty(await client.GetLookupItemsAsync("Employee"));
    }

    // ---- CRUD-001: the client half of GAP-003 ----

    // The create endpoint (POST api/save/{type}) shipped and was proven server-side on 2026-07-12, but no
    // client method ever reached it -- ApiClient could only update an EXISTING key. The server answers 201
    // with the key it generated (the client never sends one; CreateObject/CommitChanges assign it), and the
    // caller needs that key to navigate to the object it just created.
    [TestMethod]
    public async Task CreateAsync_returns_the_server_generated_key() {
        var client = Client(HttpStatusCode.Created, """{"key":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"}""");

        var outcome = await client.CreateAsync("Order",
            new Dictionary<string, object?> { ["InvoiceNumber"] = "0100001" });

        Assert.IsTrue(outcome.Success);
        Assert.AreEqual("3f2504e0-4f89-11d3-9a0c-0305e82c3301", outcome.Key,
            "the caller navigates to the object it just made, so the server-generated key must survive the round trip");
    }

    // Same 422 contract SaveAsync already honours (docs/notes/save-contract.md): a create rejected by
    // validation must come back with field-level errors, not a bare failure.
    [TestMethod]
    public async Task CreateAsync_surfaces_member_errors_on_422() {
        var client = Client(HttpStatusCode.UnprocessableEntity,
            """{"MemberErrors":{"FirstName":"First Name must not be empty."},"Messages":["Validation failed."]}""");

        var outcome = await client.CreateAsync("Employee", new Dictionary<string, object?>());

        Assert.IsFalse(outcome.Success);
        Assert.IsNull(outcome.Key, "a rejected create has no key to navigate to");
        Assert.AreEqual("First Name must not be empty.", outcome.MemberErrors["FirstName"]);
        CollectionAssert.Contains(outcome.Messages, "Validation failed.");
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

        Assert.IsLessThanOrEqualTo(ApiRequestException.MaxBodyLength, ex.Body!.Length,
            "the body excerpt must be capped");
        StringAssert.Contains(ex.Message, "500");
    }
}
