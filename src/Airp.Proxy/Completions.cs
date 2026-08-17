using System.Text;
using System.Text.Json.Nodes;

namespace Airp.Proxy;

/// <summary>Names of things the proxy reads from configuration.</summary>
public static class ProxyOptions
{
    /// <summary>
    /// Name of the secret holding the bearer token this endpoint requires.
    /// </summary>
    /// <remarks>
    /// A separate token from the model provider's key, on purpose. This one is typed into a
    /// third party's settings page and travels to whoever they route through; the provider key
    /// bills a card. They should never be the same string.
    /// </remarks>
    public const string TokenSecretName = "AIRP_PROXY_TOKEN";
}

/// <summary>Builds responses in the shape an OpenAI-compatible client expects.</summary>
internal static class Completions
{
    /// <summary>Builds a whole, non-streamed completion.</summary>
    /// <param name="text">The reply.</param>
    /// <param name="model">Model name to report.</param>
    /// <returns>The response body.</returns>
    public static JsonObject Whole(string text, string model) => new()
    {
        ["id"] = "chatcmpl-airp-" + Guid.NewGuid().ToString("N")[..12],
        ["object"] = "chat.completion",
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["model"] = model,
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
            ["finish_reason"] = "stop",
        }),
    };

    /// <summary>Builds one chunk of a streamed completion.</summary>
    /// <param name="id">Identifier shared by every chunk of one reply.</param>
    /// <param name="model">Model name to report.</param>
    /// <param name="role">Set on the first chunk only.</param>
    /// <param name="content">A piece of the reply.</param>
    /// <param name="finish">Set on the last chunk only.</param>
    /// <returns>The chunk.</returns>
    public static string Chunk(
        string id,
        string model,
        string? role = null,
        string? content = null,
        string? finish = null)
    {
        var delta = new JsonObject();

        if (role is not null)
        {
            delta["role"] = role;
        }

        if (content is not null)
        {
            delta["content"] = content;
        }

        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finish is null ? null : JsonValue.Create(finish),
            }),
        }.ToJsonString();
    }
}

/// <summary>A reply delivered as server-sent events.</summary>
/// <remarks>
/// <para>
/// The reply is already complete before the first byte goes out, because the provider hands
/// back a finished message rather than a stream. Chunking it here is not theatre: a front end
/// that asked for <c>stream: true</c> and receives a single JSON body may not render it, and
/// several of them time out waiting for a first token that a whole-body response never sends.
/// </para>
/// <para>
/// True streaming would mean the provider surfacing tokens as they arrive, which changes the
/// storage contract — a partially-arrived reply is not a turn. That belongs with a decision
/// about partial writes, not here.
/// </para>
/// </remarks>
/// <param name="Text">The finished reply.</param>
/// <param name="Model">Model name to report.</param>
internal sealed record SseResult(string Text, string Model) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = httpContext.Response;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var id = "chatcmpl-airp-" + Guid.NewGuid().ToString("N")[..12];
        var token = httpContext.RequestAborted;

        await Write(response, Completions.Chunk(id, Model, role: "assistant"), token).ConfigureAwait(false);

        foreach (var piece in Split(Text, 48))
        {
            await Write(response, Completions.Chunk(id, Model, content: piece), token).ConfigureAwait(false);
            await response.Body.FlushAsync(token).ConfigureAwait(false);
        }

        await Write(response, Completions.Chunk(id, Model, finish: "stop"), token).ConfigureAwait(false);
        await response.WriteAsync("data: [DONE]\n\n", token).ConfigureAwait(false);
        await response.Body.FlushAsync(token).ConfigureAwait(false);
    }

    private static Task Write(HttpResponse response, string payload, CancellationToken token)
        => response.WriteAsync($"data: {payload}\n\n", token);

    /// <summary>Splits the reply into chunks without cutting a character in half.</summary>
    /// <remarks>
    /// Walks by text element rather than by <c>char</c>: an emoji or an accented letter can be
    /// two UTF-16 units, and splitting between them puts a replacement character on the screen.
    /// </remarks>
    private static IEnumerable<string> Split(string text, int size)
    {
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var builder = new StringBuilder();

        while (enumerator.MoveNext())
        {
            builder.Append(enumerator.GetTextElement());

            if (builder.Length >= size)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }
}

/// <summary>Lets the endpoint return an SSE reply as an <see cref="IResult"/>.</summary>
internal static class ResultExtensions
{
    /// <summary>Streams a finished reply as server-sent events.</summary>
    /// <param name="_">The extensions anchor.</param>
    /// <param name="text">The reply.</param>
    /// <param name="model">Model name to report.</param>
    /// <returns>The result.</returns>
    public static IResult Sse(this IResultExtensions _, string text, string model)
        => new SseResult(text, model);
}
