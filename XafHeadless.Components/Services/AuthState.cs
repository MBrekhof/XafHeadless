namespace XafHeadless.Components.Services;

// Registered SCOPED (deliberately -- Singleton would leak tokens across circuits/users when hosted
// server-side in a Blazor Web App; see HOW-TO-IMPLEMENT.md gotcha 18). Holds the JWT for the
// session; ApiClient reads Token per request and calls SetToken(null) on any 401, which raises
// Changed so MainLayout.razor can redirect to /login.
//
// GAP-007: the token is persisted to sessionStorage by PersistAuth.razor (via IJSRuntime) and
// restored on the first INTERACTIVE render, so a hard reload / the InteractiveAuto WASM takeover
// keeps the session. RestoreAttempted is the "have we tried to restore yet?" latch that the
// redirect-to-/login decisions gate on -- it stays false through prerender and the pre-restore
// interactive window, so a persisted session is never bounced to /login before its token is back.
public class AuthState {
    public string? Token { get; private set; }
    public bool RestoreAttempted { get; private set; }
    public event Action? Changed;

    public void SetToken(string? token) {
        Token = token;
        Changed?.Invoke();
    }

    // Latches once the first-interactive restore has run (whether or not a stored token was found),
    // then raises Changed so the deferred /login-redirect decision re-evaluates. Idempotent.
    public void MarkRestored() {
        RestoreAttempted = true;
        Changed?.Invoke();
    }
}
