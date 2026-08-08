using DevExpress.Blazor;
using Microsoft.Extensions.DependencyInjection;
using XafHeadless.Components.Components.Editors;

namespace XafHeadless.Components.Services;

// THE wire rule, in one place. The RCL's components call the API over HTTP through ApiClient on BOTH
// render sides; this method registers that identical client stack in XafHeadless.Web (server DI) and
// XafHeadless.Web.Client (WASM DI). Centralized deliberately so the two hosts cannot drift to a
// different base address -- and so neither host is tempted to swap ApiClient for a DI shortcut to the
// XAF engine (IObjectSpaceFactory/SecurityStrategy/...), which would be a spec violation. Neither host
// references DevExpress.ExpressApp or the demo module; the only DevExpress dependency here is the
// DevExpress.Blazor UI package (AddDevExpressBlazor), which is engine-free.
public static class ClientServiceRegistration {
    // detailedErrors: pass the host's own IsDevelopment(). It decides whether a failure's detail may
    // appear ON SCREEN (see DiagnosticsOptions); the logs get it either way. Defaults to false so a host
    // that forgets to pass it errs toward the generic message.
    public static IServiceCollection AddXafHeadlessClient(this IServiceCollection services,
            bool detailedErrors = false) {
        services.AddDevExpressBlazor();
        services.AddSingleton(new DiagnosticsOptions(detailedErrors));
        // Scoped, not Singleton: under a Blazor Web App the server side is per-circuit and the WASM
        // side is per-runtime, so each render context gets its own HttpClient + AuthState. (The old
        // WASM-standalone client used Singleton; that would leak one user's token across circuits on
        // the server -- Scoped is the correct multi-render-mode shape.)
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost:5200/") });
        services.AddScoped<AuthState>();
        services.AddScoped<ApiClient>();
        services.AddSingleton(EditorMap.Default);   // immutable editor-hint map, safe to share
        return services;
    }
}
