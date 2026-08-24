using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// Talks to an OpenAI-compatible chat completions API. Defaults to OpenRouter.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is OpenRouter-specific beyond two optional attribution headers, which is the
/// point: the same class reaches DeepSeek, a local Ollama, or anything else speaking the same
/// shape, by changing <see cref="ModelOptions.BaseUrl"/>. Swapping provider is configuration.
/// </para>
/// <para>
/// Replies are requested whole rather than streamed. The terminal's own contract hands back a
/// finished message, so streaming would buy nothing a caller could show — and it would buy a
/// partial-reply failure mode that the storage layer would then have to reason about.
/// </para>
/// </remarks>
public sealed class OpenRouterClient : ILanguageModelClient
{
    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly IOptionsMonitor<AirpOptions> _options;
    private readonly ILogger<OpenRouterClient> _logger;

    /// <summary>Initialises the client.</summary>
    /// <param name="http">HTTP client used for the calls.</param>
    /// <param name="secrets">Where the API key is read from.</param>
    /// <param name="options">Application options, for the model configuration.</param>
    /// <param name="logger">Logger. Never receives the key or a message body.</param>
    public OpenRouterClient(
        HttpClient http,
        ISecretStore secrets,
        IOptionsMonitor<AirpOptions> options,
        ILogger<OpenRouterClient> logger)
    {
        _http = http;
        _secrets = secrets;
        _options = options;
        _logger = logger;
    }

    private ModelOptions Model => _options.CurrentValue.Model;

    /// <inheritdoc />
    public async Task<ModelReply> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        string? model = null,
        double? temperature = null,
        int? maxTokens = null,
        double? frequencyPenalty = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("A completion needs at least one message.", nameof(messages));
        }

        var settings = Model;
        var chosen = string.IsNullOrWhiteSpace(model) ? settings.Name : model;

        var payload = new JsonObject
        {
            ["model"] = chosen,
            ["stream"] = false,
            ["temperature"] = temperature ?? settings.Temperature,
            ["max_tokens"] = maxTokens ?? settings.MaxTokens,
            ["messages"] = new JsonArray([.. messages.Select(static m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role switch
                {
                    ModelRole.System => "system",
                    ModelRole.Assistant => "assistant",
                    _ => "user",
                },
                ["content"] = m.Content,
            })]),
        };

        // Only when a dial asks for one. Omitting the field and sending zero are different
        // requests to some backends, and only omission means "no opinion".
        if (frequencyPenalty is { } penalty)
        {
            payload["frequency_penalty"] = penalty;
        }

        if (Routing(settings) is { } routing)
        {
            payload["provider"] = routing;
        }

        var body = await SendAsync("chat/completions", payload, settings, cancellationToken).ConfigureAwait(false);
        var choice = body?["choices"]?.AsArray().FirstOrDefault();

        var text = choice?["message"]?["content"]?.GetValue<string>();
        if (text is null)
        {
            // A 200 with no content is not a reply. Saying so beats handing back an empty
            // string that would be stored as though the model had answered with silence.
            //
            // Named in as much detail as the response allows, because this failure is common
            // enough to have cost a real story its fact extraction five times running and the
            // message alone could not distinguish a host that produced nothing from one that
            // refused. 'finish_reason' separates them: content_filter is a refusal, length is
            // a ceiling hit before the first token, stop is a host with nothing to say. The
            // reasoning field is checked too — some models put everything there and leave
            // content null, which looks identical from here and is not the same problem.
            var why = choice?["finish_reason"]?.GetValue<string>();
            var host = body?["provider"]?.GetValue<string>();
            var reasoned = choice?["message"]?["reasoning"] is not null;

            _logger.LogWarning(
                "No message content from {Host}: finish_reason {Reason}, reasoning present: {Reasoned}.",
                host ?? "an unnamed provider",
                why ?? "(absent)",
                reasoned);

            throw new ModelUnavailableException(
                $"The API returned a response with no message content (finish_reason: {why ?? "absent"}"
                + (host is { Length: > 0 } ? $", served by {host}" : string.Empty)
                + (reasoned ? ", reasoning only" : string.Empty)
                + ").",
                (int)HttpStatusCode.OK);
        }

        var usage = body?["usage"];

        // Nothing is asked for to get this. The router returns full usage, cost included, on
        // every response; the request flag that used to enable it is deprecated and ignored.
        var prompt = usage?["prompt_tokens_details"];

        return new ModelReply
        {
            Text = text,
            Model = body?["model"]?.GetValue<string>(),
            Provider = body?["provider"]?.GetValue<string>(),
            GenerationId = body?["id"]?.GetValue<string>(),
            PromptTokens = usage?["prompt_tokens"]?.GetValue<int>(),
            CompletionTokens = usage?["completion_tokens"]?.GetValue<int>(),
            Cost = usage?["cost"]?.GetValue<double>(),
            CachedTokens = prompt?["cached_tokens"]?.GetValue<int>(),
            CacheWriteTokens = prompt?["cache_write_tokens"]?.GetValue<int>(),
            FinishReason = choice?["finish_reason"]?.GetValue<string>(),
        };
    }

    /// <summary>
    /// The router's own routing preferences, or null when none are configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Omitted entirely rather than sent empty, because this is the one field in the request
    /// that is not OpenAI's. Anything else speaking the same shape would either ignore an
    /// unknown key or refuse the call, and there is no reason to find out which on every turn
    /// when nobody has asked for routing.
    /// </para>
    /// <para>
    /// The values go out exactly as configured. They are slugs, and a slug that matches no host
    /// is dropped silently by the router — so mangling the case here to be helpful would only
    /// make a wrong one harder to spot. What actually served the reply is recorded per message
    /// and shown by <c>airp audit</c>.
    /// </para>
    /// </remarks>
    /// <param name="settings">The configured model options.</param>
    /// <returns>The <c>provider</c> object, or null.</returns>
    private static JsonObject? Routing(ModelOptions settings)
    {
        var ignore = settings.IgnoreProviders?.Where(static p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? [];
        var order = settings.PreferProviders?.Where(static p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? [];

        if (ignore.Length == 0 && order.Length == 0 && settings.AllowProviderFallbacks is null)
        {
            return null;
        }

        var routing = new JsonObject();

        if (order.Length > 0)
        {
            routing["order"] = new JsonArray([.. order.Select(static p => (JsonNode)p.Trim())]);
        }

        if (ignore.Length > 0)
        {
            routing["ignore"] = new JsonArray([.. ignore.Select(static p => (JsonNode)p.Trim())]);
        }

        if (settings.AllowProviderFallbacks is { } fallbacks)
        {
            routing["allow_fallbacks"] = fallbacks;
        }

        return routing;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var settings = Model;
        var body = await SendAsync("models", payload: null, settings, cancellationToken).ConfigureAwait(false);

        return body?["data"]?.AsArray()
            .Select(static m => m?["id"]?.GetValue<string>())
            .OfType<string>()
            .ToArray() ?? [];
    }

    /// <summary>
    /// Issues one call and returns the parsed body, converting every failure into a
    /// <see cref="ModelUnavailableException"/> that carries a usable hint.
    /// </summary>
    /// <param name="route">Route relative to the configured base address.</param>
    /// <param name="payload">Request body, or <see langword="null"/> to issue a GET.</param>
    /// <param name="settings">Model settings in force for this call.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>The parsed response body.</returns>
    private async Task<JsonNode?> SendAsync(
        string route,
        JsonObject? payload,
        ModelOptions settings,
        CancellationToken cancellationToken)
    {
        var key = await _secrets.GetAsync(settings.ApiKeyName, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ModelUnavailableException(
                $"No API key is configured under '{settings.ApiKeyName}'.",
                (int)HttpStatusCode.Unauthorized);
        }

        // A per-call timeout rather than one on the shared HttpClient: the ceiling belongs to
        // the request being made, and a caller cancelling early must not be reported as one.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using var request = new HttpRequestMessage(
            payload is null ? HttpMethod.Get : HttpMethod.Post,
            $"{settings.BaseUrl.TrimEnd('/')}/{route}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // OpenRouter reads these for attribution on its dashboards. Harmless everywhere else.
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/argamboad/custom-airp");
        request.Headers.TryAddWithoutValidation("X-Title", "Airp");

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelUnavailableException(
                $"The model did not answer within {settings.TimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new ModelUnavailableException($"Could not reach {settings.BaseUrl}: {ex.Message}", null, ex);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ModelUnavailableException(
                    $"The API returned {(int)response.StatusCode} {response.ReasonPhrase}. {Explain(content)}".TrimEnd(),
                    (int)response.StatusCode);
            }

            _logger.LogInformation(
                "Model call to {Route} returned {Status}.", route, (int)response.StatusCode);

            try
            {
                return JsonNode.Parse(content);
            }
            catch (JsonException ex)
            {
                throw new ModelUnavailableException("The API returned a body that is not JSON.", null, ex);
            }
        }
    }

    /// <summary>Pulls the human-readable part out of an error body, when there is one.</summary>
    /// <remarks>
    /// Returns the message alone rather than the whole payload: an error body can echo the
    /// request back, and the request contains the conversation.
    /// </remarks>
    /// <param name="content">The raw error body.</param>
    /// <returns>A short explanation, or an empty string.</returns>
    private static string Explain(string content)
    {
        try
        {
            return JsonNode.Parse(content)?["error"]?["message"]?.GetValue<string>() ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
