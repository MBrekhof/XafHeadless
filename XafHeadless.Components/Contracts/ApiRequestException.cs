using System.Net;

namespace XafHeadless.Components.Contracts;

// Thrown by ApiClient for a failure the caller cannot degrade past. Carries everything a failure needs
// to explain itself: method, absolute URL (query string INCLUDED -- for a bad $filter that IS the
// evidence), status, and a bounded excerpt of the response body, which for OData holds the real reason.
// Replaces EnsureSuccessStatusCode(), whose "Response status code does not indicate success: 400 (Bad
// Request)" named neither the request nor the cause and cost a live debugging session.
// Design: docs/superpowers/specs/2026-08-08-runtime-diagnostics-design.md.
public sealed class ApiRequestException(HttpMethod method, string url, HttpStatusCode status,
        string? reasonPhrase, string? body)
    : Exception(Describe(method, url, status, reasonPhrase, body)) {

    // An OData error fits in a few hundred bytes; an HTML error page can run to megabytes. Cap so a
    // failure can be logged in full without flooding the log.
    public const int MaxBodyLength = 2048;
    const string TruncationMarker = "… (truncated)";

    public HttpMethod Method { get; } = method;
    public string Url { get; } = url;
    public HttpStatusCode StatusCode { get; } = status;
    public string? ReasonPhrase { get; } = reasonPhrase;
    public string? Body { get; } = body;

    // Total length never exceeds MaxBodyLength, marker included.
    public static string? Excerpt(string? body) =>
        body is null || body.Length <= MaxBodyLength
            ? body
            : body[..(MaxBodyLength - TruncationMarker.Length)] + TruncationMarker;

    static string Describe(HttpMethod method, string url, HttpStatusCode status, string? reason, string? body) =>
        $"{method} {url} -> {(int)status} {reason}"
            + (string.IsNullOrWhiteSpace(body) ? "" : $": {body.Trim()}");
}
