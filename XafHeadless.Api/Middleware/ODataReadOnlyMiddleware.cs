namespace XafHeadless.Api.Middleware;

// SEC-001. The honest-wire save contract says the OData surface is READ-ONLY: all mutations go
// through the validated POST /api/save/{type}/{key} path (which runs XAF validation), because the
// built-in XAF OData CRUD endpoints on a headless host do NOT initiate validation (no live
// View/Frame activates PersistenceValidationController — see docs/notes/save-contract.md). This
// edge guard rejects any mutating verb under /api/odata with 405 before it can reach those
// non-validating endpoints, so the host matches its own documented contract from day one.
public class ODataReadOnlyMiddleware {
    static readonly string[] MutatingMethods = { "POST", "PUT", "PATCH", "DELETE", "MERGE" };
    readonly RequestDelegate next;
    public ODataReadOnlyMiddleware(RequestDelegate next) => this.next = next;

    public async Task Invoke(HttpContext context) {
        if (context.Request.Path.StartsWithSegments("/api/odata", StringComparison.OrdinalIgnoreCase)
            && MutatingMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase)) {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET";
            await context.Response.WriteAsJsonAsync(new {
                error = "OData is read-only on this host.",
                detail = "Mutations must use POST /api/save/{type}/{key}, which runs XAF validation. "
                       + "See docs/notes/save-contract.md."
            });
            return;
        }
        await next(context);
    }
}
