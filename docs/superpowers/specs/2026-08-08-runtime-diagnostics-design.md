# Runtime diagnostics: make failures self-describing

**Date:** 2026-08-08
**Status:** approved, not yet implemented

## Why

An E2E test (`OrderServerMode_DateFilterRow_SwitchesPageToTheEnteredDay`) fails by clearing the
Order date filter: an OData request returns **400**, the exception is unhandled, and the whole Blazor
circuit terminates. Chasing it produced no evidence, because nothing in the system records what
failed:

- `ApiClient` calls `response.EnsureSuccessStatusCode()`. The resulting message is
  `"Response status code does not indicate success: 400 (Bad Request)."` — no URL, no query string,
  and **not** the server's OData error body, which is where XAF puts the actual reason
  (*"The query specified in the URI is not valid: …"*).
- The Api host logs no request lines. `Microsoft.AspNetCore` is at `Warning`, and a 400 produced
  inside the XAF/OData pipeline is a normal response, not an exception — so it leaves no trace at all.
- `ODataGridDataSource` derives from `GridCustomDataSource` but never sets `ExceptionHandler`, so a
  data-load failure propagates out of a DevExpress callback and kills the circuit.
- No `ErrorBoundary` exists anywhere in the component tree.

The goal of this work is **not** to fix that 400. It is to make the next occurrence of it — and of
every failure like it — name itself.

## Decisions

| Decision | Choice |
|---|---|
| Scope | Self-describing failures. **No new dependencies.** |
| Error detail on screen | Full detail in Development, generic message otherwise. Full detail always in logs. |
| App survival | An `ErrorBoundary` keeps the app alive; failures stop being fatal. |

Explicitly **out of scope** (decided, not deferred by oversight): Serilog or any file sink,
OpenTelemetry, end-to-end correlation IDs, client→server error reporting, and E2E network capture.

## Design

### 1. The API failure names itself — `XafHeadless.Components/Services/ApiClient.cs`

Add `ApiRequestException` (Contracts) carrying `Method`, `Url`, `StatusCode`, `ReasonPhrase`, and a
bounded `Body` excerpt (cap ~2 KB — enough for an OData error, small enough to log).

One private helper replaces the two bare `EnsureSuccessStatusCode()` calls (`GetPageAsync`,
`GetGroupsAsync`):

```
Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
```

It reads the body before throwing, and builds a message of the shape
`GET http://localhost:5200/api/odata/Order?$filter=… → 400 Bad Request: <body excerpt>`.

The paths that deliberately degrade instead of throwing keep that behaviour exactly — but gain an
`ILogger<ApiClient>` warning naming request + status:

| Path | Behaviour today | After |
|---|---|---|
| `GetViewAsync` | non-success → `null` | unchanged, + warning |
| `GetNavigationAsync` | non-success → empty list | unchanged, + warning |
| `GetPrefsAsync` | non-success → `null` | unchanged, + warning |
| `SavePrefsAsync` | non-success swallowed | unchanged, + warning |
| `GetPageAsync` / `GetGroupsAsync` | `EnsureSuccessStatusCode()` | `ApiRequestException` with URL + body |

A silent degrade stays silent for the user and stops being silent for us. `ILogger` is already
available in both hosts' DI (server console; browser console under WASM).

### 2. The grid stops killing the app — `ODataGridDataSource.cs` + `XafListView.razor`

`GridCustomDataSource.ExceptionHandler` is the documented hook
(`Action<GridCustomDataSourceExceptionHandlerArgs>`, with `Exception` and `Handled` — verified via
dxdocs for 26.1). `XafListView` passes an `Action<Exception>` into the `ODataGridDataSource`
constructor; the data source wires it to `ExceptionHandler`, and the handler:

1. logs the exception (full detail, incl. the request line when it is an `ApiRequestException`),
2. sets `Handled = true`,
3. surfaces the message through `XafListView`'s existing `error` field, so the view renders its
   normal error state.

This is the fix for the circuit death, independent of whatever causes the 400.

### 3. An `ErrorBoundary` that reports — `MainLayout.razor`

Wrap `@Body` in `<ErrorBoundary>`:

- `ErrorContent` renders a plain "Something went wrong — reload"; in Development it additionally
  renders the exception type/message, and for `ApiRequestException` the failing request line.
- `Recover()` is called on `NavigationManager.LocationChanged` — without it, one error wedges the
  boundary for the rest of the session.
- The exception is logged in both cases.

The Development flag reaches the RCL as a small record singleton (`DiagnosticsOptions(bool
DetailedErrors)`) registered through `AddXafHeadlessClient(detailedErrors: …)`; `XafHeadless.Web`
passes `builder.Environment.IsDevelopment()`, `XafHeadless.Web.Client` passes
`builder.HostEnvironment.IsDevelopment()`. Keeping it in the one existing registration point is
deliberate — that method is already the single place both hosts configure the client stack.

Additionally, `XafHeadless.Web/Program.cs` sets
`AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment())` so
circuit errors carry detail in dev instead of the generic "turn on DetailedErrors" message.

### 4. The Api logs every failing request — `XafHeadless.Api/Startup.cs`

Inline middleware registered **before** `UseRouting()`, so it observes everything the pipeline
returns, including OData 400s that never surface as exceptions:

```
after await next():
  if (context.Response.StatusCode >= 400)
      log method, path + QueryString, status, elapsed ms, user name
```

400–499 at `Warning`, 500+ at `Error`.

**Why not `UseHttpLogging`:** the built-in middleware logs *every* request and needs an
`IHttpLoggingInterceptor` to narrow itself to failures — more configuration than the ten lines it
would replace, for the same output.

## Verification

- **Unit** (`XafHeadless.Components.Tests`): a stub `HttpMessageHandler` returning 400 with an OData
  error body; assert `ApiClient.GetPageAsync` throws `ApiRequestException` whose message contains the
  request URL, the status, and text from the body. This is the one piece of non-trivial logic here,
  so it gets the one test.
- **Live** (both hosts running): request a deliberately bogus `$filter`, then confirm all three legs
  at once — the Api log names the request, the client-side error carries the server's message, and
  the app renders the error boundary / grid error state instead of terminating the circuit.

## Consequence to accept

None of this reproduces the 400 that motivated it. That bug stays open and gets its own
investigation, run **after** this lands so the investigation has evidence to work with.
