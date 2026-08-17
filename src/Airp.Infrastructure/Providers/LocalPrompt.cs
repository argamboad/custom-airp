using System.Text;
using Airp.Application.Abstractions;
using Airp.Application.Context;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// Turns a stored conversation into the message list sent to the model.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Order is load-bearing.</strong> The sections are laid out from least to most
/// volatile, and everything before the first thing that changes between turns survives the
/// provider's prefix cache while everything after it is reprocessed from scratch. Measured
/// both ways: with a local model that is the difference between seconds and minutes per turn,
/// and with a paid API it is the difference between $0.0028 and $0.14 per million input
/// tokens.
/// </para>
/// <para>
/// Today the layout is: character definition, then the dials, then the whole transcript. When
/// retrieval arrives it goes <em>after</em> the transcript, not in the middle where it would
/// read more naturally — putting it there would invalidate the cache on every single turn.
/// </para>
/// </remarks>
internal static class LocalPrompt
{
    /// <summary>Builds the turns to send.</summary>
    /// <param name="conversation">The conversation, with its definition and dials.</param>
    /// <param name="messages">The transcript, oldest first, already filtered of hidden turns.</param>
    /// <param name="extraInstruction">
    /// A one-off directive for this call, such as a regenerate reason. Goes last, because it
    /// is the most volatile thing in the prompt.
    /// </param>
    /// <param name="budget">The token ceiling for the whole prompt.</param>
    /// <returns>The prompt and the accounting that produced it.</returns>
    public static BuiltContext Build(
        ConversationRecord conversation,
        IReadOnlyList<MessageRecord> messages,
        string? extraInstruction,
        int budget,
        string? persona = null,
        IReadOnlyList<string>? summaries = null,
        IReadOnlyList<string>? memories = null,
        string? worldState = null,
        string? directives = null,
        string? character = null,
        string? trackers = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(messages);

        var history = messages.Select(static m => new ModelMessage(
            m.Role switch
            {
                ChatRole.Assistant => ModelRole.Assistant,
                ChatRole.System => ModelRole.System,
                _ => ModelRole.User,
            },
            m.Text)).ToArray();

        return ContextBuilder.Build(
            character ?? conversation.CharacterDefinition,
            persona,
            directives,
            worldState,
            summaries,
            history,
            memories,
            trackers,
            extraInstruction,
            budget);
    }

    /// <summary>
    /// The instruction that makes characters show what they are not saying.
    /// </summary>
    /// <remarks>
    /// Placed with the dials rather than with the meters: it changes when the reader toggles it
    /// and not otherwise, so it belongs in the part of the prompt a provider can cache.
    /// </remarks>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The directive, or an empty string when the setting is off.</returns>
    public static string InnerThoughtsDirective(ConversationRecord conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (!conversation.InnerThoughts)
        {
            return string.Empty;
        }

        return """


            After each character speaks and acts, give one line of what they did not say, in
            this shape:

            >NAME's inner thoughts: what they are actually thinking

            One line, first person, and never for the user. It is for what the character is
            keeping back — if it only repeats what they said out loud, leave it out.
            """;
    }

    /// <summary>
    /// Maps the creativity dial onto a sampling temperature.
    /// </summary>
    /// <param name="creativity">The level, or null when unset.</param>
    /// <param name="fallback">The configured temperature, used when the dial is unset.</param>
    /// <returns>A temperature.</returns>
    public static double Temperature(int? creativity, double fallback) => creativity switch
    {
        0 => 0.6,
        1 => 0.8,
        2 => 1.0,
        3 => 1.2,
        4 => 1.4,
        _ => fallback,
    };

    /// <summary>
    /// Maps the response-length dial onto a token ceiling.
    /// </summary>
    /// <param name="length">The level, or null when unset.</param>
    /// <param name="fallback">The configured ceiling, used when the dial is unset.</param>
    /// <returns>A token ceiling.</returns>
    public static int MaxTokens(int? length, int fallback) => length switch
    {
        0 => 200,
        1 => 450,
        2 => 900,
        3 => 1600,
        4 => 2600,
        _ => fallback,
    };

    /// <summary>
    /// Turns a regenerate reason into an instruction for the next attempt.
    /// </summary>
    /// <remarks>
    /// The reasons came from ourdream's list, but the complaints are not theirs — "it wrote my
    /// actions for me" and "it looped" are what goes wrong with any roleplay model. On the site
    /// the reason was a value posted to an endpoint; here it is the only thing that makes the
    /// second attempt differ from the first, so it has to say something the model can act on.
    /// </remarks>
    /// <param name="reason">Why the reply is being asked for again.</param>
    /// <param name="instructions">The reader's own guidance, when they gave any.</param>
    /// <returns>The directive, or an empty string when there is nothing to say.</returns>
    public static string RegenerateDirective(RegenerateReason reason, string? instructions)
    {
        var directive = reason switch
        {
            RegenerateReason.Steer => "Write the previous reply differently.",
            RegenerateReason.BadMemory =>
                "The previous reply contradicted something established earlier in this conversation. "
                + "Re-read the history and stay consistent with it.",
            RegenerateReason.Looping =>
                "The previous reply repeated itself or left the scene where it was. Move it forward.",
            RegenerateReason.ActingForUser =>
                "The previous reply wrote the user's actions or words. Never do that: write only "
                + "your own character.",
            RegenerateReason.TooShort => "The previous reply was too short. Write more.",
            RegenerateReason.TooLong => "The previous reply was too long. Be brief.",
            RegenerateReason.WrongFormat =>
                "The previous reply was formatted wrongly. Keep prose, dialogue and emphasis "
                + "consistent with the earlier replies.",
            RegenerateReason.Refusing =>
                "The previous reply declined to continue. Stay in character and carry on with the scene.",
            _ => "Write the previous reply again, differently.",
        };

        return string.IsNullOrWhiteSpace(instructions)
            ? directive
            : $"{directive} {instructions.Trim()}";
    }
}
