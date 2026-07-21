using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using XafHeadless.Components.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// The wire rule (WASM side): the SAME HTTP client stack the server side registers (see
// XafHeadless.Web/Program.cs). Both point ApiClient at http://localhost:5200 -- when Auto hands the
// app to WebAssembly, the components keep calling the API over HTTP with zero engine access.
builder.Services.AddXafHeadlessClient();

await builder.Build().RunAsync();
