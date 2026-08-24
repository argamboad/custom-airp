using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Context;
using Airp.Application.Dials;
using Airp.Application.Options;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// A conversation adapter whose "site" is this machine: SQLite holds the transcript and a
/// language model writes the replies.
/// </summary>
/// <remarks>
/// <para>
/// The same four interfaces the OurDream adapter implements, which is the whole reason the
/// terminal needs no changes to use it. Where the two differ is ownership: OurDream holds the
/// conversation and this client reads it, whereas here the conversation is ours and the model
/// is a service we call. Two consequences follow, and both are deliberate.
/// </para>
/// <para>
/// <strong>Nothing is ever deleted.</strong> The terminal offers deletion and the reader
/// expects it to work; underneath, rows are hidden rather than removed. See
/// <c>AirpDbContext</c>, which refuses to save anything else.
/// </para>
/// <para>
/// <strong>The user's turn is stored before the model is called.</strong> If the model then
/// fails, the message is still there and the caller gets a
/// <see cref="ReplyMissingException"/> carrying it, exactly as it would from a site that
/// accepted a message and never answered.
/// </para>
/// </remarks>
public sealed class LocalConversationProvider : IChatProvider, IConversationProvider
{
    private readonly IDbContextFactory<AirpDbContext> _stores;
    private readonly ILanguageModelClient _model;
    private readonly IEmbeddingClient? _embeddings;
    private readonly IOptionsMonitor<AirpOptions> _options;
    private readonly ILogger<LocalConversationProvider> _logger;
    private readonly TextLibrary _library;
    private readonly IDialService _dials;
    private readonly SemaphoreSlim _migration = new(1, 1);
    private bool _migrated;

    /// <summary>Initialises the adapter.</summary>
    /// <param name="stores">Factory for the local store.</param>
    /// <param name="model">The language model that writes replies.</param>
    /// <param name="options">Application options.</param>
    /// <param name="logger">Logger. Never receives message text.</param>
    /// <param name="embeddings">
    /// The client used for retrieval. Optional: without it the adapter still holds the whole
    /// conversation and still summarises, it simply cannot bring specific old turns back.
    /// </param>
    public LocalConversationProvider(
        IDbContextFactory<AirpDbContext> stores,
        ILanguageModelClient model,
        IOptionsMonitor<AirpOptions> options,
        ILogger<LocalConversationProvider> logger,
        IEmbeddingClient? embeddings = null,
        TextLibrary? library = null,
        IDialService? dials = null)
    {
        _stores = stores;
        _model = model;
        _options = options;
        _logger = logger;
        _embeddings = embeddings;
        _library = library ?? new TextLibrary();
        _dials = dials ?? new DialService(
            stores,
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DialService>.Instance);
    }

    /// <inheritdoc />
    public string Key => "local";

    /// <inheritdoc />
    public string DisplayName => "Local";

    /// <summary>
    /// Opens the store, applying any pending migration the first time.
    /// </summary>
    /// <remarks>
    /// Migrating lazily rather than at start-up keeps the cost off every other command: the
    /// terminal can be run against the OurDream adapter without ever creating a database file.
    /// </remarks>
    private async Task<AirpDbContext> OpenAsync(CancellationToken cancellationToken)
    {
        var store = await _stores.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (_migrated)
        {
            return store;
        }

        await _migration.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_migrated)
            {
                await store.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                _migrated = true;
                _logger.LogInformation("Local store ready.");
            }
        }
        finally
        {
            _migration.Release();
        }

        return store;
    }

    // ── Reading ──────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<Chat>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await store.Conversations
            .AsNoTracking()
            .Where(c => c.DeletedAtUtc == null)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Speaker,
                Last = c.Messages
                    .Where(m => m.DeletedAtUtc == null)
                    .OrderByDescending(m => m.Sequence)
                    .Select(m => new { m.Text, m.SentAtUtc })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows
            .OrderByDescending(r => r.Last?.SentAtUtc ?? DateTimeOffset.MinValue)
            .Select(r => new Chat
            {
                Id = r.Id,
                Name = r.Name,
                Speaker = r.Speaker,
                LatestMessage = r.Last?.Text,
                LastMessageAtUtc = r.Last?.SentAtUtc,
            })];
    }

    /// <summary>The live conversations that refer to a library entry by name.</summary>
    /// <remarks>
    /// What "can I safely rewrite this file" and "can I safely delete it" both need answered.
    /// Only live conversations count: a hidden one keeps its reference, but resolution falling
    /// back to the default in a conversation nobody can open is not a consequence.
    /// </remarks>
    /// <param name="persona">True for the persona library, false for characters.</param>
    /// <param name="name">The entry's name, matched without extension or case.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>Names of the conversations using it, alphabetically.</returns>
    public async Task<IReadOnlyList<string>> ConversationsUsingAsync(
        bool persona,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var wanted = Path.GetFileNameWithoutExtension(name.Trim());

        var referenced = await store.Conversations
            .AsNoTracking()
            .Where(c => c.DeletedAtUtc == null)
            .Select(c => new { c.Name, Uses = persona ? c.PersonaName : c.CharacterName })
            .Where(c => c.Uses != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. referenced
            .Where(c => string.Equals(
                Path.GetFileNameWithoutExtension(c.Uses!.Trim()),
                wanted,
                StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <inheritdoc />
    public async Task<Chat?> GetAsync(string chatId, CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => c.Id == chatId);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await VisibleAsync(store, conversationId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ChatMessage>> VisibleAsync(
        AirpDbContext store,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var rows = await store.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.DeletedAtUtc == null)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(static m => m.ToDomain())];
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> SendAsync(
        string conversationId,
        string text,
        string? instruction = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        // Anchored on the last reply — the state this message answers — rather than on the
        // next free slot. The distinction is the whole point: after a send fails at the model
        // the user's turn is already stored, so anchoring on the next slot would hash
        // differently on the retry and store the same sentence twice. The same words typed
        // again *after* a reply has landed anchor differently, and are genuinely a new send.
        var anchor = await store.Messages
            .Where(m => m.ConversationId == conversationId
                        && m.Role == ChatRole.Assistant
                        && m.DeletedAtUtc == null)
            .MaxAsync(m => (long?)m.Sequence, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        // The direction is part of what is being asked for, so it is part of the identity of
        // the request. The same sentence sent twice with different directions wants two
        // different replies; without this the second would be handed the first one back.
        var hash = Hash(conversationId, anchor, text, instruction);

        var existing = await store.Messages
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.RequestHash == hash, cancellationToken)
            .ConfigureAwait(false);

        MessageRecord sent;

        if (existing is not null)
        {
            var answered = await store.Messages
                .AnyAsync(
                    m => m.ConversationId == conversationId
                         && m.Sequence > existing.Sequence
                         && m.Role == ChatRole.Assistant
                         && m.DeletedAtUtc == null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (answered)
            {
                // Already sent and already answered. Replaying would charge for a second reply
                // and leave the transcript saying the reader typed twice.
                _logger.LogInformation("Send already recorded and answered; returning what is stored.");

                return [.. (await VisibleAsync(store, conversationId, cancellationToken).ConfigureAwait(false))
                    .Where(m => m.Id == existing.Id)];
            }

            // Stored, never answered: the previous attempt died at the model. Ask again
            // against the message that is already there.
            _logger.LogInformation("Retrying an unanswered send rather than storing it twice.");
            sent = existing;
        }
        else
        {
            sent = new MessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversationId,
                Sequence = await NextSequenceAsync(store, conversationId, cancellationToken).ConfigureAwait(false),
                Role = ChatRole.User,
                Text = text,
                SentAtUtc = DateTimeOffset.UtcNow,
                RequestHash = hash,
            };

            store.Messages.Add(sent);
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var reply = await ReplyAsync(
                store,
                conversation,
                pending: sent,
                instruction: string.IsNullOrWhiteSpace(instruction) ? null : instruction.Trim(),
                progress: progress,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return [sent.ToDomain(), reply.ToDomain()];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> RegenerateAsync(
        string conversationId,
        RegenerateReason reason,
        string? instructions = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        var last = await store.Messages
            .Where(m => m.ConversationId == conversationId && m.DeletedAtUtc == null)
            .OrderByDescending(m => m.Sequence)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (last is null || last.Role != ChatRole.Assistant)
        {
            throw new ContractException(
                "There is no reply to write again.",
                recoveryHint: "Regenerate applies to the newest reply; send a message first.");
        }

        // Hidden, not removed: the wording being replaced is still something the model wrote,
        // and a later phase that summarises the conversation may want to know it was rejected.
        // It has to be hidden before the call, or the prompt would end on the very reply being
        // rewritten and invite the model to write it again.
        last.DeletedAtUtc = DateTimeOffset.UtcNow;
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ReplyAsync(
                store,
                conversation,
                null,
                LocalPrompt.RegenerateDirective(reason, instructions),
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Nothing arrived to replace it, so put it back. Left hidden, a failed regenerate
            // would take the reply with it — the reader loses a reply they had and a second
            // attempt finds nothing to write again, which is the worst of both.
            last.DeletedAtUtc = null;
            await store.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await VisibleAsync(store, conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> ContinueAsync(
        string conversationId,
        string? instruction = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        await ReplyAsync(
            store,
            conversation,
            pending: null,
            // A direction from the reader replaces the carry-on wording rather than joining it.
            // Both say what this turn should be, and two answers to that question in one prompt
            // is how a reply comes back trying to satisfy neither.
            instruction: string.IsNullOrWhiteSpace(instruction)
                // A card's fail-safe rule — hand the scene back rather than assume what the
                // user does — is phrased as "no exception", and it outranks a polite
                // "without waiting": the reply comes back as a beat that stops and asks. So this
                // separates the two halves the rule conflates. Never writing the user still
                // holds; stopping for them does not, this turn.
                ? "Carry the scene forward yourself. Let time pass and let the world act: other "
                  + "characters speak, move, arrive, react to one another. This reply does not "
                  + "hand the scene back and does not wait — the user's silence is not a cue "
                  + "to stop. Still never write their words, actions or thoughts; leave them "
                  + "something to step into instead."
                : instruction.Trim(),
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await VisibleAsync(store, conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The stored conversation row, for callers that need what the prompt resolves from.</summary>
    /// <remarks>
    /// The terminal's <c>Chat</c> carries what a chat list needs and deliberately not the
    /// character text, the persona or the names of the files they come from. Showing the reader
    /// which of those a turn actually resolves is the whole value of <c>/card</c> and
    /// <c>/persona</c>, so the row itself is handed over rather than widening <c>Chat</c> for
    /// two commands.
    /// </remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The row, or <see langword="null"/> when there is no such conversation.</returns>
    public async Task<ConversationRecord?> RawAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await store.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks about the story out of character, and answers without touching the transcript.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nothing is added to <c>Messages</c>.</strong> That is the whole design: an
    /// answer stored as a turn would be a paragraph the character said out of character, and
    /// every downstream reader would believe it — retrieval embeds it, the summariser
    /// compresses it as something that happened, the extractor pulls facts from it — with the
    /// append-only rule making all of that permanent. So the answer is handed back and written
    /// only to the asides table, which no prompt reads.
    /// </para>
    /// <para>
    /// The prompt is byte-identical to the one the next real turn will send, up to the
    /// instruction. That is deliberate: on a caching provider the question is nearly free,
    /// and it also means the answer is grounded in exactly what the character can currently
    /// see — not in more of the story, and not in less.
    /// </para>
    /// <para>
    /// It still spends credits, so it still gets an audit row. A billed call that left no
    /// trace would make <c>airp audit</c> quietly stop adding up.
    /// </para>
    /// </remarks>
    /// <param name="conversationId">Identifier of the conversation to ask about.</param>
    /// <param name="question">What to ask.</param>
    /// <param name="progress">Receives the phase.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>The answer, with the accounting behind it.</returns>
    /// <exception cref="ReplyMissingException">The model did not answer.</exception>
    public async Task<AskAnswer> AskAsync(
        string conversationId,
        string question,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        var composed = await ComposeAsync(
                store,
                conversation,
                LocalPrompt.AskDirective(question),
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(SendPhase.Waiting);

        ModelReply reply;

        try
        {
            var choice = ModelRouter.For(ModelTask.Aside, composed.Settings);

            reply = await _model.CompleteAsync(
                composed.Context.Messages,
                model: conversation.Model ?? choice.Model,
                temperature: choice.Temperature,
                maxTokens: choice.MaxTokens,
                frequencyPenalty: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing to hand back with it, unlike a send: no message was stored, because an
            // asking is not a turn. So this is simply a call that did not happen.
            throw new ReplyMissingException(
                $"The question was not answered: {ex.Message}",
                [],
                ex);
        }

        progress?.Report(SendPhase.Arriving);

        var answer = reply.Text.Trim();

        store.Spend.Add(Ledger.Row(conversation.Id, SpendKind.Aside, reply));

        store.Asides.Add(new AsideRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = conversation.Id,
            Sequence = await store.Messages
                .Where(m => m.ConversationId == conversation.Id && m.DeletedAtUtc == null)
                .MaxAsync(m => (long?)m.Sequence, cancellationToken)
                .ConfigureAwait(false) ?? 0,
            Question = question,
            Answer = answer,
            AskedAtUtc = DateTimeOffset.UtcNow,
            Model = reply.Model,
            Provider = reply.Provider,
            PromptTokens = reply.PromptTokens,
            CompletionTokens = reply.CompletionTokens,
            EstimatedPromptTokens = composed.Context.EstimatedTokens,
            ContextAudit = composed.Context.Describe(),
        });

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Aside answered for {Conversation}: {Audit}; reported {Prompt} in, {Completion} out.",
            conversation.Id,
            composed.Context.Describe(),
            reply.PromptTokens,
            reply.CompletionTokens);

        return new AskAnswer(
            Question: question,
            Answer: answer,
            Model: reply.Model,
            Provider: reply.Provider,
            EstimatedPromptTokens: composed.Context.EstimatedTokens,
            PromptTokens: reply.PromptTokens,
            CompletionTokens: reply.CompletionTokens,
            ContextAudit: composed.Context.Describe());
    }

    /// <summary>
    /// What has been spent, grouped by conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is applied here rather than in the query on purpose: SQLite stores a
    /// <see cref="DateTimeOffset"/> as text and this provider will not order or compare on one,
    /// which has already cost this project a crash once. The ledger is a few rows per turn, so
    /// reading it and filtering in memory is cheap and cannot be got wrong by a provider quirk.
    /// </para>
    /// <para>
    /// Whether a reply was thrown away is read from the message's tombstone at this moment, not
    /// from the ledger. A turn rerolled after the row was written is counted as discarded now,
    /// which is the only reading that stays true as the story goes on.
    /// </para>
    /// </remarks>
    /// <param name="fromUtc">Start of the window, inclusive. Null for the beginning of time.</param>
    /// <param name="toUtc">End of the window, exclusive. Null for now.</param>
    /// <param name="conversationId">One conversation, or null for all of them.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The report, dearest conversation first.</returns>
    public async Task<SpendReport> SpendAsync(
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var query = store.Spend.AsNoTracking();

        if (conversationId is not null)
        {
            query = query.Where(s => s.ConversationId == conversationId);
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var window = rows
            .Where(s => (fromUtc is null || s.AtUtc >= fromUtc) && (toUtc is null || s.AtUtc < toUtc))
            .ToList();

        if (window.Count == 0)
        {
            return new SpendReport(fromUtc, toUtc, []);
        }

        // Which of the replies paid for are no longer shown. Hidden conversations included:
        // deleting a story does not un-spend what it cost.
        var paidFor = window
            .Where(static s => s.MessageId != null)
            .Select(static s => s.MessageId!)
            .ToHashSet(StringComparer.Ordinal);

        var rolledBack = (await store.Messages
                .AsNoTracking()
                .Where(m => paidFor.Contains(m.Id) && m.DeletedAtUtc != null)
                .Select(static m => m.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var named = await store.Conversations
            .AsNoTracking()
            .Select(static c => new { c.Id, c.Name, c.Speaker })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var names = named.ToDictionary(static c => c.Id, StringComparer.Ordinal);

        var lines = window
            .GroupBy(static s => s.ConversationId, StringComparer.Ordinal)
            .Select(group =>
            {
                var discarded = group.Where(s => s.MessageId != null && rolledBack.Contains(s.MessageId)).ToList();

                return new ConversationSpend(
                    ConversationId: group.Key,
                    // A purged conversation can leave its ledger behind, so the name may be
                    // gone. Saying so beats an empty column that reads like a bug.
                    Name: names.TryGetValue(group.Key, out var row) ? row.Name : "(purged)",
                    Speaker: names.TryGetValue(group.Key, out var who) ? who.Speaker : null,
                    Calls: group.Count(),
                    Cost: group.Sum(static s => s.Cost ?? 0),
                    DiscardedCalls: discarded.Count,
                    DiscardedCost: discarded.Sum(static s => s.Cost ?? 0),
                    PromptTokens: group.Sum(static s => (long)(s.PromptTokens ?? 0)),
                    CompletionTokens: group.Sum(static s => (long)(s.CompletionTokens ?? 0)),
                    CachedTokens: group.Sum(static s => (long)(s.CachedTokens ?? 0)),
                    Unpriced: group.Count(static s => s.Cost is null),
                    ByKind:
                    [
                        .. group
                            .GroupBy(static s => s.Kind)
                            .Select(static k => new SpendByKind(k.Key, k.Count(), k.Sum(static s => s.Cost ?? 0)))
                            .OrderBy(static k => k.Kind),
                    ],
                    FirstAtUtc: group.Min(static s => s.AtUtc),
                    LastAtUtc: group.Max(static s => s.AtUtc));
            })
            .OrderByDescending(static c => c.Cost)
            .ThenBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Grouped over the same window, not per conversation: the question a host answers is
        // "should I keep using this one", which spans every story.
        var hosts = window
            .GroupBy(static s => s.Provider ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .Select(static g => new ProviderSpend(
                Provider: g.Key,
                Calls: g.Count(),
                Cost: g.Sum(static s => s.Cost ?? 0),
                PromptTokens: g.Sum(static s => (long)(s.PromptTokens ?? 0)),
                CachedTokens: g.Sum(static s => (long)(s.CachedTokens ?? 0)),
                CompletionTokens: g.Sum(static s => (long)(s.CompletionTokens ?? 0))))
            .OrderByDescending(static p => p.Cost)
            .ThenBy(static p => p.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SpendReport(fromUtc, toUtc, lines) { ByProvider = hosts };
    }

    /// <summary>The questions asked about a conversation, newest first.</summary>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The asides.</returns>
    public async Task<IReadOnlyList<AsideRecord>> AsidesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await store.Asides
            .AsNoTracking()
            .Where(a => a.ConversationId == conversationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Sorted here rather than in the query: SQLite cannot order by a DateTimeOffset.
        return [.. rows.OrderByDescending(static a => a.AskedAtUtc)];
    }

    /// <summary>
    /// Builds the prompt, calls the model and stores the reply.
    /// </summary>
    /// <param name="store">The open store.</param>
    /// <param name="conversation">The conversation being replied to.</param>
    /// <param name="pending">
    /// The message just sent, when there is one. Carried so a model failure can hand it back
    /// rather than leave the caller thinking nothing happened.
    /// </param>
    /// <param name="instruction">A one-off directive appended after the transcript.</param>
    /// <param name="progress">Receives the phase.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>The stored reply.</returns>
    private async Task<MessageRecord> ReplyAsync(
        AirpDbContext store,
        ConversationRecord conversation,
        MessageRecord? pending,
        string? instruction,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var composed = await ComposeAsync(store, conversation, instruction, cancellationToken)
            .ConfigureAwait(false);

        var context = composed.Context;
        var settings = composed.Settings;
        var meters = composed.Meters;

        progress?.Report(SendPhase.Waiting);

        ModelReply reply;

        try
        {
            var choice = ModelRouter.For(
                ModelTask.Reply,
                settings,
                temperature: composed.Sampler.Temperature,
                maxTokens: composed.Sampler.MaxTokens,
                frequencyPenalty: composed.Sampler.FrequencyPenalty);

            reply = await _model.CompleteAsync(
                context.Messages,
                // A model set on the conversation still wins: the router decides what kind of
                // work this is, not which character is played on what.
                model: conversation.Model ?? choice.Model,
                temperature: choice.Temperature,
                maxTokens: choice.MaxTokens,
                frequencyPenalty: choice.FrequencyPenalty,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The send is not undone. It is stored, it is the reader's, and telling them it
            // failed outright is how the same message gets typed a second time.
            throw new ReplyMissingException(
                $"The message was kept, but the model did not answer: {ex.Message}",
                pending is null ? [] : [pending.ToDomain()],
                ex);
        }

        progress?.Report(SendPhase.Arriving);

        var record = new MessageRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = conversation.Id,
            Sequence = await NextSequenceAsync(store, conversation.Id, cancellationToken).ConfigureAwait(false),
            Role = ChatRole.Assistant,
            Text = reply.Text.Trim(),
            SentAtUtc = DateTimeOffset.UtcNow,
            Model = reply.Model,
            Provider = reply.Provider,
            PromptTokens = reply.PromptTokens,
            CompletionTokens = reply.CompletionTokens,
            EstimatedPromptTokens = context.EstimatedTokens,
            ContextAudit = context.Describe(),
        };

        // What the model drew goes back to the store: that is what makes the number survive
        // this turn being compressed.
        var movedMeters = Trackers.Absorb(meters, record.Text, record.Sequence);

        // Written whatever becomes of the reply. A reroll a second from now hides the message
        // and leaves this row exactly where it is, which is the only way the total can still
        // agree with what was actually charged.
        store.Spend.Add(Ledger.Row(conversation.Id, SpendKind.Reply, reply, record.Id));

        store.Messages.Add(record);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (movedMeters > 0)
        {
            _logger.LogInformation("{Count} meter(s) moved.", movedMeters);
        }

        _logger.LogInformation(
            "Reply stored for {Conversation}: {Audit}; reported {Prompt} in, {Completion} out.",
            conversation.Id,
            context.Describe(),
            reply.PromptTokens,
            reply.CompletionTokens);

        return record;
    }

    /// <summary>The prompt for one call, and what was needed to build it.</summary>
    /// <param name="Context">The assembled prompt and its accounting.</param>
    /// <param name="Settings">The model options it was built against.</param>
    /// <param name="Meters">The conversation's trackers, so a reply can read moved values back.</param>
    /// <param name="Sampler">The sampler parameters the conversation's dials ask for.</param>
    private sealed record Composed(
        BuiltContext Context,
        ModelOptions Settings,
        IReadOnlyList<TrackerRecord> Meters,
        SamplerOverrides Sampler);

    /// <summary>
    /// Assembles the prompt for a call, compressing first when the transcript has outgrown
    /// the budget.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="ReplyAsync"/> so that a call which stores nothing — asking a
    /// question about the story — can be built from exactly the same layers. Sharing the
    /// assembly is not only tidiness: an aside that differs from a turn by so much as one
    /// layer would miss the provider's prefix cache and pay full price for a prompt the next
    /// real turn is about to send again.
    /// </remarks>
    /// <param name="store">The open store.</param>
    /// <param name="conversation">The conversation being spoken about.</param>
    /// <param name="instruction">The one-off directive that goes last.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>The prompt, the settings behind it, and the meters it rendered.</returns>
    private async Task<Composed> ComposeAsync(
        AirpDbContext store,
        ConversationRecord conversation,
        string? instruction,
        CancellationToken cancellationToken)
    {
        var history = await store.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.DeletedAtUtc == null)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var settings = _options.CurrentValue.Model;

        // One rule for both: text the conversation holds, then the file it names, then the
        // configured default. They are the same shape of thing and resolving them differently
        // was where this got confusing.
        var library = _library;

        var character = await TextLibrary.ResolveAsync(
                library.Characters,
                conversation.CharacterDefinition,
                conversation.CharacterName,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var persona = await TextLibrary.ResolveAsync(
                library.Personas,
                conversation.Persona,
                conversation.PersonaName,
                _options.CurrentValue.DefaultPersona,
                cancellationToken)
            .ConfigureAwait(false);

        var known = await FactExtractor.LiveAsync(store, conversation.Id, cancellationToken)
            .ConfigureAwait(false);

        var meters = await store.Trackers
            .Where(t => t.ConversationId == conversation.Id)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The dials, resolved through the pack: the conversation's stored choices where a
        // dial is enabled, the pack's default where it is disabled or untouched. One engine
        // renders the prompt half and resolves the sampler half, so the two cannot disagree
        // about what a dial is set to.
        var pack = await _dials.PackAsync(cancellationToken).ConfigureAwait(false);
        var dialValues = await DialService.ValuesAsync(store, conversation.Id, cancellationToken)
            .ConfigureAwait(false);

        var directives = DialEngine.Directives(pack, dialValues);
        var sampler = DialEngine.Sampler(pack, dialValues);

        // Resolved first, and then handed over, because the summariser has to reserve room for
        // the same layers this builds. Reading them off the conversation record instead
        // reserved nothing for a character kept in a file — which is every conversation — and
        // the summariser spent a 202-turn story believing the transcript had twice the room it
        // had. Nothing was ever compressed; the builder dropped twenty-four turns instead.
        //
        // Compress before building, not after. The builder's only tool for an over-budget
        // prompt is to drop the oldest turns, which is the forgetting this project exists to
        // avoid; summarising first means what leaves the prompt stays in the conversation.
        var prepared = await new ConversationSummariser(_model, _logger)
            .PrepareAsync(
                store: store,
                conversation: conversation,
                history: history,
                settings: settings,
                character: character,
                persona: persona,
                directives: directives,
                worldState: FactExtractor.Render(known),
                trackers: Trackers.Render(meters),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Read again, because compressing is also when facts are extracted: the turns leaving
        // the prompt are read for what they left true on their way out. The snapshot above was
        // taken to size the layer and is one extraction out of date by now, and a fact the
        // model just established has to reach this turn's prompt rather than the next one's.
        var live = await FactExtractor.LiveAsync(store, conversation.Id, cancellationToken)
            .ConfigureAwait(false);

        // The budget is a target for cost and for attention, not a limit the model imposes —
        // it sits far below the window. When compression failed, going over it is the cheaper
        // mistake: a larger bill against a character that has forgotten.
        var budget = prepared.CompressionFailed
            ? int.MaxValue
            : settings.ContextBudget;

        // Retrieval covers exactly the gap summarising leaves: a summary says what happened
        // over a stretch, and loses the wording. Only turns already compressed out are
        // candidates — the recent ones are being sent whole anyway.
        var memories = await RecallAsync(store, conversation, prepared, cancellationToken)
            .ConfigureAwait(false);

        // Named, every one of them. Three separate bugs today came from a layer being added in
        // the middle of a positional list and silently shifting the ones after it — the dials
        // arriving as the character definition, and nobody noticing until a test did.
        var context = LocalPrompt.Build(
            conversation: conversation,
            messages: prepared.Recent,
            extraInstruction: instruction,
            budget: budget,
            persona: persona,
            summaries: prepared.Summaries,
            memories: memories,
            worldState: FactExtractor.Render(live),
            trackers: Trackers.Render(meters),
            character: character,
            directives: directives);

        if (context.Dropped > 0)
        {
            _logger.LogInformation(
                "Context budget dropped {Dropped} older turn(s) from {Conversation}.",
                context.Dropped,
                conversation.Id);
        }

        return new Composed(context, settings, meters, sampler);
    }

    /// <summary>
    /// Embeds what has aged out, then finds the compressed turns that bear on the newest one.
    /// </summary>
    /// <remarks>
    /// Returns nothing at all when there is no embedding client, when nothing has been
    /// compressed yet, or when the endpoint is unreachable. Retrieval improves a prompt; it is
    /// never the reason a reader cannot get a reply.
    /// </remarks>
    private async Task<IReadOnlyList<string>> RecallAsync(
        AirpDbContext store,
        ConversationRecord conversation,
        SummarisedHistory prepared,
        CancellationToken cancellationToken)
    {
        if (_embeddings is null || prepared.Recent.Count == 0)
        {
            return [];
        }

        var compressedUpTo = prepared.Recent[0].Sequence - 1;

        if (compressedUpTo <= 0)
        {
            return [];
        }

        var query = prepared.Recent.LastOrDefault(static m => m.Role == ChatRole.User)?.Text;

        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var retriever = new MemoryRetriever(_embeddings, _logger);
        await retriever.BackfillAsync(store, conversation.Id, compressedUpTo, cancellationToken)
            .ConfigureAwait(false);

        return await retriever.RecallAsync(
            store,
            conversation.Id,
            query,
            compressedUpTo,
            conversation.Speaker,
            _options.CurrentValue.Model,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> DeleteFromAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var from = await store.Messages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == conversationId, cancellationToken)
            .ConfigureAwait(false);

        if (from is null)
        {
            return await VisibleAsync(store, conversationId, cancellationToken).ConfigureAwait(false);
        }

        var doomed = await store.Messages
            .Where(m => m.ConversationId == conversationId
                        && m.Sequence >= from.Sequence
                        && m.DeletedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var message in doomed)
        {
            message.DeletedAtUtc = now;
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Hid {Count} message(s) from {Conversation}.", doomed.Count, conversationId);

        return await VisibleAsync(store, conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        conversation.DeletedAtUtc = DateTimeOffset.UtcNow;
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What is waiting to be purged: the conversations already deleted.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>Each hidden conversation with the weight it is still carrying.</returns>
    public async Task<IReadOnlyList<PurgeCandidate>> PurgeableAsync(
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // SQLite cannot sort a DateTimeOffset, so the ordering happens once the rows are here.
        var waiting = await store.Conversations
            .AsNoTracking()
            .Where(c => c.DeletedAtUtc != null)
            .Select(c => new PurgeCandidate(
                c.Id,
                c.Name,
                c.DeletedAtUtc!.Value,
                store.Messages.Count(m => m.ConversationId == c.Id)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. waiting.OrderBy(static c => c.DeletedAtUtc)];
    }

    /// <summary>
    /// Throws away a conversation's derived memory and produces it again from the transcript.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invariant 6 says summaries, facts and embeddings are derived: dropping those tables
    /// loses nothing, because <c>Messages</c> can produce all of it again. This is that
    /// invariant being spent. It exists because the memory can be produced <em>badly</em> — by
    /// a version with a bug in it — and a story that has already been played cannot be played
    /// again to fix it.
    /// </para>
    /// <para>
    /// <b>Hand-written facts are the one exception and are kept.</b> A pinned fact was stated by
    /// a person, possibly about something the transcript never says, so it is not derived from
    /// anything and deleting it would be the only unrecoverable act available here.
    /// </para>
    /// <para>
    /// Embeddings are left alone. They are derived too, but from message text that has not
    /// changed, so the same call would buy the same vectors.
    /// </para>
    /// <para>
    /// What this cannot give back is the money the first attempt cost. The ledger is not
    /// derived and is not touched: those calls happened, and rebuilding adds its own rows
    /// beside them.
    /// </para>
    /// </remarks>
    /// <param name="conversationId">The conversation to rebuild.</param>
    /// <param name="progress">Told what is being compressed, for a long transcript.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>What the rebuild produced.</returns>
    public async Task<MemoryRebuild> RebuildMemoryAsync(
        string conversationId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        var summaries = await store.Summaries
            .Where(s => s.ConversationId == conversationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var derived = await store.Facts
            .Where(f => f.ConversationId == conversationId && !f.Pinned)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pinned = await store.Facts
            .CountAsync(f => f.ConversationId == conversationId && f.Pinned, cancellationToken)
            .ConfigureAwait(false);

        store.Summaries.RemoveRange(summaries);
        store.Facts.RemoveRange(derived);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Cleared {Summaries} summary(ies) and {Facts} extracted fact(s) from {Conversation}; "
            + "{Pinned} pinned fact(s) kept.",
            summaries.Count,
            derived.Count,
            conversationId,
            pinned);

        // Replayed through the ordinary send path rather than a second implementation of it.
        // Composing is what compresses, so calling it repeatedly works the backlog down exactly
        // as playing the conversation would have — and a rebuild that used its own rules would
        // produce a memory the application never would.
        var written = 0;

        // Every pass either compresses a stretch or finds nothing to do. The bound is the
        // transcript itself: a pass that writes no summary ends the loop, so this only stops
        // early if something is wrong, and then it says so rather than spinning.
        for (var pass = 0; pass < 200; pass++)
        {
            var before = await store.Summaries
                .CountAsync(s => s.ConversationId == conversationId, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(written == 0
                ? "Reading the transcript…"
                : $"{written} stretch(es) compressed…");

            await ComposeAsync(store, conversation, instruction: null, cancellationToken)
                .ConfigureAwait(false);

            var after = await store.Summaries
                .CountAsync(s => s.ConversationId == conversationId, cancellationToken)
                .ConfigureAwait(false);

            if (after == before)
            {
                break;
            }

            written = after;
        }

        var facts = await store.Facts
            .CountAsync(f => f.ConversationId == conversationId && !f.Pinned, cancellationToken)
            .ConfigureAwait(false);

        var covered = await store.Summaries
            .Where(s => s.ConversationId == conversationId)
            .SumAsync(s => (int?)s.MessageCount, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new MemoryRebuild(
            SummariesRemoved: summaries.Count,
            FactsRemoved: derived.Count,
            PinnedKept: pinned,
            SummariesWritten: written,
            FactsExtracted: facts,
            MessagesCovered: covered);
    }

    /// <summary>Erases the conversations already deleted, and everything they own.</summary>
    /// <remarks>
    /// Deleting a chat hides it, because messages are append-only and dropping the row would
    /// take them with it. That leaves the whole history on disk, in the clear, for a story the
    /// reader thought was gone — so this exists to finish the job when it is asked for by
    /// name. It touches nothing that is still visible, and the database is vacuumed afterwards
    /// so the pages are actually released rather than merely marked free.
    /// </remarks>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>What was erased.</returns>
    public async Task<PurgeReport> PurgeDeletedAsync(CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var doomed = await store.Conversations
            .Where(c => c.DeletedAtUtc != null)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (doomed.Count == 0)
        {
            return new PurgeReport(0, 0, 0, 0, 0, 0, default);
        }

        var messages = await store.Messages
            .Where(m => doomed.Contains(m.ConversationId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var summaries = await store.Summaries
            .Where(s => doomed.Contains(s.ConversationId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var facts = await store.Facts
            .Where(f => doomed.Contains(f.ConversationId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var trackers = await store.Trackers
            .Where(t => doomed.Contains(t.ConversationId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var asides = await store.Asides
            .Where(a => doomed.Contains(a.ConversationId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var dialChoices = await store.DialValues
            .Where(v => doomed.Contains(v.ConversationId)).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Counted, not removed. See LedgerKept: the money left the account whatever became of
        // the story, and these rows carry no text from it.
        var ledger = await store.Spend
            .AsNoTracking()
            .Where(s => doomed.Contains(s.ConversationId))
            .Select(static s => s.Cost)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var conversations = await store.Conversations
            .Where(c => doomed.Contains(c.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);

        store.Messages.RemoveRange(messages);
        store.Summaries.RemoveRange(summaries);
        store.Facts.RemoveRange(facts);
        store.Trackers.RemoveRange(trackers);
        store.Asides.RemoveRange(asides);
        store.DialValues.RemoveRange(dialChoices);
        store.Conversations.RemoveRange(conversations);

        // The one place the append-only guard is lifted, and it says so out loud.
        store.Purging = true;

        try
        {
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            store.Purging = false;
        }

        await store.Database.ExecuteSqlRawAsync("VACUUM", cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Purged {Conversations} conversation(s) and {Messages} message(s). This cannot be undone.",
            conversations.Count,
            messages.Count);

        return new PurgeReport(
            conversations.Count,
            messages.Count,
            summaries.Count,
            facts.Count,
            trackers.Count,
            asides.Count,
            new LedgerKept(ledger.Count, ledger.Sum(static c => c ?? 0)));
    }

    /// <inheritdoc />
    public async Task RenameConversationAsync(
        string conversationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        conversation.Name = name.Trim();
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Dials ────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ChatSettings> GetSettingsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        return LegacyDials.ToSettings(
            await DialService.ValuesAsync(store, conversationId, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<ChatSettings> UpdateSettingsAsync(
        string conversationId,
        ChatSettings changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        // Only what was asked for. Null means "leave it", not "clear it" — the same reading
        // the terminal's own settings screen relies on.
        foreach (var (key, value) in LegacyDials.FromSettings(changes))
        {
            await DialService.SetAsync(store, conversationId, key, value, cancellationToken)
                .ConfigureAwait(false);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return LegacyDials.ToSettings(
            await DialService.ValuesAsync(store, conversationId, cancellationToken).ConfigureAwait(false));
    }

    // ── Creation ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a conversation.
    /// </summary>
    /// <remarks>
    /// Not on any provider interface, and deliberately so. A site adapter has no such method
    /// because conversations are created on the site; here they are created by us, so this
    /// lives on the concrete type until a second local adapter ever needs it.
    /// </remarks>
    /// <param name="setup">Everything the conversation starts from.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The new conversation.</returns>
    public async Task<Chat> CreateAsync(
        NewConversation setup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentException.ThrowIfNullOrWhiteSpace(setup.Name);

        var (name, speaker, characterDefinition, opening, characterName, personaName, persona) = (
            setup.Name, setup.Speaker, setup.CharacterDefinition, setup.Opening,
            setup.CharacterName, setup.PersonaName, setup.Persona);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var conversation = new ConversationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Speaker = string.IsNullOrWhiteSpace(speaker) ? null : speaker.Trim(),
            CharacterDefinition = characterDefinition,
            CharacterName = string.IsNullOrWhiteSpace(characterName) ? null : characterName.Trim(),
            PersonaName = string.IsNullOrWhiteSpace(personaName) ? null : personaName.Trim(),
            Persona = string.IsNullOrWhiteSpace(persona) ? null : persona,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        store.Conversations.Add(conversation);

        if (!string.IsNullOrWhiteSpace(opening))
        {
            store.Messages.Add(new MessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversation.Id,
                Sequence = 1,
                Role = ChatRole.Assistant,
                Text = opening.Trim(),
                SentAtUtc = conversation.CreatedAtUtc,
            });
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new Chat
        {
            Id = conversation.Id,
            Name = conversation.Name,
            Speaker = conversation.Speaker,
            LatestMessage = opening,
            LastMessageAtUtc = conversation.CreatedAtUtc,
        };
    }

    /// <summary>
    /// Copies a conversation as far as one message, so the story can go a different way from
    /// there without losing the way it already went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that makes the original read the way it does comes along: the character and
    /// persona it names, the dials, the transcript up to the chosen turn, and the memory built
    /// from those turns — summaries, the facts that were true at that point, and the embeddings
    /// already paid for. What comes after the branch point does not exist in the copy, which is
    /// the whole point of making one.
    /// </para>
    /// <para>
    /// Two things are deliberately left behind. <c>Spend</c> is a ledger of money actually
    /// charged, one row per billed call; copying it would invent a second bill for calls that
    /// happened once. And <c>RequestHash</c> is computed over the conversation's own id, so a
    /// copied hash can never match anything the copy computes — carrying it would be a column
    /// full of values that look meaningful and are not.
    /// </para>
    /// <para>
    /// Hidden messages are not copied either. A reply that was rerolled away belongs to the
    /// original's audit, where the question "why did it say that" gets asked; the branch starts
    /// from the story as its reader can see it.
    /// </para>
    /// </remarks>
    /// <param name="conversationId">The conversation to branch from.</param>
    /// <param name="throughMessageId">
    /// The last message the copy keeps. It is included: branching on a reply means "carry on
    /// from here", not "undo this".
    /// </param>
    /// <param name="name">What to call the copy.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The new conversation.</returns>
    public async Task<Chat> BranchAsync(
        string conversationId,
        string throughMessageId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var source = await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        var through = await store.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.Id == throughMessageId && m.ConversationId == conversationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ContractException(
                "That message is not in this conversation.",
                what: throughMessageId,
                recoveryHint: "Pick the turn to branch from in the transcript and try again.");

        var point = through.Sequence;

        var branch = new ConversationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Speaker = source.Speaker,
            CharacterDefinition = source.CharacterDefinition,
            CharacterName = source.CharacterName,
            Persona = source.Persona,
            PersonaName = source.PersonaName,
            Model = source.Model,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        store.Conversations.Add(branch);

        var messages = await store.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId
                        && m.DeletedAtUtc == null
                        && m.Sequence <= point)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in messages)
        {
            store.Messages.Add(new MessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = branch.Id,
                Sequence = message.Sequence,
                Role = message.Role,
                Text = message.Text,
                SentAtUtc = message.SentAtUtc,
                Model = message.Model,
                Provider = message.Provider,
                PromptTokens = message.PromptTokens,
                CompletionTokens = message.CompletionTokens,
                EstimatedPromptTokens = message.EstimatedPromptTokens,
                ContextAudit = message.ContextAudit,

                // Carried rather than recomputed. The text is identical, so the vector is too,
                // and retrieval in the branch works from turn one instead of paying to embed
                // the same lines again.
                Embedding = message.Embedding,
            });
        }

        // Only summaries wholly inside the branch. One that straddles the point describes turns
        // the copy does not have, and would tell the model about a scene that has not happened
        // in this version of the story.
        var summaries = await store.Summaries
            .AsNoTracking()
            .Where(s => s.ConversationId == conversationId && s.ToSequence <= point)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var summary in summaries)
        {
            store.Summaries.Add(new SummaryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = branch.Id,
                FromSequence = summary.FromSequence,
                ToSequence = summary.ToSequence,
                Text = summary.Text,
                CreatedAtUtc = summary.CreatedAtUtc,
                Model = summary.Model,
                MessageCount = summary.MessageCount,
            });
        }

        // What was true *then*, which the validity range answers exactly. A fact retired after
        // the branch point was retired by turns this copy does not have, so in the branch it
        // never stopped being true — and the fact that superseded it does not exist here to
        // point at.
        var facts = await store.Facts
            .AsNoTracking()
            .Where(f => f.ConversationId == conversationId && f.ValidFromSequence <= point)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var fact in facts)
        {
            var retiredLater = fact.ValidToSequence is { } until && until > point;

            store.Facts.Add(new FactRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = branch.Id,
                Subject = fact.Subject,
                Text = fact.Text,
                ValidFromSequence = fact.ValidFromSequence,
                ValidToSequence = retiredLater ? null : fact.ValidToSequence,
                SupersededById = retiredLater ? null : fact.SupersededById,
                CreatedAtUtc = fact.CreatedAtUtc,
                Model = fact.Model,
                Pinned = fact.Pinned,
            });
        }

        // Meters come over at the value they hold now, which is the one thing here that cannot
        // be rewound: a tracker stores a number and the turn it last moved, not the number it
        // held at every turn. Branching far back therefore carries a meter forward from a scene
        // the copy has not played. It is visible in the pane and editable, which is the honest
        // answer available without keeping a history nobody asked for.
        var meters = await store.Trackers
            .AsNoTracking()
            .Where(t => t.ConversationId == conversationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var meter in meters)
        {
            store.Trackers.Add(new TrackerRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = branch.Id,
                Name = meter.Name,
                Value = meter.Value,
                Max = meter.Max,
                Delta = meter.Delta,
                Note = meter.Note,
                Means = meter.Means,
                Anchors = meter.Anchors,
                Rule = meter.Rule,
                UpdatedAtSequence = Math.Min(meter.UpdatedAtSequence, point),
                CreatedAtUtc = meter.CreatedAtUtc,
            });
        }

        // The dials come over as they stand, like the meters: a choice is configuration the
        // reader made, not something the story did, so there is nothing to rewind.
        var dialChoices = await store.DialValues
            .AsNoTracking()
            .Where(v => v.ConversationId == conversationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var choice in dialChoices)
        {
            store.DialValues.Add(new DialValueRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = branch.Id,
                Key = choice.Key,
                Value = choice.Value,
                UpdatedAtUtc = choice.UpdatedAtUtc,
            });
        }

        var asides = await store.Asides
            .AsNoTracking()
            .Where(a => a.ConversationId == conversationId && a.Sequence <= point)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var aside in asides)
        {
            store.Asides.Add(new AsideRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = branch.Id,
                Sequence = aside.Sequence,
                Question = aside.Question,
                Answer = aside.Answer,
                AskedAtUtc = aside.AskedAtUtc,
                Model = aside.Model,
                Provider = aside.Provider,
                PromptTokens = aside.PromptTokens,
                CompletionTokens = aside.CompletionTokens,
                EstimatedPromptTokens = aside.EstimatedPromptTokens,
                ContextAudit = aside.ContextAudit,
            });
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Branched {Source} at sequence {Point} into {Branch}: {Messages} message(s), "
            + "{Summaries} summary(ies), {Facts} fact(s).",
            conversationId,
            point,
            branch.Id,
            messages.Count,
            summaries.Count,
            facts.Count);

        return new Chat
        {
            Id = branch.Id,
            Name = branch.Name,
            Speaker = branch.Speaker,
            LatestMessage = messages.Count > 0 ? messages[^1].Text : null,
            LastMessageAtUtc = messages.Count > 0 ? messages[^1].SentAtUtc : branch.CreatedAtUtc,
        };
    }

    /// <summary>
    /// Brings exported transcripts into the local store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The site's own conversation identifier is kept as the local one. That is what makes a
    /// second run harmless — an already-imported conversation is recognised and left alone —
    /// and it keeps the link back to where the history came from, which no separate column
    /// would preserve as reliably.
    /// </para>
    /// <para>
    /// An export carries no character definition, because the site never put one in the
    /// transcript. Imported conversations therefore continue on the strength of their own
    /// history alone unless one is supplied here. That works better than it sounds — the model
    /// has thousands of words of the character in front of it — but an explicit definition is
    /// still worth writing.
    /// </para>
    /// </remarks>
    /// <param name="path">A transcript file, or a directory of them.</param>
    /// <param name="characterDefinition">A definition to attach to everything imported.</param>
    /// <param name="progress">Receives one line per file.</param>
    /// <param name="cancellationToken">Token used to abort the import.</param>
    /// <returns>What was written and what was left alone.</returns>
    public async Task<ImportResult> ImportAsync(
        string path,
        string? characterDefinition = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var isDirectory = Directory.Exists(path);

        // A path that is neither, and a directory holding nothing to read, fail the same way
        // for the reader: there is nothing to import. Letting the first case surface as a
        // FileNotFoundException from deep inside the loop said the same thing far less usefully.
        var files = isDirectory
            ? Directory.GetFiles(path, "*.json", SearchOption.TopDirectoryOnly).OrderBy(static f => f).ToArray()
            : File.Exists(path) ? [path] : [];

        if (files.Length == 0)
        {
            throw new ContractException(
                isDirectory ? $"No JSON files under {path}." : $"Nothing to read at {path}.",
                what: path,
                recoveryHint: "Run 'airp export' against the site first; it writes JSON by default.");
        }

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var imported = 0;
        var skipped = 0;
        var written = 0;
        var ignored = 0;

        foreach (var file in files)
        {
            var transcript = await TranscriptFile.ReadAsync(file, cancellationToken).ConfigureAwait(false);

            if (transcript?.ConversationId is not { Length: > 0 } id)
            {
                ignored++;
                progress?.Report($"{Path.GetFileName(file)}: not a transcript, ignored");
                continue;
            }

            if (await store.Conversations.AnyAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false))
            {
                skipped++;
                progress?.Report($"{transcript.Title}: already imported");
                continue;
            }

            store.Conversations.Add(new ConversationRecord
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(transcript.Title) ? "Imported" : transcript.Title,
                Speaker = transcript.Speaker,
                CharacterDefinition = characterDefinition,
                CreatedAtUtc = transcript.StartedAtUtc ?? DateTimeOffset.UtcNow,
            });

            var sequence = 0L;

            foreach (var message in transcript.Messages.OrderBy(static m => m.Index))
            {
                if (string.IsNullOrEmpty(message.Text))
                {
                    continue;
                }

                store.Messages.Add(new MessageRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConversationId = id,
                    // Renumbered rather than trusting the export's Index: a transcript that
                    // skipped or repeated one would collide with the unique index and take the
                    // whole import down with it.
                    Sequence = ++sequence,
                    Role = TranscriptFile.ParseRole(message.Role),
                    Text = message.Text,
                    SentAtUtc = message.SentAtUtc ?? transcript.StartedAtUtc ?? DateTimeOffset.UtcNow,
                });

                written++;
            }

            imported++;
            progress?.Report($"{transcript.Title}: {sequence} message(s)");
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Imported {Imported} conversation(s), {Written} message(s); {Skipped} already present.",
            imported,
            written,
            skipped);

        return new ImportResult(imported, skipped, written, ignored);
    }

    /// <summary>
    /// What went into each reply of a conversation, and what it cost.
    /// </summary>
    /// <remarks>
    /// The point of invariant 4, made reachable. A memory layer that cannot be inspected is a
    /// black box with extra steps: when a character says something odd the only useful question
    /// is what it was actually shown, and that is answerable only because it was recorded at the
    /// moment the prompt was assembled.
    /// </remarks>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>One entry per reply, oldest first.</returns>
    public async Task<IReadOnlyList<TurnAudit>> AuditAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var replies = await store.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role == ChatRole.Assistant)
            .OrderBy(m => m.Sequence)
            .Select(m => new TurnAudit(
                m.Sequence,
                m.SentAtUtc,
                m.DeletedAtUtc != null,
                m.Model,
                m.Provider,
                m.EstimatedPromptTokens,
                m.PromptTokens,
                m.CompletionTokens,
                m.ContextAudit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return replies;
    }

    /// <summary>
    /// States a fact by hand, outside anything the transcript said.
    /// </summary>
    /// <remarks>
    /// What a pinned memory is for, and the piece extraction cannot supply: something true that
    /// has simply never come up, or a correction to something the extractor read wrongly. Pinned,
    /// so the model cannot retire it — a reader who states a fact outright has not asked for a
    /// second opinion.
    /// </remarks>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="subject">Who or what it is about.</param>
    /// <param name="text">The fact, in one sentence.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The stored fact.</returns>
    public async Task<FactRecord> AddFactAsync(
        string conversationId,
        string subject,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        var fact = new FactRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = conversationId,
            Subject = subject.Trim(),
            Text = text.Trim(),
            ValidFromSequence = await NextSequenceAsync(store, conversationId, cancellationToken)
                .ConfigureAwait(false) - 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Pinned = true,
        };

        store.Facts.Add(fact);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return fact;
    }

    /// <summary>
    /// Marks a fact as no longer true, by hand.
    /// </summary>
    /// <remarks>
    /// It stops being sent; it does not stop existing. That the story once held it is part of
    /// the story, and the audit still shows it with the point it was retired.
    /// </remarks>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="idPrefix">Enough of the fact's identifier to be unambiguous.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The fact retired, or null when the prefix matched nothing live.</returns>
    public async Task<FactRecord?> RetireFactAsync(
        string conversationId,
        string idPrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPrefix);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var live = await FactExtractor.LiveAsync(store, conversationId, cancellationToken)
            .ConfigureAwait(false);

        var matches = live
            .Where(f => f.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // An ambiguous prefix retires nothing rather than the first thing it happens to hit.
        if (matches.Length != 1)
        {
            return null;
        }

        matches[0].ValidToSequence =
            await NextSequenceAsync(store, conversationId, cancellationToken).ConfigureAwait(false) - 1;

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return matches[0];
    }

    /// <summary>Every fact of a conversation, live and retired, oldest first.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The facts.</returns>
    public async Task<IReadOnlyList<FactRecord>> FactsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await store.Facts
            .AsNoTracking()
            .Where(f => f.ConversationId == conversationId)
            .OrderBy(f => f.ValidFromSequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The meters this story keeps, by name.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The meters.</returns>
    public async Task<IReadOnlyList<TrackerRecord>> TrackersAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await store.Trackers
            .AsNoTracking()
            .Where(t => t.ConversationId == conversationId)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a meter to a story, or moves one that is already there.
    /// </summary>
    /// <remarks>
    /// Nothing about what a meter means is fixed here. A heist wants SUSPICION and a marriage
    /// wants RESENTMENT, and a schema that guessed between them would be wrong for both.
    /// </remarks>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="name">What the meter is called; it is what the model renders.</param>
    /// <param name="value">Where it starts.</param>
    /// <param name="max">The top of the scale.</param>
    /// <param name="means">What it measures and what moves it, or null to leave it.</param>
    /// <param name="anchors">What points on the scale mean, or null to leave them.</param>
    /// <param name="rule">A rule the model must apply to it, or null.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The stored meter.</returns>
    public async Task<TrackerRecord> SetTrackerAsync(
        string conversationId,
        string name,
        double? value = null,
        double? max = null,
        string? means = null,
        string? anchors = null,
        string? rule = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        var trimmed = name.Trim();

        var tracker = await store.Trackers
            .FirstOrDefaultAsync(t => t.ConversationId == conversationId && t.Name == trimmed, cancellationToken)
            .ConfigureAwait(false);

        if (tracker is null)
        {
            tracker = new TrackerRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversationId,
                Name = trimmed,
                Max = max ?? 100,
                Value = value ?? 0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            store.Trackers.Add(tracker);
        }
        else
        {
            // Only what was asked for: a caller setting a rule is not also resetting the value.
            tracker.Max = max ?? tracker.Max;
            tracker.Value = value ?? tracker.Value;
        }

        // Null leaves a field alone; empty clears it. That distinction is what lets a caller
        // set the anchors later without also wiping what the meter measures.
        if (means is not null)
        {
            tracker.Means = string.IsNullOrWhiteSpace(means) ? null : means.Trim();
        }

        if (anchors is not null)
        {
            tracker.Anchors = string.IsNullOrWhiteSpace(anchors) ? null : anchors.Trim();
        }

        if (rule is not null)
        {
            tracker.Rule = string.IsNullOrWhiteSpace(rule) ? null : rule.Trim();
        }

        tracker.Value = Math.Clamp(tracker.Value, 0, tracker.Max);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return tracker;
    }

    /// <summary>
    /// Removes a meter from a story.
    /// </summary>
    /// <remarks>
    /// Genuinely removed, unlike a message or a fact. A meter is configuration the reader wrote,
    /// not something the story said, so there is no history in it worth keeping.
    /// </remarks>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>Whether one was removed.</returns>
    public async Task<bool> RemoveTrackerAsync(
        string conversationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var tracker = await store.Trackers
            .FirstOrDefaultAsync(
                t => t.ConversationId == conversationId && t.Name == name.Trim(),
                cancellationToken)
            .ConfigureAwait(false);

        if (tracker is null)
        {
            return false;
        }

        store.Trackers.Remove(tracker);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Turns inner thoughts on or off for a conversation.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="on">Whether characters should show what they withhold.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The setting as it now stands.</returns>
    public async Task<bool> SetInnerThoughtsAsync(
        string conversationId,
        bool on,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireAsync(store, conversationId, cancellationToken).ConfigureAwait(false);

        await DialService.SetAsync(
                store,
                conversationId,
                LegacyDials.InnerThoughts,
                on ? "true" : "false",
                cancellationToken)
            .ConfigureAwait(false);

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return on;
    }

    /// <summary>The summaries held for a conversation, oldest first.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The summaries.</returns>
    public async Task<IReadOnlyList<SummaryRecord>> SummariesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await store.Summaries
            .AsNoTracking()
            .Where(s => s.ConversationId == conversationId)
            .OrderBy(s => s.FromSequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static async Task<ConversationRecord> RequireAsync(
        AirpDbContext store,
        string conversationId,
        CancellationToken cancellationToken)
        => await store.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
               .ConfigureAwait(false)
           ?? throw new ContractException(
               "That conversation is not in the local store.",
               what: conversationId,
               recoveryHint: "Run 'airp new' to start one, or switch to another provider.");

    /// <summary>
    /// The next position in a conversation.
    /// </summary>
    /// <remarks>
    /// Counted from the highest sequence ever used, including hidden rows. Reusing the number
    /// of a hidden message would collide with it in the unique index, and the row it collided
    /// with is one nobody can see.
    /// </remarks>
    private static async Task<long> NextSequenceAsync(
        AirpDbContext store,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var highest = await store.Messages
            .Where(m => m.ConversationId == conversationId)
            .MaxAsync(m => (long?)m.Sequence, cancellationToken)
            .ConfigureAwait(false);

        return (highest ?? 0) + 1;
    }

    private static string Hash(string conversationId, long sequence, string text, string? instruction = null)
    {
        // Appended only when there is one, so a plain send hashes exactly as it always has.
        // Changing that would give every unanswered message in an existing database a new
        // hash, and the retry that found it would store the sentence a second time.
        // A control character rather than a space or a pipe. Those appear in prose, and a
        // separator the message can contain is one that lets two different sends hash
        // alike when the words happen to fall either side of it.
        const string separator = "\u001f";
        var directed = string.IsNullOrWhiteSpace(instruction) ? text : text + separator + instruction.Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{conversationId}|{sequence}|{directed}"));
        return Convert.ToHexString(bytes)[..32];
    }
}
