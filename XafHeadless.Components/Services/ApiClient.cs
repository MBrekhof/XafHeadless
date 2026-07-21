using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using XafHeadless.Components.Contracts;

namespace XafHeadless.Components.Services;

// Typed API client. Server wire contract (Tasks 3-6, see docs/notes/save-contract.md):
// PascalCase JSON everywhere, JWT bearer auth, OData reads only (writes MUST go through
// api/save/{entity}/{key} -- OData PATCH is non-validating on this host).
public class ApiClient(HttpClient http, AuthState authState) {
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
        if (CheckUnauthorized(response) || !response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<ViewMetadata>();
    }

    // GAP-004: the flat, security-trimmed nav menu. Empty list (never null) on any failure/401 --
    // callers (NavMenu, Home/Login's startup routing) treat "no items" as "route to /login".
    public async Task<List<NavigationItemDto>> GetNavigationAsync() {
        ApplyAuthHeader();
        var response = await http.GetAsync("api/model/navigation");
        if (CheckUnauthorized(response) || !response.IsSuccessStatusCode) return new();
        return await response.Content.ReadFromJsonAsync<List<NavigationItemDto>>() ?? new();
    }

    public async Task<ODataPage> GetPageAsync(string entity, ODataQuery q, CancellationToken ct = default) {
        ApplyAuthHeader();
        var response = await http.GetAsync($"api/odata/{entity}{ODataQueryBuilder.Build(q)}", ct);
        if (CheckUnauthorized(response)) return new ODataPage([], 0);
        response.EnsureSuccessStatusCode(); // was: silently swallowed into an empty page -- callers
                                             // (the grid) now surface this via GridCustomDataSource.ExceptionHandler.
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
        response.EnsureSuccessStatusCode(); // surfaces via GridCustomDataSource.ExceptionHandler, like GetPageAsync
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var buckets = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("value");
        return buckets.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    public async Task<SaveOutcome> SaveAsync(string entity, string key, Dictionary<string, object?> changes) {
        ApplyAuthHeader();
        var response = await http.PostAsJsonAsync($"api/save/{entity}/{key}", changes);
        if (CheckUnauthorized(response)) return new SaveOutcome(false, new(), ["Not authorized."]);
        if (response.IsSuccessStatusCode) return new SaveOutcome(true, new(), []);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity) {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var memberErrors = body.GetProperty("MemberErrors").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            var messages = body.GetProperty("Messages").EnumerateArray()
                .Select(m => m.GetString() ?? "").ToArray();
            return new SaveOutcome(false, memberErrors, messages);
        }
        return new SaveOutcome(false, new(), [$"Unexpected status {(int)response.StatusCode}."]);
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
        if (CheckUnauthorized(response) || response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
            return null;
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
        CheckUnauthorized(response);
    }

    void ApplyAuthHeader() => http.DefaultRequestHeaders.Authorization =
        authState.Token is null ? null : new AuthenticationHeaderValue("Bearer", authState.Token);

    bool CheckUnauthorized(HttpResponseMessage response) {
        if (response.StatusCode != HttpStatusCode.Unauthorized) return false;
        authState.SetToken(null);
        return true;
    }
}
