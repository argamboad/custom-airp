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
    /// <para>
    /// The reasons came from ourdream's list, but the complaints are not theirs — "it wrote my
    /// actions for me" and "it looped" are what goes wrong with any roleplay model. On the site
    /// the reason was a value posted to an endpoint; here it is the only thing that makes the
    /// second attempt differ from the first, so it has to say something the model can act on.
    /// </para>
    /// <para>
    /// <strong>It is framed, not sent bare.</strong> A trailing imperative with nothing around
    /// it — <c>Use at least 30 words.</c> — is read by some models as the latest thing said
    /// rather than as a note about what to write, and the reply that comes back is the note
    /// repeated. Saying what the text is, and that the answer must be the scene rather than a
    /// response to it, is what stops that. The same lesson as the carry-on directive, which
    /// used to return nothing at all for want of a frame.
    /// </para>
    /// <para>
    /// The reader's own words go on their own line under a label. Run together with the canned
    /// reason they read as one more sentence of prose to reply to; set apart they read as a
    /// constraint, which is what they are.
    /// </para>
    /// <para>
    /// Nothing here refers to the previous attempt's wording, because it is hidden from the
    /// prompt before the call and the model cannot see it. Telling a model to write something
    /// "differently" from text it was never shown is asking it to guess what it is avoiding.
    /// The rule is enforced as written: no directive may contain the phrase "previous reply",
    /// which is why the frame says "your last reply" and every reason says "the last attempt".
    /// </para>
    /// </remarks>
    /// <param name="reason">Why the reply is being asked for again.</param>
    /// <param name="instructions">The reader's own guidance, when they gave any.</param>
    /// <returns>The directive, or an empty string when there is nothing to say.</returns>
    public static string RegenerateDirective(RegenerateReason reason, string? instructions)
    {
        var directive = reason switch
        {
            RegenerateReason.Steer => "Take the scene somewhere other than where it was going.",
            RegenerateReason.BadMemory =>
                "The last attempt contradicted something the story had already established. "
                + "Re-read the history and keep this one consistent with it.",
            RegenerateReason.Looping =>
                "The last attempt repeated itself and left the scene where it was. Move this one "
                + "forward.",
            RegenerateReason.ActingForUser =>
                "The last attempt wrote the user's words and actions. Write only your own "
                + "character, and leave the user theirs.",
            RegenerateReason.TooShort => "The last attempt was too short. Give this one room.",
            RegenerateReason.TooLong => "The last attempt ran long. Keep this one tight.",
            RegenerateReason.WrongFormat =>
                "The last attempt broke the formatting. Match the prose, dialogue and emphasis "
                + "of the earlier replies.",
            RegenerateReason.Refusing =>
                "The last attempt declined to continue. Stay in character and carry the scene on.",
            _ => "Write this turn afresh; do not settle for the obvious version of it.",
        };

        var note = string.IsNullOrWhiteSpace(instructions)
            ? directive
            : directive + "\n\n" + "Also, from the reader: " + instructions.Trim();

        return "Your last reply has been withdrawn and is no longer part of the scene. "
            + "Write that turn again from the same point, taking the note below into account.\n\n"
            + "The note is a direction about how to write, not something anyone said and not "
            + "something to answer. Your reply is the scene itself, in your own voice as the "
            + "character — never repeat, quote, summarise or acknowledge the note, and never "
            + "write the user's words, actions or thoughts.\n\n"
            + note;
    }

    /// <summary>
    /// Frames a question about the story so it is answered by the author rather than acted out
    /// by the character.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every layer above this one has spent its words telling the model to <em>be</em> someone.
    /// Asking a question inside that frame gets the question answered in character, in prose,
    /// as a beat of the scene — which is a reply the reader did not want and cannot use. So the
    /// directive has to break the frame explicitly, and it is last in the prompt, where it is
    /// read most recently.
    /// </para>
    /// <para>
    /// The instruction not to invent is the important half. A model asked how far the rehearsal
    /// room is will answer confidently rather than say the story never mentions it, and because
    /// this answer is stored nowhere, that invention vanishes the moment the pane closes —
    /// leaving the reader playing on a detail the next turn has never heard of. Saying why it
    /// matters, rather than only that it does, is what makes it hold.
    /// </para>
    /// </remarks>
    /// <param name="question">The reader's question, verbatim.</param>
    /// <returns>The directive.</returns>
    public static string AskDirective(string question)
        => "Step out of the scene. This is a question from the reader about the story, put to "
        + "you as its author, and it is not a turn: nothing here is said aloud by anyone and "
        + "nothing that follows happens.\n\n"
        + "Answer in your own voice, briefly and plainly. No prose, no dialogue, no action, no "
        + "staying in character, and do not move the story forward by so much as a moment.\n\n"
        + "Answer only from what you have been given above. Where it does not say, say that it "
        + "does not say. Anything you make up here is written down nowhere and will be gone the "
        + "moment this is read, so an invented detail becomes something the reader believes and "
        + "the story then contradicts.\n\n"
        + "The question: " + question.Trim();
}
