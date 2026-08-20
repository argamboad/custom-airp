using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;
using Airp.Application.Context;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// Keeps track of what the conversation has established, and of what has stopped being true.
/// </summary>
/// <remarks>
/// <para>
/// Runs on the same stretch the summariser compresses, and for the same reason: that is where a
/// model call is already being made, and it is the moment turns stop being visible to the model
/// in their own words. The summary says what happened over that stretch; the facts say what it
/// left true.
/// </para>
/// <para>
/// The interesting half is not extracting facts but retiring them. A world state that only
/// accumulates is worse than none: it ends up asserting both that she distrusts you and that she
/// trusts you, and the model picks whichever it read last.
/// </para>
/// </remarks>
internal sealed class FactExtractor
{
    private const string Instruction =
        """
        You are maintaining the world state of a roleplay conversation. You are given the facts
        currently believed to be true, and a new stretch of transcript.

        Reply with JSON only, in this shape:
        {
          "facts": [ { "subject": "...", "text": "..." } ],
          "retired": [ "<id of a fact that is no longer true>" ]
        }

        Rules:
        - "facts" holds only what the new transcript establishes and the existing list does not
          already say. Say nothing twice.
        - "subject" is who or what it is about: a character's name, a place, or a pair such as
          "Elena and Marcus". Always a name. Never "User", "the user" or "the reader" — every
          person in the transcript is named there, and the story does not know who "the user"
          is.
        - "text" is one plain sentence, in the language of the transcript, stating something
          durable: a trait, a possession, an injury, a commitment, a standing between people.
        - Do not record what merely happened; that is the summary's job. Record what it left true.
        - "retired" lists the ids of existing facts the new transcript has made false. A change
          of heart, a wound that healed, a promise broken or kept. Be conservative: retire a fact
          only when the transcript actually contradicts it.
        - If there is nothing to add and nothing to retire, reply with empty arrays.
        """;

    private readonly ILanguageModelClient _model;
    private readonly ILogger _logger;

    /// <summary>Initialises the extractor.</summary>
    /// <param name="model">The model that does the extracting.</param>
    /// <param name="logger">Logger. Never receives message text.</param>
    public FactExtractor(ILanguageModelClient model, ILogger logger)
    {
        _model = model;
        _logger = logger;
    }

    /// <summary>The facts currently true for a conversation, oldest first.</summary>
    /// <param name="store">The open store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The live facts.</returns>
    public static Task<List<FactRecord>> LiveAsync(
        AirpDbContext store,
        string conversationId,
        CancellationToken cancellationToken)
        => store.Facts
            .Where(f => f.ConversationId == conversationId && f.ValidToSequence == null)
            .OrderBy(f => f.ValidFromSequence)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Renders the live facts as the world-state layer of a prompt.
    /// </summary>
    /// <param name="facts">The live facts.</param>
    /// <returns>The text, or null when there is nothing to say.</returns>
    public static string? Render(IReadOnlyList<FactRecord> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Count == 0)
        {
            return null;
        }

        var lines = facts
            .GroupBy(static f => f.Subject, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}: {string.Join(" ", g.Select(static f => f.Text.TrimEnd('.') + "."))}");

        return "What is true in this story right now:\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// Reads a stretch of transcript, records what it established, and retires what it undid.
    /// </summary>
    /// <param name="store">The open store.</param>
    /// <param name="conversation">The conversation.</param>
    /// <param name="messages">The stretch being compressed.</param>
    /// <param name="settings">Model settings.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>How many facts were added and how many retired.</returns>
    public async Task<(int Added, int Retired)> UpdateAsync(
        AirpDbContext store,
        ConversationRecord conversation,
        IReadOnlyList<MessageRecord> messages,
        ModelOptions settings,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return (0, 0);
        }

        var live = await LiveAsync(store, conversation.Id, cancellationToken).ConfigureAwait(false);

        var known = live.Count == 0
            ? "(none yet)"
            : string.Join("\n", live.Select(f => $"{f.Id[..8]} | {f.Subject} | {f.Text}"));

        var transcript = Transcript.Render(conversation, messages);

        var choice = ModelRouter.For(ModelTask.Summary, settings);
        JsonNode? parsed;

        try
        {
            var reply = await _model.CompleteAsync(
                [
                    new ModelMessage(ModelRole.System, Instruction),
                    new ModelMessage(ModelRole.User, $"Existing facts:\n{known}\n\nNew transcript:\n{transcript}"),
                ],
                choice.Model,
                choice.Temperature,
                choice.MaxTokens,
                cancellationToken).ConfigureAwait(false);

            // Same reasoning as the summariser: it runs on its own, and an extraction that
            // came back unparseable cost what a good one would have.
            store.Spend.Add(Ledger.Row(conversation.Id, SpendKind.Facts, reply));

            parsed = Parse(reply.Text);

            if (parsed is null)
            {
                _logger.LogWarning("Fact extraction returned something that is not the agreed JSON.");
                return (0, 0);
            }

            return await ApplyAsync(store, conversation, messages, parsed, reply.Model, live, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // World state is an improvement on the prompt, never a precondition for a reply.
            _logger.LogWarning(ex, "Fact extraction failed; the world state is unchanged this turn.");
            return (0, 0);
        }
    }

    /// <summary>
    /// Pulls the JSON out of a reply that may have wrapped it in prose or a code fence.
    /// </summary>
    /// <remarks>
    /// Models asked for JSON often supply it inside a fence or after a sentence of
    /// introduction. Refusing those would throw away a correct answer over its packaging.
    /// </remarks>
    private static JsonNode? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(int Added, int Retired)> ApplyAsync(
        AirpDbContext store,
        ConversationRecord conversation,
        IReadOnlyList<MessageRecord> messages,
        JsonNode parsed,
        string? model,
        List<FactRecord> live,
        CancellationToken cancellationToken)
    {
        var at = messages[^1].Sequence;
        var added = 0;

        foreach (var item in parsed["facts"]?.AsArray() ?? [])
        {
            var subject = item?["subject"]?.GetValue<string>()?.Trim();
            var text = item?["text"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            store.Facts.Add(new FactRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversation.Id,
                Subject = subject,
                Text = text,
                ValidFromSequence = messages[0].Sequence,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Model = model,
            });

            added++;
        }

        var retired = 0;

        foreach (var node in parsed["retired"]?.AsArray() ?? [])
        {
            if (node?.GetValue<string>()?.Trim() is not { Length: > 0 } prefix)
            {
                continue;
            }

            // Matched on the short prefix the model was shown. Asking it to echo a 32-character
            // identifier exactly is asking for a transcription error to retire the wrong fact.
            var target = live.FirstOrDefault(f => f.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            // A pinned fact was stated by a person. The model does not get to decide it stopped
            // being true — that is the difference between a fact the reader controls and one
            // they merely suggested.
            if (target is null || target.ValidToSequence is not null || target.Pinned)
            {
                continue;
            }

            target.ValidToSequence = at;
            retired++;
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (added, retired);
    }
}
