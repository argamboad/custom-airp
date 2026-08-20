using Airp.Application.Abstractions;
using Airp.Application.Context;
using Airp.Domain;
using Microsoft.Extensions.Logging;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// The model calls nobody is watching.
/// </summary>
/// <remarks>
/// <para>
/// A reply that fails is visible: the reader is sitting there, sees it, and presses the key
/// again. Summarising and fact extraction fail into a log line. Both are wired to give up
/// quietly on purpose — neither is a precondition for a reply, and a story that stops because
/// the world state could not be updated would be a worse failure than a stale world state.
/// </para>
/// <para>
/// Quietly is not the same as immediately. Observed over seven extractions on one story: two
/// died on <c>a response with no message content</c>, which is a host returning 200 and
/// nothing — the same lottery that produces token soup, and unrelated to the request. One of
/// the two was the extraction over the first sixty-two messages, the most valuable one the
/// story will ever run. It was never attempted again, because the stretch it covered was
/// summarised in the same breath and no later call will look at those turns.
/// </para>
/// <para>
/// So: one retry, and only for the failures a second attempt could plausibly answer. The
/// router picks a host per request, so the retry usually lands somewhere else. A rejected key
/// or an empty account will fail the same way twice and is not retried.
/// </para>
/// </remarks>
internal static class Background
{
    /// <summary>Whether trying again could plausibly give a different answer.</summary>
    /// <remarks>
    /// 200-with-no-content is the host, not the request. 429 and 5xx are the moment. Everything
    /// else — a rejected key, no credit, an unknown model — is the account or the configuration,
    /// and will be exactly as wrong on the second call while costing a second call.
    /// </remarks>
    /// <param name="failure">What went wrong.</param>
    /// <returns>Whether to try once more.</returns>
    private static bool WorthAnotherGo(ModelUnavailableException failure)
        => failure.StatusCode is null or 200 or 408 or 429 or (>= 500 and < 600);

    /// <summary>Runs a background call, once more if the first attempt was unlucky.</summary>
    /// <param name="model">The model client.</param>
    /// <param name="messages">The prompt.</param>
    /// <param name="choice">Model, temperature and ceiling for this task.</param>
    /// <param name="logger">Logger. Never receives message text.</param>
    /// <param name="what">What is being attempted, for the log line.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>The reply.</returns>
    public static async Task<ModelReply> CompleteAsync(
        ILanguageModelClient model,
        IReadOnlyList<ModelMessage> messages,
        ModelChoice choice,
        ILogger logger,
        string what,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        try
        {
            return await model.CompleteAsync(
                    messages,
                    choice.Model,
                    choice.Temperature,
                    choice.MaxTokens,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ModelUnavailableException failure) when (WorthAnotherGo(failure))
        {
            logger.LogWarning(
                "{What} did not come back ({Reason}); trying once more.",
                what,
                failure.Message);
        }

        return await model.CompleteAsync(
                messages,
                choice.Model,
                choice.Temperature,
                choice.MaxTokens,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
