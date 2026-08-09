using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Services;

// Typed API client. Server wire contract (Tasks 3-6, see docs/notes/save-contract.md):
// PascalCase JSON everywhere, JWT bearer auth, OData reads only (writes MUST go through
// api/save/{entity}/{key} -- OData PATCH is non-validating on this host).
//
// Runtime diagnostics (docs/superpowers/specs/2026-08-08-runtime-diagnostics-design.md): every failure
// here is recorded. Two kinds:
//  - The caller cannot degrade past it (grid reads) -> ApiRequestException, naming method, URL, status
//    and the server's own error body.
//  - The caller deliberately degrades (a null view, an empty menu, a dropped pref) -> behaviour is
//    UNCHANGED, but the failure is logged at Warning instead of vanishing.
public class ApiClient(HttpClient http, AuthState authState, ILogger<ApiClient> logger) {
    public async Task<string?> LoginAsync(string user, string pass) {
        var response = await http.PostAsJsonAsync("api/Authentication/Authenticate",
            new { userName = user, password = pass });
        if (!response.IsSuccessStatusCode) return null;
        var token = (await response.Content.ReadAsStringAsync()).Trim('"');
        authState.SetToken(token);
        return token;
    }

    public async Task<ViewMetadata?> GetViewAsync(string viewId) {
        ApplyAuthHeader();
        var response = await http.GetAsync($"api/model/views/{viewId}");
        if (CheckUnauthorized(response)) return null;
        if (!response.IsSuccessStatusCode) {
            LogDegraded(response, $"view metadata for '{viewId}' unavailable, caller renders its error state");
            return null;
        }
        return await response.Content.ReadFromJsonAsync<ViewMetadata>();
    }

    // GAP-004: the flat, security-trimmed nav menu. Empty list (never null) on any failure/401 --
    // callers (NavMenu, Home/Login's startup routing) treat "no items" as "route to /login".
    public async Task<List<NavigationItemDto>> GetNavigationAsync() {
        ApplyAuthHeader();
        var response = await http.GetAsync("api/model/navigation");
        if (CheckUnauthorized(response)) return new();
        if (!response.IsSuccessStatusCode) {
            LogDegraded(response, "navigation menu empty, callers route to /login");
            return new();
        }
        return await response.Content.ReadFromJsonAsync<List<NavigationItemDto>>() ?? new();
    }

    public async Task<ODataPage> GetPageAsync(string entity, ODataQuery q, CancellationToken ct = default) {
        ApplyAuthHeader();
        var response = await http.GetAsync($"api/odata/{entity}{ODataQueryBuilder.Build(q)}", ct);
        if (CheckUnauthorized(response)) return new ODataPage([], 0);
        // Surfaced to the grid via GridCustomDataSource.ExceptionHandler (ODataGridDataSource), which
        // renders it instead of letting it terminate the circuit.
        await EnsureSuccessAsync(response, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var rows = doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => e.Clone()).ToArray();
        var total = doc.RootElement.GetProperty("@odata.count").GetInt64();
        return new ODataPage(rows, total);
    }

    // Task 4.3 (GRID-001 server-side grouping): one $apply=groupby fetch. The XAF WebApi pipeline
    // returns aggregation results as a BARE JSON array (no @odata wrapper -- live probe evidence in the
    // companion headless implementation); the standard {"value":[...]} wrapper is tolerated
    // defensively.
    public async Task<JsonElement[]> GetGroupsAsync(string entity, string apply, string? orderBy,
            int? top = null, CancellationToken ct = default) {
        ApplyAuthHeader();
        var response = await http.GetAsync($"api/odata/{entity}{ODataQueryBuilder.BuildGroups(apply, orderBy, top)}", ct);
        if (CheckUnauthorized(response)) return [];
        await EnsureSuccessAsync(response, ct); // surfaces via ExceptionHandler, like GetPageAsync
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var buckets = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("value");
        return buckets.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    // RPT-001: the report catalogue -- which reports this app has. Degrades to empty rather than throwing:
    // a reporting menu that cannot load its list should show nothing, not take the page down.
    public async Task<ReportSummary[]> GetReportsAsync() {
        ApplyAuthHeader();
        var response = await http.GetAsync("api/reports");
        if (CheckUnauthorized(response)) return [];
        if (!response.IsSuccessStatusCode) {
            LogDegraded(response, "report catalogue not loaded");
            return [];
        }
        return await response.Content.ReadFromJsonAsync<ReportSummary[]>() ?? [];
    }

    // RPT-001: enqueue a render. Returns the correlation id to poll, or null when the run was refused --
    // the API answers 404 for a report this user cannot see, so a null here is "you may not run that",
    // not a transport failure.
    public async Task<Guid?> RunReportAsync(string reportId, string? criteria = null) {
        ApplyAuthHeader();
        var url = $"api/reports/{Uri.EscapeDataString(reportId)}/run"
                  + (string.IsNullOrWhiteSpace(criteria) ? "" : $"?criteria={Uri.EscapeDataString(criteria)}");
        var response = await http.PostAsync(url, null);
        if (CheckUnauthorized(response)) return null;
        if (!response.IsSuccessStatusCode) {
            LogDegraded(response, $"report '{reportId}' could not be started");
            return null;
        }
        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>();
        return accepted?.CorrelationId;
    }

    // Collect a finished render. Null means NOT READY YET (the API answers 202 while the job is still
    // running), which is an ordinary state the caller polls on -- distinct from a failure, which is
    // logged. Returns the PDF bytes and its content type once the job has committed.
    public async Task<(byte[] Content, string ContentType, string FileName)?> CollectReportAsync(Guid correlationId) {
        ApplyAuthHeader();
        var response = await http.GetAsync($"api/reports/runs/{correlationId}");
        if (CheckUnauthorized(response)) return null;
        if (response.StatusCode == HttpStatusCode.Accepted) return null;      // still rendering
        if (!response.IsSuccessStatusCode) {
            LogDegraded(response, $"report run '{correlationId}' could not be collected");
            return null;
        }
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? "report.pdf";
        return (bytes, contentType, fileName);
    }

    public record RunAccepted(Guid CorrelationId);

    // CRUD-002: removes an object through the same validating write path as save/create (never OData
    // DELETE, which ODataReadOnlyMiddleware blocks host-wide). Returns whether it actually happened -- the
    // caller refreshes a grid on the strength of this, and a refused delete that reported success would
    // show a row vanishing and then reappearing.
    public async Task<bool> DeleteAsync(string entity, string key) {
        ApplyAuthHeader();
        var response = await http.DeleteAsync($"api/save/{entity}/{key}");
        if (CheckUnauthorized(response)) return false;
        if (response.IsSuccessStatusCode) return true;
        LogDegraded(response, $"delete of {entity} '{key}' was refused");
        return false;
    }

    // PH2-005 / LOOKUP-001: the lookup editor's candidate feed. `key` asks the server to include the object
    // the record ALREADY references even when it falls outside the page or the search -- without it an
    // editor can silently drop an existing value, which is what the old OData top-50 fetch did (Employee
    // has 51 rows). Degrades to an empty list rather than throwing: a lookup that cannot load its
    // candidates must not take the whole form down with it.
    public async Task<LookupItem[]> GetLookupItemsAsync(string type, string? search = null,
            string? key = null, int? top = null) {
        ApplyAuthHeader();
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(key)) query.Add($"key={Uri.EscapeDataString(key)}");
        if (top is not null) query.Add($"top={top}");
        var url = $"api/lookup/{type}{(query.Count == 0 ? "" : "?" + string.Join("&", query))}";
        var response = await http.GetAsync(url);
        if (CheckUnauthorized(response)) return [];
        if (!response.IsSuccessStatusCode) {
            LogDegraded(response, $"lookup candidates for '{type}' not loaded");
            return [];
        }
        return await response.Content.ReadFromJsonAsync<LookupItem[]>() ?? [];
    }

    // CRUD-001: the client half of GAP-003. POST api/save/{type} carries NO key -- the server calls
    // CreateObject, applies the members through the same gate as an update, commits with validation, and
    // answers 201 { key } with the key IT generated (BaseObject.ID, a Guid). The caller needs that key to
    // navigate to the object it just made, so it is carried on SaveOutcome rather than discarded.
    public async Task<SaveOutcome> CreateAsync(string entity, Dictionary<string, object?> values) {
        ApplyAuthHeader();
        var response = await http.PostAsJsonAsync($"api/save/{entity}", values);
        if (CheckUnauthorized(response)) return new SaveOutcome(false, new(), ["Not authorized."]);
        if (!response.IsSuccessStatusCode) return await FailureOutcomeAsync(response);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new SaveOutcome(true, new(), [],
            created.TryGetProperty("key", out var k) ? k.GetString() : null);
    }

    public async Task<SaveOutcome> SaveAsync(string entity, string key, Dictionary<string, object?> changes) {
        ApplyAuthHeader();
        var response = await http.PostAsJsonAsync($"api/save/{entity}/{key}", changes);
        if (CheckUnauthorized(response)) return new SaveOutcome(false, new(), ["Not authorized."]);
        if (response.IsSuccessStatusCode) return new SaveOutcome(true, new(), []);
        return await FailureOutcomeAsync(response);
    }

    // The failure contract BOTH write paths share (docs/notes/save-contract.md): a 422 carries
    // MemberErrors keyed by member name, for display at the offending editor, plus whole-object Messages.
    // Anything else is an unexpected status and says so rather than pretending to be a validation result.
    static async Task<SaveOutcome> FailureOutcomeAsync(HttpResponseMessage response) {
        if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
            return new SaveOutcome(false, new(), [$"Unexpected status {(int)response.StatusCode}."]);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var memberErrors = body.GetProperty("MemberErrors").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        var messages = body.GetProperty("Messages").EnumerateArray()
            .Select(m => m.GetString() ?? "").ToArray();
        return new SaveOutcome(false, memberErrors, messages);
    }

    public async Task<CommandResult> ExecuteCommandAsync(string id, string[] keys) {
        ApplyAuthHeader();
        var response = await http.PostAsJsonAsync($"api/commands/{id}", new CommandRequest(keys));
        if (CheckUnauthorized(response)) return new CommandResult(false, "Not authorized.", []);
        if (!response.IsSuccessStatusCode) return new CommandResult(false, $"Unexpected status {(int)response.StatusCode}.", []);
        return await response.Content.ReadFromJsonAsync<CommandResult>()
            ?? new CommandResult(false, "Empty response.", []);
    }

    // GAP-008: per-user grid-layout prefs. GetPrefsAsync returns null when the user has no saved layout
    // for this view (server 204) or on any failure -- callers just fall back to the default layout.
    public async Task<string?> GetPrefsAsync(string viewId) {
        ApplyAuthHeader();
        var response = await http.GetAsync($"api/prefs/{viewId}");
        if (CheckUnauthorized(response) || response.StatusCode == HttpStatusCode.NoContent) return null;
        if (!response.IsSuccessStatusCode) {
            // 204 above is "no saved layout", the normal case -- not a failure, so not logged.
            LogDegraded(response, $"layout prefs for '{viewId}' not loaded, grid uses its default layout");
            return null;
        }
        var body = await response.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(body) ? null : body;
    }

    // Best-effort upsert of the layout blob. A failed save must never disrupt the grid interaction, so a
    // non-success is swallowed (a 401 still clears the token via CheckUnauthorized). An empty blob clears
    // the pref server-side.
    public async Task SavePrefsAsync(string viewId, string json) {
        ApplyAuthHeader();
        var response = await http.PutAsync($"api/prefs/{viewId}",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        if (CheckUnauthorized(response) || response.IsSuccessStatusCode) return;
        LogDegraded(response, $"layout prefs for '{viewId}' not saved, the user's next visit sees the old layout");
    }

    void ApplyAuthHeader() => http.DefaultRequestHeaders.Authorization =
        authState.Token is null ? null : new AuthenticationHeaderValue("Bearer", authState.Token);

    // Replaces EnsureSuccessStatusCode() on the paths whose failure must reach the caller. Reads the
    // body FIRST -- that is where an OData error states the actual reason.
    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct) {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ApiRequestException(
            response.RequestMessage?.Method ?? HttpMethod.Get,
            response.RequestMessage?.RequestUri?.ToString() ?? "(unknown url)",
            response.StatusCode, response.ReasonPhrase, ApiRequestException.Excerpt(body));
    }

    // For the paths that swallow a failure by design: keep the behaviour, lose the silence.
    void LogDegraded(HttpResponseMessage response, string outcome) => logger.LogWarning(
        "API request failed: {Method} {Url} -> {Status} {Reason}; {Outcome}",
        response.RequestMessage?.Method, response.RequestMessage?.RequestUri,
        (int)response.StatusCode, response.ReasonPhrase, outcome);

    bool CheckUnauthorized(HttpResponseMessage response) {
        if (response.StatusCode != HttpStatusCode.Unauthorized) return false;
        authState.SetToken(null);
        return true;
    }
}
