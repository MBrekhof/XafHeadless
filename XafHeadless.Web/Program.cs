using XafHeadless.Web.Components;
using XafHeadless.Components.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// The wire rule (server side): the SAME HTTP client stack the WASM side uses. This host talks to
// the XAF engine ONLY over HTTP (http://localhost:5200) via ApiClient -- it has no reference to
// DevExpress.ExpressApp or the demo module, and no DI shortcut to engine services. AddDevExpressBlazor
// is included here (the Blazor UI package is engine-free).
builder.Services.AddXafHeadlessClient();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    // Routable pages (list/detail/login/home) + Routes live in the RCL, shared by both render sides.
    .AddAdditionalAssemblies(typeof(ApiClient).Assembly);

app.Run();
