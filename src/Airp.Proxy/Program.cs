using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Proxy;

// ─────────────────────────────────────────────────────────────────────────────────────────
//  Airp.Proxy — an OpenAI-compatible endpoint that a third-party front end can be pointed at.
//
//  The front end sends whatever truncated history it keeps. That history is read only to work
//  out which of our conversations this is, and then discarded: the prompt that reaches the
//  model is built from our own store, by the same context builder the terminal uses.
//
//  This never calls the front end. It is called, because the reader configured a URL.
// ─────────────────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile(AppPaths.ConfigurationFile, optional: true, reloadOnChange: true)
    .AddInMemoryCollection(EnvironmentOverrides.Read());

builder.Services.AddAirpInfrastructure(builder.Configuration);

var app = builder.Build();
var log = app.Logger;
var options = app.Services.GetRequiredService<IOptionsMonitor<AirpOptions>>();
var secrets = app.Services.GetRequiredService<ISecretStore>();

if (app.Services.GetService<LocalConversationProvider>() is not { } conversations)
{
    log.LogCritical(
        "The proxy serves the local store; set Airp:Provider to 'local'. Nothing else can answer.");
    return 78;
}

// ── Authentication ───────────────────────────────────────────────────────────────────────
//
//  A bearer token, always, including in development. The thing behind this endpoint is a
//  database of someone's private conversations, and it is reachable from wherever the tunnel
//  reaches. There is no configuration in which leaving it open is acceptable.

var expected = await secrets.GetAsync(ProxyOptions.TokenSecretName).ConfigureAwait(false);

if (string.IsNullOrWhiteSpace(expected))
{
    log.LogCritical(
        "No bearer token is configured. Run 'airp secret set {Name}' and start again.",
        ProxyOptions.TokenSecretName);

    return 78;
}

app.Use(async (context, next) =>
{
    var offered = context.Request.Headers.Authorization.ToString();
    const string scheme = "Bearer ";

    var supplied = offered.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
        ? offered[scheme.Length..].Trim()
        : offered.Trim();

    // Fixed-time comparison: a token is a secret, and an endpoint that answers faster for a
    // near-miss than for a wrong first character will eventually give one up.
    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied),
            System.Text.Encoding.UTF8.GetBytes(expected)))
    {
        log.LogWarning("Rejected a request from {Address} with no usable token.",
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("""{"error":{"message":"Unauthorized","code":401}}""");
        return;
    }

    await next();
});

// ── The endpoint ─────────────────────────────────────────────────────────────────────────

app.MapGet("/v1/models", () => Results.Json(new
{
    @object = "list",
    data = new[]
    {
        new
        {
            id = options.CurrentValue.Model.Name,
            @object = "model",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            owned_by = "airp",
        },
    },
}));

app.MapPost("/v1/chat/completions", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

    JsonNode? body;

    try
    {
        body = JsonNode.Parse(raw);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = new { message = "The body is not JSON." } });
    }

    var incoming = body?["messages"]?.AsArray() ?? [];

    var everything = string.Join(
        "\n",
        incoming.Select(m => m?["content"]?.ToString() ?? string.Empty));

    var userTurns = incoming
        .Where(m => m?["role"]?.GetValue<string>() == "user")
        .Select(m => m?["content"]?.ToString() ?? string.Empty)
        .Where(static t => !string.IsNullOrWhiteSpace(t))
        .ToArray();

    if (userTurns.Length == 0)
    {
        return Results.BadRequest(new { error = new { message = "No user message to answer." } });
    }

    var chats = await conversations.ListAsync(cancellationToken).ConfigureAwait(false);
    var openings = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (var chat in chats)
    {
        var stored = await conversations.GetMessagesAsync(chat.Id, cancellationToken).ConfigureAwait(false);
        var first = stored.FirstOrDefault(static m => m.Role == Airp.Domain.Conversations.ChatRole.User);

        if (first is not null)
        {
            openings[chat.Id] = first.Text;
        }
    }

    var resolved = SessionResolver.Resolve(everything, userTurns[0], chats, openings);

    if (resolved.ConversationId is null)
    {
        // Writing a turn into the wrong conversation is permanent here, so an unidentified
        // request is refused rather than guessed at. The message says how to fix it, because
        // the reader is looking at a chat window and not at this log.
        log.LogWarning("Could not identify a conversation. Ambiguous: {Ambiguous}.", resolved.Ambiguous);

        return Results.Json(
            new
            {
                error = new
                {
                    message = resolved.Ambiguous
                        ? "More than one stored conversation fits this chat. Add [[rp:<id>]] to the "
                          + "custom prompt to say which one. 'airp audit' lists the ids."
                        : "No stored conversation matches this chat. Add [[rp:<id>]] to the custom "
                          + "prompt, or start one with 'airp new'.",
                    code = 404,
                },
            },
            statusCode: StatusCodes.Status404NotFound);
    }

    log.LogInformation(
        "Request resolved to {Conversation} by {How}.", resolved.ConversationId, resolved.How);

    // The newest user turn is the only thing taken from the request. Everything else the front
    // end sent — its truncated history, its own framing — is what this exists to replace.
    var said = userTurns[^1];

    try
    {
        var added = await conversations
            .SendAsync(resolved.ConversationId, said, progress: null, cancellationToken)
            .ConfigureAwait(false);

        var replyText = added.LastOrDefault()?.Text ?? string.Empty;
        var wantsStream = body?["stream"]?.GetValue<bool>() ?? false;

        return wantsStream
            ? Results.Extensions.Sse(replyText, options.CurrentValue.Model.Name)
            : Results.Json(Completions.Whole(replyText, options.CurrentValue.Model.Name));
    }
    catch (ReplyMissingException ex)
    {
        // The turn is stored and unanswered. Saying so beats a generic failure the reader
        // would respond to by sending the same message again.
        log.LogWarning(ex, "The message was kept but no reply was produced.");

        return Results.Json(
            new { error = new { message = ex.Message + " It is saved; do not send it again.", code = 502 } },
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (AirpException ex)
    {
        log.LogError(ex, "The request failed.");

        return Results.Json(
            new { error = new { message = ex.Message, code = 500 } },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapFallback((HttpContext context) =>
{
    log.LogInformation("Unhandled route {Method} {Path}.", context.Request.Method, context.Request.Path);
    return Results.NotFound(new { error = new { message = "No such route." } });
});

log.LogInformation("Listening. Point the front end's proxy URL at /v1/chat/completions.");
app.Run();
return 0;
