namespace Airp.Domain;

/// <summary>
/// Base class for every error this application raises deliberately. Catching this type
/// separates "the site or the browser misbehaved" from genuine programming faults.
/// </summary>
public abstract class AirpException : Exception
{
    /// <summary>Initialises the exception.</summary>
    /// <param name="message">A message suitable for display in the terminal.</param>
    /// <param name="inner">The underlying failure, if any.</param>
    protected AirpException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>
    /// A short, actionable hint rendered under the error banner, for example
    /// "press R to sign in again".
    /// </summary>
    public abstract string RecoveryHint { get; }
}

/// <summary>A navigation or page interaction did not complete in time.</summary>
public sealed class ReplyTimeoutException : AirpException
{
    /// <summary>Initialises the exception.</summary>
    /// <param name="message">A message suitable for display in the terminal.</param>
    /// <param name="inner">The underlying failure, if any.</param>
    public ReplyTimeoutException(string message, Exception? inner = null) : base(message, inner) { }

    /// <inheritdoc />
    public override string RecoveryHint =>
        "The site took too long to respond. Press R to retry, or raise Site:NavigationTimeoutSeconds in the config file.";
}

/// <summary>
/// The page loaded but did not look the way the adapter expected — usually because the
/// site changed its markup.
/// </summary>
public sealed class ContractException : AirpException
{
    /// <summary>Initialises the exception.</summary>
    /// <param name="message">A message suitable for display in the terminal.</param>
    /// <param name="what">The element or payload the adapter was looking for.</param>
    /// <param name="inner">The underlying failure, if any.</param>
    public ContractException(
        string message,
        string? what = null,
        Exception? inner = null,
        string? recoveryHint = null)
        : base(message, inner)
    {
        What = what;
        _recoveryHint = recoveryHint;
    }

    private readonly string? _recoveryHint;

    /// <summary>The element or payload the adapter was looking for.</summary>
    public string? What { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Defaults to the selector advice, which fits the common case of an extraction that
    /// stopped matching. A caller that knows better — a request the site actively rejected,
    /// say, where no selector is involved — supplies its own rather than sending the reader
    /// to edit configuration that has nothing to do with the failure.
    /// </remarks>
    public override string RecoveryHint =>
        _recoveryHint
        ?? "The site's layout no longer matches the configured selectors. Run 'airp diagnose' to dump what "
        + "the page actually contains, then adjust the Site:Selectors section of the config file.";
}

/// <summary>
/// The message was sent and the site kept it, but no reply was ever generated for it.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="ReplyTimeoutException"/>, and the distinction is the whole point:
/// a timeout means nothing could be observed, whereas this means something was observed and
/// what it said was that the reply is not coming. The two need different words because they
/// need different actions — one is worth waiting out, the other is not.
/// </para>
/// <para>
/// It carries <see cref="Partial"/> because the failure is only half a failure. The message
/// was accepted, and a caller that discards that on its way to reporting an error leaves the
/// reader looking at a conversation missing a message the site is holding — which is how the
/// same message gets sent, and paid for, twice.
/// </para>
/// </remarks>
public sealed class ReplyMissingException : AirpException
{
    /// <summary>Initialises the exception.</summary>
    /// <param name="message">A message suitable for display in the terminal.</param>
    /// <param name="partial">What the transcript gained regardless — normally the sent message.</param>
    /// <param name="inner">The underlying failure, if any.</param>
    public ReplyMissingException(
        string message,
        IReadOnlyList<Conversations.ChatMessage> partial,
        Exception? inner = null)
        : base(message, inner)
        => Partial = partial;

    /// <summary>
    /// The new messages that did arrive, which is normally the one that was sent and nothing
    /// else. Never null; callers merge it rather than assigning it.
    /// </summary>
    public IReadOnlyList<Conversations.ChatMessage> Partial { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Says "kept" rather than "on the site". Both adapters raise this and only one of them
    /// involves a site — but the instruction that matters is the same either way, and it is the
    /// one worth getting right: <strong>do not send it again</strong>. A reader told their
    /// message failed will retype it, and then it is in the conversation twice.
    /// </remarks>
    public override string RecoveryHint =>
        "Your message was kept — do not send it again. Press R to refresh in case the reply "
        + "lands late, or delete the message and send it once more if it never does.";
}

/// <summary>
/// The language model API refused the request, or could not be reached.
/// </summary>
/// <remarks>
/// Separate from <see cref="SiteNetworkException"/> because the recovery is different in a way
/// the reader has to act on: a site failure is usually worth retrying, whereas a model refusing
/// on credit, key or model name will refuse identically forever. <see cref="StatusCode"/> is
/// carried so the hint can say which of those it was.
/// </remarks>
public sealed class ModelUnavailableException : AirpException
{
    /// <summary>Initialises the exception.</summary>
    /// <param name="message">A message suitable for display in the terminal.</param>
    /// <param name="statusCode">The HTTP status the API returned, when there was one.</param>
    /// <param name="inner">The underlying failure, if any.</param>
    public ModelUnavailableException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
        => StatusCode = statusCode;

    /// <summary>The HTTP status the API returned, when the failure got that far.</summary>
    public int? StatusCode { get; }

    /// <inheritdoc />
    public override string RecoveryHint => StatusCode switch
    {
        401 or 403 => "The API key was rejected. Store a new one with 'airp secret set', "
            + "under the name in Model:ApiKeyName.",
        402 => "The account is out of credit. Top it up with whoever Model:BaseUrl points at.",
        404 => "No model by that name. Run 'airp models' to see what the account can reach.",
        429 => "Rate limited. Wait a moment and try again.",
        >= 500 => "The provider is having trouble. Try again, or switch model with --model.",
        _ => "Check the connection and the configured Model:BaseUrl, then try again.",
    };
}

