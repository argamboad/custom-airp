namespace Airp.Application.Abstractions;

/// <summary>Who wrote a turn, as a chat completions API understands it.</summary>
public enum ModelRole
{
    /// <summary>Instructions, character definition, world state — everything that frames the scene.</summary>
    System = 0,

    /// <summary>The account holder.</summary>
    User,

    /// <summary>The model.</summary>
    Assistant,
}

/// <summary>One turn on its way to the model.</summary>
/// <remarks>
/// Deliberately not <c>ChatMessage</c>. That type carries identifiers, timestamps and flags
/// that belong to a stored conversation; this one carries only what crosses the wire. Keeping
/// them apart is what stops storage concerns leaking into prompt construction, and it is the
/// seam where a context builder will later decide that a stored message does not go at all.
/// </remarks>
/// <param name="Role">Who wrote it.</param>
/// <param name="Content">The text.</param>
public readonly record struct ModelMessage(ModelRole Role, string Content);

/// <summary>What the model wrote back, and what it cost to get it.</summary>
public sealed record ModelReply
{
    /// <summary>The reply text.</summary>
    public required string Text { get; init; }

    /// <summary>The model that actually answered, as the API reported it.</summary>
    /// <remarks>
    /// Worth keeping even though the caller asked for a specific one: a router is free to
    /// serve a different build than the name implied, and a reply that reads oddly is much
    /// easier to explain when the record says which model wrote it.
    /// </remarks>
    public string? Model { get; init; }

    /// <summary>
    /// The upstream host that served the request, when the API names one.
    /// </summary>
    /// <remarks>
    /// A router fronts many hosts for the same model, and they are not interchangeable: they
    /// differ in price, in whether they cache, and in what they are willing to generate. When a
    /// reply comes out worse than usual, or a scene is refused, the host is the first thing
    /// worth knowing and the one thing that is unrecoverable after the fact.
    /// </remarks>
    public string? Provider { get; init; }

    /// <summary>Tokens of input, when the API reported them.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Tokens generated, when the API reported them.</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>
    /// What this call was actually charged, in the account's currency, when the API says.
    /// </summary>
    /// <remarks>
    /// The figure the router billed, not one worked out here from a price list. Prices change,
    /// a router fans one model across hosts that charge differently, and a cached prefix is
    /// discounted — so any number this client computed itself would drift away from the invoice
    /// and never say by how much.
    /// </remarks>
    public double? Cost { get; init; }

    /// <summary>
    /// Prompt tokens the provider served from its cache rather than reading again.
    /// </summary>
    /// <remarks>
    /// The one measurement that says whether the prompt's layer order is doing its job. The
    /// whole ordering contract exists to keep everything before the first change of the turn
    /// cacheable; without this figure that is a belief rather than an observation.
    /// </remarks>
    public int? CachedTokens { get; init; }

    /// <summary>Prompt tokens written into the cache, on models that charge for it.</summary>
    public int? CacheWriteTokens { get; init; }

    /// <summary>
    /// The router's own identifier for this generation.
    /// </summary>
    /// <remarks>
    /// Kept so a charge can be taken back to the provider's record of it later. It costs one
    /// short string per reply and is unrecoverable once the response is gone.
    /// </remarks>
    public string? GenerationId { get; init; }

    /// <summary>
    /// Why generation stopped — <c>stop</c> for a finished reply, <c>length</c> for one cut
    /// off at the token ceiling.
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>Whether the reply was truncated by the token ceiling rather than finished.</summary>
    public bool WasTruncated =>
        string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Calls an OpenAI-compatible chat completions endpoint.</summary>
public interface ILanguageModelClient
{
    /// <summary>Asks the model to write the next turn.</summary>
    /// <remarks>
    /// <strong>This spends the account's credit.</strong> Nothing is retried inside: a caller
    /// that wants a second attempt is asking for a second charge and should say so.
    /// </remarks>
    /// <param name="messages">The conversation so far, oldest first.</param>
    /// <param name="model">Model identifier, or <see langword="null"/> for the configured one.</param>
    /// <param name="temperature">Sampling temperature, or <see langword="null"/> for the configured one.</param>
    /// <param name="maxTokens">Token ceiling for the reply, or <see langword="null"/> for the configured one.</param>
    /// <param name="frequencyPenalty">
    /// Penalty on re-used wording, or <see langword="null"/> to send none. Null rather than
    /// zero, because omitting the field and sending <c>0</c> are different requests to some
    /// backends and only one of them is "no opinion".
    /// </param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="Domain.ModelUnavailableException">
    /// The API refused the request or could not be reached.
    /// </exception>
    Task<ModelReply> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        string? model = null,
        double? temperature = null,
        int? maxTokens = null,
        double? frequencyPenalty = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the model identifiers the account can reach.</summary>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>Model identifiers, in the order the API returned them.</returns>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Turns text into vectors that can be compared for similarity.</summary>
public interface IEmbeddingClient
{
    /// <summary>Embeds a batch of texts.</summary>
    /// <remarks>
    /// A batch rather than one at a time, because the cost of an embedding call is dominated by
    /// the round trip and a conversation is embedded in stretches.
    /// </remarks>
    /// <param name="texts">The texts, in order.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>One vector per text, in the same order.</returns>
    /// <exception cref="Domain.ModelUnavailableException">The API refused or could not be reached.</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and writes secrets that must never live in a configuration file.
/// </summary>
/// <remarks>
/// Named lookups rather than typed properties, so adding a second provider does not change
/// this interface. Implementations are expected to return <see langword="null"/> for a
/// missing secret rather than throwing: "no key configured" is an ordinary state on a first
/// run, not an error.
/// </remarks>
public interface ISecretStore
{
    /// <summary>Reads a secret.</summary>
    /// <param name="name">Name of the secret.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The value, or <see langword="null"/> when it is not set.</returns>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Stores a secret, replacing any previous value.</summary>
    /// <param name="name">Name of the secret.</param>
    /// <param name="value">The value to protect.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes once the secret is on disk.</returns>
    Task SetAsync(string name, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes a secret. Succeeds whether or not one was there.</summary>
    /// <param name="name">Name of the secret.</param>
    /// <param name="cancellationToken">Token used to abort the removal.</param>
    /// <returns>A task that completes once the secret is gone.</returns>
    Task RemoveAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Says where a secret would be read from, without revealing it.
    /// </summary>
    /// <remarks>
    /// Exists so diagnostics can answer "is my key set up, and which one is winning" — the
    /// question that actually gets asked — without any code path that prints the value.
    /// </remarks>
    /// <param name="name">Name of the secret.</param>
    /// <param name="cancellationToken">Token used to abort the check.</param>
    /// <returns>A short human-readable description of the source.</returns>
    Task<string> DescribeAsync(string name, CancellationToken cancellationToken = default);
}
