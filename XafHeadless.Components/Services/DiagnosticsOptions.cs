namespace XafHeadless.Components.Services;

// Whether the UI may show failure DETAIL (the failing request, the exception message) on screen.
// True only in Development; outside it the ErrorBoundary shows a generic message and the detail goes to
// the log alone -- an OData error body can name internal types and members. Registered by
// AddXafHeadlessClient so both render sides (server DI and WASM DI) get the value from their OWN
// environment, which is the only place that knows it.
// Design: docs/superpowers/specs/2026-08-08-runtime-diagnostics-design.md.
public sealed record DiagnosticsOptions(bool DetailedErrors);
