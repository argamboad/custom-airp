using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain;

namespace Airp.Infrastructure.Providers;

/// <summary>Embeds text through an OpenAI-compatible <c>/embeddings</c> endpoint.</summary>
/// <remarks>
/// <para>
/// It uses the chat client's base address and key by default, because for OpenRouter they are
/// one service. They can be split with <c>Airp:Model:EmbeddingBaseUrl</c> and
/// <c>EmbeddingApiKeyName</c>, which is what makes it possible to run the replies against a
/// provider's own API — cheaper, and it caches prefixes — while embeddings stay somewhere that
/// offers them. DeepSeek exposes no embeddings endpoint at all, and without the split, pointing
/// the client at it would take retrieval away without saying so.
/// </para>
/// </remarks>
public sealed class OpenRouterEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly IOptionsMonitor<AirpOptions> _options;
    private readonly ILogger<OpenRouterEmbeddingClient> _logger;

    /// <summary>Initialises the client.</summary>
    /// <param name="http">HTTP client used for the calls.</param>
    /// <param name="secrets">Where the API key is read from.</param>
    /// <param name="options">Application options.</param>
    /// <param name="logger">Logger. Never receives the key or the text.</param>
    public OpenRouterEmbeddingClient(
        HttpClient http,
        ISecretStore secrets,
        IOptionsMonitor<AirpOptions> options,
        ILogger<OpenRouterEmbeddingClient> logger)
    {
        _http = http;
        _secrets = secrets;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        var settings = _options.CurrentValue.Model;
        var endpoint = settings.ResolvedEmbeddingBaseUrl;
        var keyName = settings.ResolvedEmbeddingApiKeyName;

        var key = await _secrets.GetAsync(keyName, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ModelUnavailableException(
                $"No API key is configured under '{keyName}'.", 401);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/embeddings");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new JsonObject
        {
            ["model"] = settings.EmbeddingModel,
            ["input"] = new JsonArray([.. texts.Select(static t => (JsonNode)t)]),
        });

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelUnavailableException($"Could not reach {endpoint}: {ex.Message}", null, ex);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ModelUnavailableException(
                    $"The embeddings API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    (int)response.StatusCode);
            }

            var data = JsonNode.Parse(content)?["data"]?.AsArray()
                ?? throw new ModelUnavailableException("The embeddings API returned no data.");

            // Ordered by the index the API reports rather than by position in the array: the
            // contract is one vector per input in the same order, and the index is how the API
            // states that. Trusting array order would be trusting something never promised.
            var vectors = data
                .OrderBy(item => item?["index"]?.GetValue<int>() ?? 0)
                .Select(item => item?["embedding"]?.AsArray()
                    .Select(v => (float)(v?.GetValue<double>() ?? 0))
                    .ToArray() ?? [])
                .ToArray();

            _logger.LogInformation(
                "Embedded {Count} text(s) into {Dimensions} dimensions.",
                vectors.Length,
                vectors.Length > 0 ? vectors[0].Length : 0);

            return vectors;
        }
    }
}
