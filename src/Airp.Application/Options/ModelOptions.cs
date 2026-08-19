using System.ComponentModel.DataAnnotations;

namespace Airp.Application.Options;

/// <summary>Which language model writes the replies, and how it is reached.</summary>
/// <remarks>
/// The defaults point at OpenRouter because it fronts many models behind one key and one
/// OpenAI-compatible endpoint, which is what turns "try a different model" into a
/// configuration change instead of a code change. Pointing <see cref="BaseUrl"/> at a
/// provider's own API works identically — the wire format is the same.
/// </remarks>
public sealed class ModelOptions
{
    /// <summary>Base address of an OpenAI-compatible API, without a trailing slash.</summary>
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Hosts never to send this conversation to, by provider slug.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A router fans one model across many machines, and they are not interchangeable. Measured
    /// in a single session: one host returned four consecutive replies of token soup beginning
    /// with the model's own start-of-sequence marker — its serving stack applying the chat
    /// template wrongly — while two others answered the same prompt with eight hundred coherent
    /// tokens. A denied host costs nothing and is the difference between playing and rerolling.
    /// </para>
    /// <para>
    /// <strong>Slugs, not the names replies come back with.</strong> The response says
    /// <c>DeepInfra</c>; the request wants <c>deepinfra</c>. A slug that matches nothing is
    /// ignored without complaint, so the check is to play a turn and look at what
    /// <c>airp audit</c> says served it.
    /// </para>
    /// </remarks>
    public IList<string> IgnoreProviders { get; set; } = [];

    /// <summary>
    /// Hosts to try first, in order, by provider slug. Others still follow unless fallbacks
    /// are switched off.
    /// </summary>
    /// <remarks>
    /// Worth setting once you know which hosts cache: the same conversation measured 61% of its
    /// prompt served from cache on one and nothing at all on four others, which on a 60,000
    /// token prompt is most of what a turn costs.
    /// </remarks>
    public IList<string> PreferProviders { get; set; } = [];

    /// <summary>
    /// Whether the router may fall back to hosts outside <see cref="PreferProviders"/>.
    /// </summary>
    /// <remarks>
    /// Null leaves the decision to the router, which allows them. Setting it false makes the
    /// preference a restriction — fewer surprises, and a failed call rather than a reply from
    /// somewhere unvetted.
    /// </remarks>
    public bool? AllowProviderFallbacks { get; set; }

    /// <summary>Model identifier passed to the API.</summary>
    /// <remarks>
    /// DeepSeek V4 Flash by default: it carries over half the roleplay traffic on OpenRouter
    /// between its variants, takes a 1M-token context, and is permissive enough for the
    /// content this client exists to keep.
    /// </remarks>
    public string Name { get; set; } = "deepseek/deepseek-v4-flash";

    /// <summary>
    /// Name of the secret holding the API key, looked up through <c>ISecretStore</c>.
    /// </summary>
    /// <remarks>
    /// A name rather than the key itself, and deliberately so: the key must never be
    /// reachable from configuration, because anything that dumps configuration would then
    /// print it.
    /// </remarks>
    public string ApiKeyName { get; set; } = "OPENROUTER_API_KEY";

    /// <summary>
    /// Model used for background work — summarising, and later extracting facts — or null to
    /// use the same one that writes the replies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null by default, and deliberately. The obvious reason to split this off is cost, and at
    /// this volume there is none: a summary runs about $0.0005, and the reply model is already
    /// among the cheapest that serve it. What a separate model actually buys is speed on the
    /// turn where summarising happens, which is worth having but is not worth guessing at.
    /// </para>
    /// <para>
    /// One constraint is easy to miss: this model reads the transcript, so for a client that
    /// holds adult roleplay it has to be as permissive as the one writing it. Most of the cheap
    /// well-behaved models would simply refuse, and a summariser that refuses is a character
    /// that forgets.
    /// </para>
    /// </remarks>
    public string? BackgroundModel { get; set; }

    /// <summary>Model used to turn text into vectors for retrieval.</summary>
    /// <remarks>
    /// Reached through the same base address and key as the chat model. At roughly $0.02 per
    /// million tokens, embedding an entire corpus of this size costs under a cent, so nothing
    /// here is worth optimising for price.
    /// </remarks>
    public string EmbeddingModel { get; set; } = "openai/text-embedding-3-small";

    /// <summary>
    /// Base address for embeddings, or null to use the same API the replies come from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separable because the two are not always the same service. DeepSeek's own API is
    /// cheaper than reaching it through a router and caches prefixes, which is exactly what
    /// this prompt's layer order is built for — but it exposes no embeddings endpoint at all,
    /// so pointing the whole client at it would silently cost the memory its retrieval.
    /// </para>
    /// <para>
    /// Falls back rather than defaulting to a second URL: one service answering both is the
    /// common case, and a setting that has to be filled in to keep working is a setting that
    /// breaks an existing install on upgrade.
    /// </para>
    /// </remarks>
    public string? EmbeddingBaseUrl { get; set; }

    /// <summary>
    /// Name of the secret holding the embeddings key, or null to use the replies' key.
    /// </summary>
    /// <remarks>
    /// A second endpoint usually means a second account. Still a name and never the key
    /// itself, for the same reason as <see cref="ApiKeyName"/>.
    /// </remarks>
    public string? EmbeddingApiKeyName { get; set; }

    /// <summary>Where embeddings are actually fetched from, once the fallback is applied.</summary>
    public string ResolvedEmbeddingBaseUrl =>
        string.IsNullOrWhiteSpace(EmbeddingBaseUrl) ? BaseUrl : EmbeddingBaseUrl;

    /// <summary>Which secret the embeddings call actually uses, once the fallback is applied.</summary>
    public string ResolvedEmbeddingApiKeyName =>
        string.IsNullOrWhiteSpace(EmbeddingApiKeyName) ? ApiKeyName : EmbeddingApiKeyName;

    /// <summary>
    /// How many past turns retrieval may bring back into a prompt.
    /// </summary>
    /// <remarks>
    /// Small on purpose. Retrieval earns its place by finding the one exchange that matters,
    /// and a dozen near-misses crowd out the recent turns while reading as noise to the model.
    /// </remarks>
    [Range(0, 20)]
    public int RecallCount { get; set; } = 4;

    /// <summary>Similarity below which a recalled turn is not worth sending.</summary>
    [Range(0d, 1d)]
    public double RecallThreshold { get; set; } = 0.35;

    /// <summary>Sampling temperature. Higher wanders further from the obvious reply.</summary>
    [Range(0d, 2d)]
    public double Temperature { get; set; } = 1.0;

    /// <summary>Upper bound on a single reply, in tokens.</summary>
    [Range(1, 32768)]
    public int MaxTokens { get; set; } = 1024;

    /// <summary>
    /// Token ceiling for the prompt the context builder assembles.
    /// </summary>
    /// <remarks>
    /// Well under what the model accepts, and on purpose. A 1M window is not a reason to send
    /// a million tokens: attention thins out over a long context, every token is paid for on
    /// every turn, and a prompt that holds everything is a prompt nobody chose. The budget is
    /// what forces the choice to be made and recorded.
    /// </remarks>
    [Range(1000, 900000)]
    public int ContextBudget { get; set; } = 32000;

    /// <summary>How long to wait for a reply before giving up.</summary>
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 180;
}
