using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;
using Airp.Application.Context;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// Compresses turns that no longer fit, so they leave the prompt without leaving the
/// conversation.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference the project exists for. Without it, a prompt over budget simply
/// drops its oldest turns — which is what the services this replaces already do, and the
/// reason their characters forget. Summarising costs a second model call and keeps what
/// happened.
/// </para>
/// <para>
/// Runs at send time rather than in the background, and only when the budget would actually
/// bind. A conversation that fits is never summarised, so nothing is spent and nothing is
/// compressed until compression is the alternative to loss.
/// </para>
/// </remarks>
/// <summary>What the summariser prepared for one turn.</summary>
/// <param name="Summaries">Summaries covering the compressed prefix, oldest first.</param>
/// <param name="Recent">The turns to send whole.</param>
/// <param name="CompressionFailed">
/// Whether turns that should have been compressed could not be, and are therefore being sent
/// whole. The caller is expected to raise the budget rather than let them be dropped: going
/// over budget costs a little more, and dropping them is the forgetting this exists to stop.
/// </param>
internal readonly record struct SummarisedHistory(
    IReadOnlyList<string> Summaries,
    IReadOnlyList<MessageRecord> Recent,
    bool CompressionFailed);

internal sealed class ConversationSummariser
{
    /// <summary>
    /// What the summariser is asked for.
    /// </summary>
    /// <remarks>
    /// Written for roleplay, not for prose in general. A plot synopsis is the wrong artefact:
    /// what a character has to carry forward is what was established, what changed between the
    /// people in the scene, and where things were left — the things a reader would be annoyed
    /// to have to repeat.
    /// </remarks>
    private const string Instruction =
        """
        You are compressing part of a roleplay transcript so it can be carried forward once the
        original turns no longer fit in the context.

        Write a factual account, in the same language as the transcript, covering:
        - what happened, in order
        - facts established about the characters, places and relationships
        - what changed between the participants
        - anything promised, threatened, agreed or left unresolved
        - where and how the scene was left

        Do not write in character, do not add events, and do not editorialise. Prefer specifics
        over impressions: names, places and commitments matter more than mood. Be brief.
        """;

    private readonly ILanguageModelClient _model;
    private readonly ILogger _logger;

    /// <summary>Initialises the summariser.</summary>
    /// <param name="model">The model that writes the summaries.</param>
    /// <param name="logger">Logger. Never receives message text.</param>
    public ConversationSummariser(ILanguageModelClient model, ILogger logger)
    {
        _model = model;
        _logger = logger;
    }

    /// <summary>
    /// Summarises whatever would otherwise be dropped, and returns the summaries to send.
    /// </summary>
    /// <param name="store">The open store.</param>
    /// <param name="conversation">The conversation.</param>
    /// <param name="history">The visible transcript, oldest first.</param>
    /// <param name="settings">Model settings, for the budget and the summarising model.</param>
    /// <param name="character">
    /// The character layer as it will be sent — <em>resolved</em>, not the conversation's own
    /// text. Nearly every conversation names a file rather than carrying a copy, so reading
    /// <see cref="ConversationRecord.CharacterDefinition"/> here reserved nothing for a layer
    /// that was half the prompt.
    /// </param>
    /// <param name="persona">The persona, resolved the same way. Framed before it is counted.</param>
    /// <param name="directives">The dials rendered as text, or null.</param>
    /// <param name="worldState">What the story holds to be true, or null.</param>
    /// <param name="trackers">The meters rendered as text, or null.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>The summaries, the turns to send whole, and whether compression was lost.</returns>
    public async Task<SummarisedHistory> PrepareAsync(
        AirpDbContext store,
        ConversationRecord conversation,
        IReadOnlyList<MessageRecord> history,
        ModelOptions settings,
        string? character,
        string? persona,
        string? directives,
        string? worldState,
        string? trackers,
        CancellationToken cancellationToken)
    {
        var existing = await store.Summaries
            .Where(s => s.ConversationId == conversation.Id)
            .OrderBy(s => s.FromSequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var covered = existing.Count > 0 ? existing[^1].ToSequence : 0;
        var uncovered = history.Where(m => m.Sequence > covered).ToList();

        // What the transcript may spend: the budget less everything else the prompt carries,
        // counted exactly as the builder will count it. This has to agree with the builder or
        // the two disagree about how much room the transcript has — and the builder wins,
        // because it is the one that drops turns.
        var reserved = ContextBuilder.Reserve(
                           character,
                           ContextBuilder.PersonaLayer(persona),
                           directives,
                           worldState,
                           existing.Count > 0 ? string.Join("\n\n", existing.Select(s => s.Text)) : null,
                           trackers)
                       + Retrieval(history, settings)
                       + settings.MaxTokens
                       + 200;

        var allowance = Math.Max(0, settings.ContextBudget - reserved);

        // Walk back from the newest turn until the allowance is used up. Everything before
        // that point is what would be dropped, and is therefore what has to be compressed.
        var kept = 0;
        var spent = 0;

        for (var i = uncovered.Count - 1; i >= 0; i--)
        {
            var cost = TokenEstimator.ForText(uncovered[i].Text) + 4;

            if (spent + cost > allowance)
            {
                break;
            }

            spent += cost;
            kept++;
        }

        var toCompress = Worthwhile(uncovered, uncovered.Count - kept);

        if (toCompress.Count > 0)
        {
            var summary = await WriteAsync(store, conversation, toCompress, settings, cancellationToken)
                .ConfigureAwait(false);

            if (summary is not null)
            {
                store.Summaries.Add(summary);
                await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                existing.Add(summary);

                _logger.LogInformation(
                    "Summarised turns {From}-{To} of {Conversation} into {Tokens} tokens.",
                    summary.FromSequence,
                    summary.ToSequence,
                    conversation.Id,
                    TokenEstimator.ForText(summary.Text));

                // The same stretch, read for a different question. The summary says what
                // happened over it; the facts say what it left true. This is the moment those
                // turns stop being visible to the model in their own words, so it is the last
                // moment either can be taken from them cheaply.
                var (added, retired) = await new FactExtractor(_model, _logger)
                    .UpdateAsync(store, conversation, toCompress, settings, cancellationToken)
                    .ConfigureAwait(false);

                if (added > 0 || retired > 0)
                {
                    _logger.LogInformation(
                        "World state: {Added} fact(s) added, {Retired} retired.", added, retired);
                }
            }
            else
            {
                // The summary could not be written. Sending the turns whole and letting the
                // budget drop them is worse than sending them and going over: the model would
                // forget, which is the failure this exists to prevent.
                _logger.LogWarning(
                    "Could not summarise {Count} turn(s) of {Conversation}; sending them whole "
                    + "and going over budget rather than dropping them.",
                    toCompress.Count,
                    conversation.Id);

                return new SummarisedHistory(
                    [.. existing.Select(s => s.Text)],
                    history,
                    CompressionFailed: true);
            }
        }

        var recentFrom = existing.Count > 0 ? existing[^1].ToSequence : 0;

        return new SummarisedHistory(
            [.. existing.Select(s => s.Text)],
            [.. history.Where(m => m.Sequence > recentFrom)],
            CompressionFailed: false);
    }

    /// <summary>
    /// How few turns are too few to be worth a summarising call.
    /// </summary>
    /// <remarks>
    /// Measured on a real story rather than chosen. Compressing the minimum that overflows
    /// means compressing whatever the last send pushed over the edge — two messages — and the
    /// instruction asks for six things about them: what happened, what was established, what
    /// changed, what was promised, where the scene was left. On two messages that scaffolding
    /// produces about as much text as it replaced. Observed ratios in one conversation: 37×
    /// over 62 messages, 1.06× over two, and one stretch of two that came out <em>longer</em>
    /// than the turns it stood in for — a paid call that made the prompt bigger.
    /// </remarks>
    private const int WorthACall = 10;

    /// <summary>
    /// How many messages one summary may stand in for.
    /// </summary>
    /// <remarks>
    /// The output ceiling for a summarising call is fixed, so fidelity falls as the stretch
    /// grows: sixty-two messages came back as a good 428-token account, and ninety-nine came
    /// back as two characters. A backlog larger than this is compressed in several passes
    /// instead of one, which costs another call and keeps the detail that makes a summary worth
    /// having.
    /// </remarks>
    private const int AtMostPerSummary = 40;

    /// <summary>
    /// How much of the recent transcript is never compressed, however tight the budget.
    /// </summary>
    /// <remarks>
    /// The turn just sent is in here. Reaching the batch size by swallowing the exchange the
    /// reader is in the middle of would trade the bug for a worse one.
    /// </remarks>
    private const int AlwaysWhole = 6;

    /// <summary>
    /// Widens a compression to a stretch worth compressing.
    /// </summary>
    /// <remarks>
    /// Compressing exactly what overflowed refires on the next send, because the next send
    /// overflows again by exactly one exchange. Taking a batch buys headroom for several turns
    /// and — the part that matters more — hands the extractor a stretch with something durable
    /// in it. Four extractions in a row returned empty arrays over two-message stretches, which
    /// was the model being right: nothing is established in two messages.
    /// <para>
    /// The extra turns are not lost. They leave the prompt for the summary, stay whole in the
    /// store, and are embedded for retrieval on the way out — which is the trade this whole
    /// design is built on.
    /// </para>
    /// </remarks>
    /// <param name="uncovered">Turns no summary covers yet, oldest first.</param>
    /// <param name="overflowing">How many must go for the transcript to fit.</param>
    /// <returns>The stretch to compress, oldest first.</returns>
    private static List<MessageRecord> Worthwhile(IReadOnlyList<MessageRecord> uncovered, int overflowing)
    {
        if (overflowing <= 0)
        {
            return [];
        }

        // Never past the turns being played. When there is not room for a full batch this
        // takes what there is, which is still the minimum that has to go.
        var room = Math.Max(overflowing, Math.Min(WorthACall, uncovered.Count - AlwaysWhole));

        // Capped as well as floored. A backlog of a hundred turns arrives here as one stretch,
        // and one summarising call cannot carry a hundred turns — the caller compresses again
        // on the next pass, and the transcript is over budget only until it does.
        return [.. uncovered.Take(Math.Min(room, AtMostPerSummary))];
    }

    /// <summary>
    /// The least a summary of these turns could plausibly be.
    /// </summary>
    /// <remarks>
    /// Scaled to what it is standing in for rather than a flat floor: ten short messages
    /// genuinely do compress to a couple of sentences, while a hundred cannot.
    /// <para>
    /// The ratio is a ceiling on compression, and it is set from measurement. Summaries that
    /// worked ran between 3× and 19×; the ones that had to be caught were 84× — twenty-seven
    /// messages ending mid-word — and roughly 30,000× for the reply that was <c>##</c>. Sixty
    /// sits far enough above every working figure to catch only an answer that stopped rather
    /// than finished.
    /// </para>
    /// </remarks>
    /// <param name="messages">The stretch being compressed.</param>
    /// <returns>The smallest believable summary, in tokens.</returns>
    private static int Credible(IReadOnlyList<MessageRecord> messages)
    {
        var source = messages.Sum(m => TokenEstimator.ForText(m.Text));

        return Math.Max(20, source / 60);
    }

    /// <summary>Room to leave for the turns retrieval will bring back.</summary>
    /// <remarks>
    /// The one layer whose size cannot be known here: which turns are recalled depends on what
    /// this call is about to compress. It is bounded, though — <c>RecallCount</c> turns of the
    /// same transcript — so the mean turn is a fair estimate, and it grows with the story the
    /// way the real layer does.
    /// <para>
    /// Erring high is the safe direction. Reserving too much compresses a turn or two earlier
    /// than strictly necessary, which costs one summarising call; reserving too little lets the
    /// builder drop turns nothing was written down about, which costs the story.
    /// </para>
    /// </remarks>
    /// <param name="history">The visible transcript.</param>
    /// <param name="settings">Model settings, for how many turns retrieval returns.</param>
    /// <returns>The tokens to hold back for the memories layer.</returns>
    private static int Retrieval(IReadOnlyList<MessageRecord> history, ModelOptions settings)
    {
        if (settings.RecallCount <= 0 || history.Count == 0)
        {
            return 0;
        }

        var mean = history.Sum(m => TokenEstimator.ForText(m.Text)) / history.Count;
        return Math.Min(settings.RecallCount, history.Count) * mean;
    }

    /// <summary>Asks the model to compress a stretch of transcript.</summary>
    /// <returns>The summary, or <see langword="null"/> when the model would not write one.</returns>
    private async Task<SummaryRecord?> WriteAsync(
        AirpDbContext store,
        ConversationRecord conversation,
        IReadOnlyList<MessageRecord> messages,
        ModelOptions settings,
        CancellationToken cancellationToken)
    {
        var transcript = Transcript.Render(conversation, messages);

        var choice = ModelRouter.For(ModelTask.Summary, settings);

        try
        {
            var reply = await Background.CompleteAsync(
                _model,
                [
                    new ModelMessage(ModelRole.System, Instruction),
                    new ModelMessage(ModelRole.User, transcript),
                ],
                choice,
                _logger,
                "The summary",
                cancellationToken).ConfigureAwait(false);

            // Recorded before the reply is judged. Compressing fires without the reader asking
            // for it, and a call that came back useless was billed exactly like one that did
            // not — so the row goes in even when the summary is thrown away on the next line.
            store.Spend.Add(Ledger.Row(conversation.Id, SpendKind.Summary, reply));

            if (string.IsNullOrWhiteSpace(reply.Text))
            {
                return null;
            }

            // A reply is not automatically a summary. Observed on the real story: ninety-nine
            // messages went up and "##" came back — two characters, accepted, stored, and left
            // standing in for the first hundred turns of a conversation while the turns
            // themselves left the prompt. Nothing downstream ever looks at a summary again, so
            // the more useless it is, the longer it survives.
            //
            // Refusing it here reaches the branch that already exists for a summary that could
            // not be written at all: the turns go whole and the budget is exceeded, which costs
            // cents, against a character that has forgotten, which costs the story.
            var produced = TokenEstimator.ForText(reply.Text);

            // Judged against the stretch, and only against the stretch. A guard on "was it cut
            // off, and below half the ceiling" was tried here and removed the same day: it
            // refused a 582-token account of ten messages for being eighteen tokens under an
            // arbitrary line, while the summary it was written to catch — seventy tokens for
            // twenty-seven messages — fails the ratio on its own. A fraction of the ceiling says
            // nothing about whether an answer covers what it replaced.
            //
            // Refusing a clipped-but-substantial summary is also the worse failure. It loses the
            // tail of the stretch, which is bad; refusing it means never compressing at all
            // against a host that always clips, and a transcript permanently over budget is
            // worse than one summary missing its last sentence.
            if (produced < Credible(messages))
            {
                _logger.LogWarning(
                    "Summarising {Count} message(s) of {Conversation} produced {Tokens} token(s), "
                    + "which cannot be an account of them; refusing it.",
                    messages.Count,
                    conversation.Id,
                    produced);

                return null;
            }

            return new SummaryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversation.Id,
                FromSequence = messages[0].Sequence,
                ToSequence = messages[^1].Sequence,
                Text = reply.Text.Trim(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Model = reply.Model,
                MessageCount = messages.Count,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed summary must not fail the turn the reader is waiting on.
            _logger.LogWarning(ex, "Summarising failed.");
            return null;
        }
    }
}
