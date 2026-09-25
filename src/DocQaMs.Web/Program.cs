using DocQaMs.Claude;
using DocQaMs.Core;
using DocQaMs.Web;
using DocQaMs.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddDocQaCore(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddDocQaMsClaude();

await using var app = builder.Build();

// Loopback binding is the only access control until authentication is added, so refuse any other
// address in configuration before the server opens a socket.
IReadOnlyList<string> configured = LoopbackGuard.NonLoopbackConfigured(app.Configuration);
if (configured.Count > 0)
{
    app.Logger.LogCritical(
        "DocQaMs has no authentication and must listen on loopback only, but its configuration asks for " +
        "{Addresses}. Not starting. Use http://localhost:<port> instead.",
        string.Join(", ", configured));
    return 1;
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.StartAsync();

// Backstop for any endpoint the configuration check cannot see: stop at once if the server bound
// anything other than loopback.
IReadOnlyList<string> exposed = LoopbackGuard.NonLoopback(app.Urls);
if (exposed.Count > 0)
{
    app.Logger.LogCritical(
        "DocQaMs has no authentication and must listen on loopback only, but it bound {Addresses}. " +
        "Stopping. Use http://localhost:<port> instead.",
        string.Join(", ", exposed));
    await app.StopAsync();
    return 1;
}

await app.WaitForShutdownAsync();
return 0;
