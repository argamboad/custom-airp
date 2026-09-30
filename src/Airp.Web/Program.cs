using Airp.Application.Abstractions;
using Airp.Application.Services;
using Airp.Domain;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Web;

// ─────────────────────────────────────────────────────────────────────────────────────────
//  Airp.Web — the stories in a phone's browser, over the owner's tailnet.
//
//  Another front end over the same store, like the terminal: it translates, and everything it
//  does goes through the provider the terminal uses. It listens on loopback only and lets one
//  tailnet account in, by the identity tailscale serve passes along (see Gate).
// ─────────────────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile(AppPaths.ConfigurationFile, optional: true, reloadOnChange: true)
    .AddInMemoryCollection(EnvironmentOverrides.Read());

builder.Services.AddAirpInfrastructure(builder.Configuration);

// The one application service the pages use, for a transcript download. Not the whole
// application layer: that brings a background synchroniser this process has no use for.
builder.Services.TryAddSingleton<IExportService, ExportService>();
builder.Services.AddRazorPages();

var login = builder.Configuration["Airp:Web:Login"];
var app = builder.Build();
var log = app.Logger;

if (string.IsNullOrWhiteSpace(login))
{
    log.LogCritical(
        "No tailnet login is allowed in. Set Airp:Web:Login to yours — the one tailscale serve "
        + "reports as Tailscale-User-Login — and start again.");
    return 78;
}

if (!Gate.OnlyLoopback(
        builder.Configuration["urls"],
        builder.Configuration["http_ports"],
        builder.Configuration["https_ports"]))
{
    log.LogCritical(
        "This listens on loopback only, behind tailscale serve: the login it trusts comes from a "
        + "header, and a header from anywhere else could be forged. Use --urls http://127.0.0.1:<port>.");
    return 78;
}

if (app.Services.GetService<LocalConversationProvider>() is null)
{
    log.LogCritical("The web pages read the local store; set Airp:Provider to 'local'.");
    return 78;
}

app.Use(async (context, next) =>
{
    if (!Gate.Admits(context.Request.Headers[Gate.LoginHeader], login))
    {
        log.LogWarning("Turned away a request without the allowed tailnet login.");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Not available.");
        return;
    }

    // The pages hold story text. Nothing from elsewhere is loaded into them, nothing of them is
    // kept by the browser or a cache, and nothing about them is sent to wherever a link leads.
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy = "default-src 'self'; form-action 'self'; frame-ancestors 'none'";
    headers.CacheControl = "no-store";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Robots-Tag"] = "noindex, nofollow";
    headers.XContentTypeOptions = "nosniff";

    await next();
});

app.UseStaticFiles();
app.MapRazorPages();

log.LogInformation("Listening. Reach it through tailscale serve.");
app.Run();
return 0;
