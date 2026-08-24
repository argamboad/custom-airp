using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;
using Airp.Application.Context;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// Brings specific old turns back into a prompt when they bear on what was just said.
/// </summary>
/// <remarks>
/// <para>
/// The other half of the memory layer, and the half that answers a different question. A
/// summary says what happened over a stretch; retrieval says <em>this exact exchange, three
/// hundred turns ago, is about what you are asking now</em>. Summaries lose the wording, and
/// the wording is often the point.
/// </para>
/// <para>
/// Only turns already compressed out of the prompt are candidates. Recent ones are being sent
/// whole regardless, so retrieving them would spend a call to find what the model can already
/// read — and would crowd the prompt with duplicates of it.
/// </para>
/// </remarks>
internal sealed class MemoryRetriever
{
    private readonly IEmbeddingClient _embeddings;
    private readonly ILogger _logger;

    /// <summary>Initialises the retriever.</summary>
    /// <param name="embeddings">The client that turns text into vectors.</param>
    /// <param name="logger">Logger. Never receives message text.</param>
    public MemoryRetriever(IEmbeddingClient embeddings, ILogger logger)
    {
        _embeddings = embeddings;
        _logger = logger;
    }

    /// <summary>
    /// Embeds whatever has aged out and is not embedded yet.
    /// </summary>
    /// <param name="store">The open store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="upToSequence">The last turn that has been compressed away.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>How many turns were embedded.</returns>
    public async Task<int> BackfillAsync(
        AirpDbContext store,
        string conversationId,
        long upToSequence,
        CancellationToken cancellationToken)
    {
        var pending = await store.Messages
            .Where(m => m.ConversationId == conversationId
                        && m.Sequence <= upToSequence
                        && m.DeletedAtUtc == null
                        && m.Embedding == null)
            .OrderBy(m => m.Sequence)
            .Take(128)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return 0;
        }

        try
        {
            var vectors = await _embeddings
                .EmbedAsync([.. pending.Select(static m => m.Text)], cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < pending.Count && i < vectors.Count; i++)
            {
                pending[i].Embedding = Similarity.ToBytes(vectors[i]);
            }

            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Embedded {Count} aged-out turn(s).", pending.Count);
            return pending.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Retrieval is an improvement on the prompt, not a precondition for it. A turn the
            // reader is waiting on must not fail because the embeddings endpoint is down.
            _logger.LogWarning(ex, "Embedding failed; retrieval will be thinner this turn.");
            return 0;
        }
    }

    /// <summary>
    /// Finds the compressed turns that bear on what was just said.
    /// </summary>
    /// <param name="store">The open store.</param>
    /// <param name="conversation">
    /// The conversation, not just its id: the recalled lines are a stretch of transcript
    /// rendered for the model to read, and both sides of it are named the way
    /// <see cref="Transcript"/> names them. Labelling the reader's turns <c>User</c> here while
    /// the summaries above call the same person by their persona name split one person into
    /// two — the exact bug <see cref="Transcript.Reader"/> exists to prevent, alive in the one
    /// background reader that had kept its own copy of the labels.
    /// </param>
    /// <param name="query">What the reader just wrote.</param>
    /// <param name="upToSequence">The last turn that has been compressed away.</param>
    /// <param name="settings">Model settings, for the count and the threshold.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>Rendered lines, most relevant first, or empty when nothing is relevant.</returns>
    public async Task<IReadOnlyList<string>> RecallAsync(
        AirpDbContext store,
        ConversationRecord conversation,
        string query,
        long upToSequence,
        ModelOptions settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var conversationId = conversation.Id;

        if (settings.RecallCount == 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var candidates = await store.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId
                        && m.Sequence <= upToSequence
                        && m.DeletedAtUtc == null
                        && m.Embedding != null)
            .Select(m => new { m.Sequence, m.Role, m.Text, m.Embedding })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return [];
        }

        float[] wanted;

        try
        {
            var embedded = await _embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false);

            if (embedded.Count == 0)
            {
                return [];
            }

            wanted = embedded[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not embed the query; sending no recalled turns.");
            return [];
        }

        var scored = candidates
            .Select(c => (c.Sequence, c.Role, c.Text, Score: Similarity.Cosine(wanted, Similarity.FromBytes(c.Embedding))))
            .Where(c => c.Score >= settings.RecallThreshold)
            .OrderByDescending(static c => c.Score)
            .Take(settings.RecallCount)
            // Back into transcript order once chosen: they are excerpts of a conversation, and
            // handing the model a scene out of sequence invites it to reorder events.
            .OrderBy(static c => c.Sequence)
            .ToArray();

        if (scored.Length == 0)
        {
            return [];
        }

        _logger.LogInformation(
            "Recalled {Count} turn(s), best score {Score:F2}.",
            scored.Length,
            scored.Max(static s => s.Score));

        var reader = Transcript.Reader(conversation);
        var character = Transcript.Character(conversation);

        return
        [
            "Earlier in this conversation:",
            .. scored.Select(s =>
                $"[{s.Sequence}] {(s.Role == ChatRole.Assistant ? character : reader)}: {s.Text}"),
        ];
    }
}
